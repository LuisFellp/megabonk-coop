using System;
using System.Collections.Generic;
using Assets.Scripts.Actors.Player;
using TMPro;
using UnityEngine;

namespace MegabonkCoop
{
    // Native in-run HUD: status chip, the friend's marker (name, distance, HP bar, edge arrow) and map markers ("pings", key G).
    public partial class CoopBehaviour
    {
        bool hudBuilt, hudErr;
        NUi.El statBg, statTx, chipBg, chipTx, fmName, fmBarBg, fmBarFill, fmArrow, fmEdgeName;
        NUi.El[] pmArrow = new NUi.El[3], pmText = new NUi.El[3];

        public struct PingM { public Vector3 pos; public float until; public bool mine; }
        readonly List<PingM> pings = new List<PingM>();
        float nextPingSend; int pingsSent, pingsRecv;

        public bool NativeHudUp => hudBuilt && !hudErr;

        void BuildHud()
        {
            chipBg = NUi.Box(null, 18, 1080 - 62, 560, 44, new Color(0.05f, 0.05f, 0.09f, 0.78f), false);
            chipTx = NUi.Text(chipBg, 14, 0, 540, 44, "", 24, Color.white);
            statBg = NUi.Box(null, 600, 1080 - 62, 760, 44, new Color(0.05f, 0.05f, 0.09f, 0.78f), false);
            statTx = NUi.Text(statBg, 14, 0, 740, 44, "", 22, new Color(1f, 0.92f, 0.6f));
            fmName = NUi.Text(null, 0, 0, 260, 30, "", 26, new Color(0.45f, 0.9f, 1f), TextAlignmentOptions.Center);
            fmBarBg = NUi.Box(null, 0, 0, 130, 14, new Color(0.25f, 0.05f, 0.05f, 0.95f), false);
            fmBarFill = NUi.Box(fmBarBg, 0, 0, 130, 14, Color.green, false);
            fmArrow = NUi.Arrow(null, 30, new Color(0.45f, 0.9f, 1f));
            fmEdgeName = NUi.Text(null, 0, 0, 260, 30, "", 24, new Color(0.45f, 0.9f, 1f), TextAlignmentOptions.Center);
            for (int i = 0; i < pmArrow.Length; i++)
            {
                pmArrow[i] = NUi.Arrow(null, 34, new Color(1f, 0.85f, 0.2f));
                pmText[i] = NUi.Text(null, 0, 0, 200, 28, "", 24, new Color(1f, 0.9f, 0.3f), TextAlignmentOptions.Center);
            }
            hudBuilt = true;
            Plugin.Info("[nui] native HUD built");
        }

        static void Place(NUi.El e, float cx, float cy) { e.SetPos(cx - e.w / 2f, cy - e.h / 2f); }

        // world position -> reference-space screen position (mirrored when behind the camera, like the IMGUI code did)
        static void Project(Camera cam, Vector3 world, out Vector2 r, out bool inFront)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            inFront = sp.z > 0f;
            float sx = sp.x, sy = sp.y;
            if (!inFront) { sx = Screen.width - sx; sy = Screen.height - sy; }
            r = NUi.ScreenToRef(new Vector2(sx, sy));
        }

        // keeps the marker on screen: returns the clamped point and the arrow angle (0 = up, clockwise) when it is off screen
        static bool EdgeClamp(Vector2 r, bool inFront, float W, float H, float m, out Vector2 p, out float ang)
        {
            bool on = inFront && r.x > m && r.x < W - m && r.y > m && r.y < H - m;
            p = r; ang = 180f;
            if (on) return true;
            Vector2 c = new Vector2(W / 2f, H / 2f);
            Vector2 dir = r - c;
            if (dir.sqrMagnitude < 1f) dir = new Vector2(0, -1);
            float tx = Mathf.Abs(dir.x) > 0.01f ? (W / 2f - m) / Mathf.Abs(dir.x) : float.MaxValue;
            float ty = Mathf.Abs(dir.y) > 0.01f ? (H / 2f - m) / Mathf.Abs(dir.y) : float.MaxValue;
            p = c + dir * Mathf.Min(tx, ty);
            ang = Mathf.Atan2(dir.x, -dir.y) * Mathf.Rad2Deg;
            return false;
        }

        void NativeHudTick()
        {
            if (hudErr) return;
            if (!hudBuilt) BuildHud();
            float now = Time.realtimeSinceStartup;
            var me = MyPlayer.Instance;
            bool inRun = me != null && lastScene != "MainMenu";
            bool live = (net.Active || selfTest) && inRun;

            // ---- status chip
            chipBg.Visible = live;
            if (live)
            {
                var blue = new Color(0.45f, 0.9f, 1f);
                string status = selfTest ? "COOP: TESTE (F6) - amigo simulado" : (haveRemote && remoteDead) ? "COOP: seu amigo morreu" : !haveRemote
                    ? (Coop.IsHost ? "COOP: aguardando amigo..." : "COOP: conectando ao host...")
                    : Coop.PeerLost ? "COOP: amigo sumiu" : stalled ? "COOP: sem sinal do amigo!" : Coop.Synced ? $"COOP: SINCRONIZADO  {(rtt < 0 ? "?" : rtt.ToString("F0"))} ms" : $"COOP: mapa nao confirmado  {(rtt < 0 ? "?" : rtt.ToString("F0"))} ms";
                chipTx.SetText(status);
                chipTx.SetColor(selfTest ? new Color(1f, 0.85f, 0.3f) : haveRemote && !stalled && Coop.Synced ? new Color(0.45f, 1f, 0.55f) : haveRemote && !stalled ? blue : new Color(1f, 0.65f, 0.25f));
            }

            statBg.Visible = live && net.Active && CoopStats.Any;
            if (statBg.Visible) statTx.SetText(CoopStats.Line());

            // ---- friend marker + pings
            var cam = live ? GetCam() : null;
            bool canAct = !Coop.WindowOpen;
            bool showFriend = live && canAct && cam != null && haveRemote && remoteInRun && !remoteDead;
            fmName.Visible = fmBarBg.Visible = fmArrow.Visible = fmEdgeName.Visible = false;
            float W = NUi.RefW, H = NUi.RefH;
            if (showFriend)
            {
                Project(cam, ghostTarget + Vector3.up * 1.0f, out Vector2 r, out bool front);
                float dist = Vector3.Distance(me.transform.position, ghostTarget);
                string txt = $"{Coop.FriendLabel}  {dist:F0}m";
                float frac = remoteMaxHp > 0 ? Mathf.Clamp01(remoteHp / (float)remoteMaxHp) : 1f;
                bool on = EdgeClamp(r, front, W, H, 60f, out Vector2 p, out float ang);
                fmBarFill.SetSize(130f * frac, 14f); fmBarFill.SetBg(Color.Lerp(new Color(0.95f, 0.2f, 0.2f), new Color(0.3f, 0.95f, 0.35f), frac));
                if (on)
                {
                    fmName.Visible = fmBarBg.Visible = fmArrow.Visible = true;
                    fmName.SetText(txt); fmName.SetPos(p.x - fmName.w / 2f, p.y - 112f);
                    fmBarBg.SetPos(p.x - 65f, p.y - 78f);
                    Place(fmArrow, p.x, p.y - 40f); fmArrow.SetRot(180f);
                }
                else
                {
                    fmArrow.Visible = fmEdgeName.Visible = fmBarBg.Visible = true;
                    Place(fmArrow, p.x, p.y); fmArrow.SetRot(ang);
                    Vector2 inward = (new Vector2(W / 2f, H / 2f) - p).normalized * 52f;
                    fmEdgeName.SetText(txt); fmEdgeName.SetPos(p.x + inward.x - fmEdgeName.w / 2f, p.y + inward.y - 15f);
                    fmBarBg.SetPos(p.x + inward.x - 65f, p.y + inward.y + 18f);
                }
            }

            NativeChatTick();
            pings.RemoveAll(x => x.until < now);
            for (int i = 0; i < pmArrow.Length; i++)
            {
                bool has = live && canAct && cam != null && i < pings.Count;
                pmArrow[i].Visible = has; pmText[i].Visible = has;
                if (!has) continue;
                var pg = pings[i];
                Project(cam, pg.pos + Vector3.up * 0.5f, out Vector2 r, out bool front);
                bool on = EdgeClamp(r, front, W, H, 60f, out Vector2 p, out float ang);
                var col = pg.mine ? new Color(0.5f, 1f, 0.6f) : new Color(1f, 0.85f, 0.2f);
                pmArrow[i].SetBg(col); pmText[i].SetColor(col);
                float d = me != null ? Vector3.Distance(me.transform.position, pg.pos) : 0f;
                pmText[i].SetText((pg.mine ? "VOCE " : Coop.FriendLabel + " ") + $"{d:F0}m");
                if (on) { Place(pmArrow[i], p.x, p.y - 20f); pmArrow[i].SetRot(180f); pmText[i].SetPos(p.x - 100f, p.y - 66f); }
                else
                {
                    Place(pmArrow[i], p.x, p.y); pmArrow[i].SetRot(ang);
                    Vector2 inward = (new Vector2(W / 2f, H / 2f) - p).normalized * 48f;
                    pmText[i].SetPos(p.x + inward.x - 100f, p.y + inward.y - 14f);
                }
            }
        }


        // ------------------------------------------------------------ native chat (T): last 8 messages with fade + input box
        NUi.El[] chBg = new NUi.El[8], chTx = new NUi.El[8];
        NUi.El chInBg, chInTx;
        bool chatBuilt;

        void BuildChat()
        {
            for (int i = 0; i < chBg.Length; i++)
            {
                chBg[i] = NUi.Box(null, 14, 0, 1020, 36, new Color(0f, 0f, 0f, 0.38f), false);
                chTx[i] = NUi.Text(chBg[i], 10, 0, 1000, 36, "", 24, Color.white);
                chTx[i].tmp.enableWordWrapping = true;
                chTx[i].tmp.overflowMode = TextOverflowModes.Overflow;
                chTx[i].tmp.alignment = TextAlignmentOptions.Left;
            }
            chInBg = NUi.Box(null, 14, 0, 1020, 44, new Color(0.04f, 0.04f, 0.08f, 0.92f), false);
            chInTx = NUi.Text(chInBg, 10, 0, 1000, 44, "", 26, Color.white);
            chInTx.tmp.enableWordWrapping = true;
            chInTx.tmp.overflowMode = TextOverflowModes.Overflow;
            chatBuilt = true;
        }

        void NativeChatTick()
        {
            if (!chatBuilt) BuildChat();
            float now = Time.realtimeSinceStartup;
            bool show = net.Active || selfTest;
            for (int i = 0; i < chBg.Length; i++) chBg[i].Visible = false;
            chInBg.Visible = false;
            if (!show) return;
            float bottom = 1080f - 74f;                      // just above the status chip
            if (chatOpen)
            {
                string t = "[T] " + (chatText ?? "") + (((int)(now * 2f) % 2 == 0) ? "|" : "");
                int lines = Mathf.Max(1, Mathf.CeilToInt(t.Length / 62f));
                float ih = 14f + lines * 30f;
                chInBg.SetSize(1020f, ih); chInBg.SetPos(14f, bottom - ih);
                chInTx.SetSize(1000f, ih); chInTx.SetText(t);
                chInBg.Visible = true;
                bottom -= ih + 8f;
            }
            int shown = 0; float y = bottom;
            for (int i = Coop.Chat.Count - 1; i >= 0 && shown < chBg.Length; i--)
            {
                var m = Coop.Chat[i];
                float a = chatOpen ? 1f : Mathf.Clamp01((12f - (now - m.At)) / 2f);
                if (a <= 0f) continue;
                int lines = Mathf.Max(1, Mathf.CeilToInt(m.Text.Length / 62f));
                float h = 8f + lines * 30f;
                y -= h;
                var bg = chBg[shown]; var tx = chTx[shown];
                bg.Visible = true; bg.SetSize(1020f, h); bg.SetPos(14f, y);
                bg.img.color = new Color(0f, 0f, 0f, 0.38f * a);
                tx.SetSize(1000f, h); tx.SetText(m.Text);
                var c = m.C; c.a = a; tx.SetColor(c);
                shown++;
                y -= 4f;
            }
        }

        // ------------------------------------------------------------ map marker (G): marks where you look, the friend sees it for 10 s
        void HandleMarkerKey(float now)
        {
            if (!KeyDown(KeyCode.G) || chatOpen || !net.Active || !net.Connected || MyPlayer.Instance == null) return;
            DoMarker(now);
        }

        void DoMarker(float now)
        {
            if (now < nextPingSend) return;
            var cam = GetCam(); var me = MyPlayer.Instance;
            if (cam == null || me == null) return;
            nextPingSend = now + 1f;
            Vector3 p;
            var ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
            if (Physics.Raycast(ray, out RaycastHit hit, 400f)) p = hit.point; else p = ray.origin + ray.direction * 40f;
            AddMarker(p, true);
            pingsSent++;
            Vector3 pp = p;
            net.SendReliable(Coop.Pack(Msg.Marker, w => { w.Write(pp.x); w.Write(pp.y); w.Write(pp.z); }));
            Plugin.Info($"[marker] sent at {p}");
        }

        void AddMarker(Vector3 p, bool mine)
        {
            pings.Add(new PingM { pos = p, until = Time.realtimeSinceStartup + 10f, mine = mine });
            while (pings.Count > pmArrow.Length) pings.RemoveAt(0);
        }

        void HandleMarker(System.IO.BinaryReader br)
        {
            var p = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            if (float.IsNaN(p.x) || Mathf.Abs(p.x) > 5000f || Mathf.Abs(p.z) > 5000f) return;
            AddMarker(p, false); pingsRecv++;
            Plugin.Info($"[marker] received at {p}");
            Coop.Toast("O amigo marcou um ponto no mapa!", 1, 3f);
        }

        // ------------------------------------------------------------ teleport to the friend (H), 45 s cooldown
        float nextTp; bool tpTested;

        void HandleTeleportKey(float now)
        {
            if (!KeyDown(KeyCode.H) || chatOpen) return;
            DoTeleport(now);
        }

        void DoTeleport(float now, bool dev = false)
        {
            var me = MyPlayer.Instance;
            if (me == null || !net.Active || !haveRemote || !remoteInRun || remoteDead || (Coop.WindowOpen && !dev)) return;
            if (now < nextTp) { Coop.Toast($"Teleporte disponivel em {nextTp - now:F0} s", 2, 2f); return; }
            Vector3 mine = me.transform.position;
            float d = Vector3.Distance(mine, ghostTarget);
            if (d < 12f) { Coop.Toast("Voce ja esta perto do amigo.", 0, 2f); return; }
            Vector3 away = new Vector3(mine.x - ghostTarget.x, 0f, mine.z - ghostTarget.z);
            if (away.sqrMagnitude < 0.01f) away = Vector3.right;
            Vector3 dest = ghostTarget + away.normalized * 3f;
            Vector3 dir = new Vector3(ghostTarget.x - dest.x, 0f, ghostTarget.z - dest.z).normalized;
            try { me.TeleportPlayerImmediate(dest, dir, dir, 20f); }
            catch (Exception e) { Plugin.Info("[tp] failed: " + e.Message); return; }
            nextTp = now + 45f;
            Plugin.Info($"[tp] teleported to the friend: {mine} -> {dest} (was {d:F0} m away)");
            Coop.Toast("Teleportado para o amigo!", 1, 3f);
        }

        void DevTeleportTestTick(float now)
        {
            if (tpTested || !Plugin.DevTeleportTest.Value || !Coop.Synced || MyPlayer.Instance == null || !remoteInRun || now < 45f) return;
            tpTested = true; DoTeleport(now, true);
        }

        bool markerTested;
        void DevMarkerTest(float now)
        {
            if (markerTested || !Plugin.DevPingTest.Value || !Coop.IsHost || !Coop.Synced || MyPlayer.Instance == null || now < 40f) return;
            markerTested = true; DoMarker(now);
            Coop.AddChat("Teste", "Esta e uma mensagem longa de teste para ver a quebra de linha do chat nativo na tela do jogo, com bastante texto mesmo.", false);
            Coop.AddChat("Amigo", "oi!", false);
        }
    }
}
