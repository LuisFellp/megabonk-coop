using System;
using System.Collections.Generic;
using System.Net;
using Assets.Scripts.Actors.Player;
using TMPro;
using UnityEngine;

namespace MegabonkCoop
{
    // Native join / host menu (uGUI + TextMeshPro) with a live checklist, recent IPs, auto-enter for the client and
    // diagnostics when the host does not answer. Replaces the IMGUI panel when the native UI could be created.
    public partial class CoopBehaviour
    {
        public bool NativeMenuUp;                     // true once the native menu exists: the IMGUI panel stays hidden
        bool nmBuilt, nmErr;
        NUi.El nmVerWarn, nmOptWin, nmDim, nmCard, nmWin, nmMini, nmLeave, nmOff, nmOn, nmField, nmFieldText, nmIpMain, nmIpMore, nmValid, nmTitle2, nmPing, nmHint, nmAuto, nmRetry;
        NUi.El[] nmRows = new NUi.El[5];
        NUi.El[] nmRecent = new NUi.El[3];
        string[] recentIps = new string[0];
        float joinStartedAt = -1f;
        string lastTitle;

        static readonly Color cBg = new Color(0.08f, 0.07f, 0.12f, 0.94f);
        static readonly Color cBtn = new Color(0.20f, 0.45f, 0.75f, 1f);
        static readonly Color cGreen = new Color(0.20f, 0.60f, 0.30f, 1f);
        static readonly Color cRed = new Color(0.70f, 0.22f, 0.22f, 1f);
        static readonly Color cGrey = new Color(0.30f, 0.30f, 0.36f, 1f);
        static readonly Color cOk = new Color(0.45f, 1f, 0.55f), cWait = new Color(1f, 0.85f, 0.35f), cBad = new Color(1f, 0.45f, 0.4f), cDim = new Color(0.75f, 0.75f, 0.82f);

        List<string> allLocalIps = new List<string>();

        void NativeUiUpdate()
        {
            if (!Plugin.NativeUi.Value || nmErr) return;
            if (!NUi.Ensure()) return;
            try
            {
                if (!nmBuilt) BuildMenu();
                UpdateMenu();
                NUi.Tick(!chatOpen);
                NativeToastsTick();
                try { NativeHudTick(); } catch (Exception e) { hudErr = true; Plugin.Info("[nui] HUD failed, back to IMGUI: " + e); }
            }
            catch (Exception e) { nmErr = true; NativeMenuUp = false; Plugin.Info("[nui] menu failed, back to IMGUI: " + e); }
        }

        void BuildMenu()
        {
            nmBuilt = true;
            LoadRecent();
            allLocalIps = ListLocalIps();
            const float X = 560f, Y = 80f, W = 800f, Hh = 930f;
            nmDim = NUi.Box(null, 0, 0, 1920, 1080, new Color(0f, 0f, 0f, 0.6f));
            nmWin = NUi.Box(null, X, Y, W, Hh, cBg);
            NUi.Text(nmWin, 30, 18, 600, 60, "MEGABONK COOP", 46, Color.white);
            NUi.Text(nmWin, 560, 30, 170, 40, "v" + Plugin.Version, 28, cDim, TextAlignmentOptions.Right);
            NUi.Button(nmWin, W - 70, 14, 56, 56, "X", () => { panelOpen = false; ipFocus = false; }, cGrey, 34);

            NUi.Text(nmWin, 30, 96, 740, 40, "Seu IP (passe pro amigo):", 28, cDim);
            nmIpMain = NUi.Text(nmWin, 30, 136, 480, 70, bestIp, 56, new Color(0.55f, 0.9f, 1f));
            NUi.Button(nmWin, W - 250, 140, 220, 64, "COPIAR", () =>
            {
                if (bestIp == "?") { Coop.Toast("Nao achei seu IP. Veja no Radmin.", 2, 4f); return; }
                try { GUIUtility.systemCopyBuffer = bestIp; Coop.Toast("IP copiado: " + bestIp, 1, 3f); } catch (Exception e) { Plugin.Info("[panel] copy failed: " + e.Message); }
            }, cBtn, 30);
            nmIpMore = NUi.Text(nmWin, 30, 208, W - 60, 34, "", 20, cDim);

            // ---- not connected
            nmOff = NUi.Group(nmWin, 0, 260, W, 500);
            NUi.Button(nmOff, 30, 0, W - 60, 100, "HOSPEDAR  (voce e o host)", () => { ipFocus = false; StartHost("[ui] Hospedar"); }, cGreen, 40);
            NUi.Text(nmOff, 30, 124, W - 60, 40, "ou entre no host do seu amigo:", 28, cDim);
            nmField = NUi.Button(nmOff, 30, 170, W - 270, 76, "", () => { ipFocus = true; }, new Color(0.14f, 0.14f, 0.2f, 1f), 36);
            nmFieldText = NUi.Text(nmField, 18, 0, W - 300, 76, "", 38, Color.white, TextAlignmentOptions.Left);
            NUi.Button(nmOff, W - 220, 170, 190, 76, "COLAR", () => { ipFocus = false; PasteIp(); }, cBtn, 32);
            nmValid = NUi.Text(nmOff, 30, 250, W - 60, 34, "", 24, cBad);
            for (int i = 0; i < nmRecent.Length; i++)
            {
                int k = i;
                nmRecent[i] = NUi.Button(nmOff, 30 + i * 250, 292, 236, 52, "", () => { if (k < recentIps.Length) { ipText = recentIps[k]; ipFocus = false; } }, cGrey, 26);
            }
            NUi.Button(nmOff, 30, 370, W - 60, 100, "ENTRAR NO HOST", () => { JoinFromMenu(); }, cBtn, 40);
            NUi.Button(nmOff, 30, 492, W - 60, 64, "OPCOES", () => { optOpen = true; }, cGrey, 30);
            nmVerWarn = NUi.Text(nmOff, 30, 574, W - 60, 76, "", 22, cBad);
            nmVerWarn.tmp.enableWordWrapping = true;
            nmVerWarn.tmp.overflowMode = TextOverflowModes.Overflow;
            BuildOptions();

            // ---- connected: compact card on the right, above the game's leaderboard (the host still needs the game's PLAY button)
            const float CW = 520f;
            nmCard = NUi.Box(null, 1380, 80, CW, 328, cBg);
            nmOn = NUi.Group(nmCard, 0, 0, CW, 328);
            NUi.Button(nmOn, CW - 48, 6, 40, 40, "X", () => { panelOpen = false; }, cGrey, 24);
            nmTitle2 = NUi.Text(nmOn, 16, 8, CW - 80, 40, "", 28, Color.white);
            for (int i = 0; i < nmRows.Length; i++) nmRows[i] = NUi.Text(nmOn, 16, 54 + i * 31, CW - 32, 30, "", 21, cDim);
            nmPing = NUi.Text(nmOn, CW - 160, 12, 110, 30, "", 20, cDim, TextAlignmentOptions.Right);
            nmHint = NUi.Text(nmOn, 16, 214, CW - 32, 62, "", 19, cWait);
            nmHint.tmp.enableWordWrapping = true;
            nmHint.tmp.overflowMode = TextOverflowModes.Overflow;
            nmAuto = NUi.Button(nmOn, 12, 280, 236, 40, "", () => { Plugin.AutoJoin.Value = !Plugin.AutoJoin.Value; }, cGrey, 18);
            nmRetry = NUi.Button(nmOn, 256, 280, 124, 40, "REFAZER", () => { string ip = Plugin.JoinIp.Value; Disconnect("[ui] Refazer"); ipText = ip; JoinFromMenu(); }, cBtn, 18);
            nmLeave = NUi.Button(nmOn, 388, 280, 120, 40, "SAIR", () => Disconnect("[ui] Desconectar"), cRed, 18);

            // ---- minimised
            nmMini = NUi.Button(null, 1920f / 2f - 130f, 1080f - 70f, 260, 52, "COOP  [F7]", () => { panelOpen = true; }, new Color(0.1f, 0.1f, 0.16f, 0.92f), 26);
            NativeMenuUp = true;
            Plugin.Info("[nui] native join menu built");
        }

        // ------------------------------------------------------------ options
        bool optOpen;
        NUi.El[] optRows = new NUi.El[8];
        readonly List<Func<string>> optLabels = new List<Func<string>>();

        static string Onoff(bool b) => b ? "[X]  " : "[  ]  ";

        void BuildOptions()
        {
            const float W = 800f;
            nmOptWin = NUi.Box(null, 560, 40, W, 960, cBg);
            NUi.Text(nmOptWin, 30, 18, 600, 60, "OPCOES", 46, Color.white);
            NUi.Button(nmOptWin, W - 70, 14, 56, 56, "X", () => { optOpen = false; }, cGrey, 34);
            NUi.Text(nmOptWin, 30, 84, W - 60, 40, "Clique para ligar/desligar. (host) = so vale no host.", 22, cDim);
            var items = new List<(Func<string> label, Action act)>
            {
                (() => Onoff(Plugin.ShareXpGold.Value) + "XP e ouro somados entre os dois", () => Plugin.ShareXpGold.Value = !Plugin.ShareXpGold.Value),
                (() => Onoff(Plugin.AutoJoin.Value) + "Entrar sozinho quando o host inicia", () => Plugin.AutoJoin.Value = !Plugin.AutoJoin.Value),
                (() => Onoff(Plugin.NoPause.Value) + "Jogo nao pausa (invencivel na janela)", () => Plugin.NoPause.Value = !Plugin.NoPause.Value),
                (() => Onoff(Plugin.SplitSpawns.Value) + "Inimigos nascem perto dos dois (host)", () => Plugin.SplitSpawns.Value = !Plugin.SplitSpawns.Value),
                (() => Onoff(Plugin.SpawnTogether.Value) + "Nascer juntos no inicio", () => Plugin.SpawnTogether.Value = !Plugin.SpawnTogether.Value),
                (() => Onoff(Plugin.BlockLeaderboard.Value) + "Nao enviar run coop ao ranking Steam", () => Plugin.BlockLeaderboard.Value = !Plugin.BlockLeaderboard.Value),
                (() => "Vida dos inimigos com 2 jogadores: x" + Plugin.HpScale.Value.ToString("0.##") + " (host)", () =>
                {
                    float v = Plugin.HpScale.Value; Plugin.HpScale.Value = v < 1.1f ? 1.25f : v < 1.4f ? 1.5f : v < 1.9f ? 2f : 1f;
                }),
                (() => Onoff(Plugin.NativeUi.Value) + "Interface nativa (reabrir o jogo p/ trocar)", () => Plugin.NativeUi.Value = !Plugin.NativeUi.Value),
            };
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                optLabels.Add(it.label);
                optRows[i] = NUi.Button(nmOptWin, 30, 140 + i * 78, W - 60, 66, it.label(), it.act, cGrey, 28);
            }
            NUi.Button(nmOptWin, 30, 140 + items.Count * 78, W - 60, 66, "SALVAR O LOG NA AREA DE TRABALHO", () => SaveLogToDesktop(), cGreen, 26);
            NUi.Button(nmOptWin, 30, 140 + items.Count * 78 + 78, W - 60, 80, "VOLTAR", () => { optOpen = false; }, cBtn, 36);
        }

        // the BepInEx log is open for writing by the game: copy it with shared access to a file the user can find and send
        void SaveLogToDesktop()
        {
            try
            {
                string src = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
                string who = (string.IsNullOrEmpty(myName) ? "jogador" : myName);
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) who = who.Replace(c, '_');
                string role = Coop.IsHost ? "host" : Coop.IsClient ? "amigo" : "solo";
                string dir = !string.IsNullOrEmpty(Plugin.DevLogDir.Value) ? Plugin.DevLogDir.Value : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                System.IO.Directory.CreateDirectory(dir);
                string dst = System.IO.Path.Combine(dir, $"coop-log-v{Plugin.Version}-{role}-{who}.log");
                using (var fs = new System.IO.FileStream(src, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (var os = new System.IO.FileStream(dst, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                    fs.CopyTo(os);
                Plugin.Info("[ui] log saved to " + dst);
                Coop.Toast("Log salvo na Area de Trabalho: " + System.IO.Path.GetFileName(dst), 1, 6f);
            }
            catch (Exception e) { Plugin.Info("[ui] saving the log failed: " + e.Message); Coop.Toast("Nao consegui salvar o log: " + e.Message, 3, 6f); }
        }

        void UpdateOptions()
        {
            for (int i = 0; i < optLabels.Count && i < optRows.Length; i++) if (optRows[i] != null) optRows[i].SetText(optLabels[i]());
        }

        void JoinFromMenu()
        {
            ipFocus = false;
            string t = (ipText ?? "").Trim();
            if (t.Length == 0 || t.IndexOf(' ') >= 0) { Coop.Toast("Digite o IP do host (ex.: 26.12.34.56)", 3, 4f); return; }
            Plugin.JoinIp.Value = t;
            RememberIp(t);
            joinStartedAt = Time.realtimeSinceStartup;
            StartJoin("[ui] Conectar");
        }

        void LoadRecent()
        {
            try { recentIps = (Plugin.RecentIps.Value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries); } catch { recentIps = new string[0]; }
        }

        void RememberIp(string ip)
        {
            var l = new List<string>(recentIps);
            l.Remove(ip); l.Insert(0, ip);
            if (l.Count > 3) l.RemoveRange(3, l.Count - 3);
            recentIps = l.ToArray();
            try { Plugin.RecentIps.Value = string.Join(";", recentIps); } catch { }
        }

        static List<string> ListLocalIps()
        {
            var l = new List<string>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        var ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.")) continue;
                        string tag = ip.StartsWith("26.") ? "Radmin" : ip.StartsWith("192.168.") || ip.StartsWith("10.") ? "LAN" : "outra";
                        l.Add(ip + " (" + tag + ")");
                    }
                }
            }
            catch { }
            return l;
        }

        string Row(string state, string text) => state + "  " + text;

        void SetRow(int i, bool ok, bool waiting, string text)
        {
            var r = nmRows[i]; if (r == null) return;
            r.Visible = true;
            r.SetText((ok ? "[OK]  " : waiting ? "[..]  " : "[X]  ") + text);
            r.SetColor(ok ? cOk : waiting ? cWait : cBad);
        }

        void UpdateMenu()
        {
            float now = Time.realtimeSinceStartup;
            // IMGUI panel is only hidden once the native one exists
            NativeMenuUp = true;
            bool menuScene = lastScene == "MainMenu";
            bool modal = panelOpen && !net.Active;
            nmDim.Visible = modal; nmWin.Visible = modal && !optOpen;
            nmOptWin.Visible = modal && optOpen;
            if (!modal) optOpen = false;
            if (modal && optOpen) UpdateOptions();
            nmCard.Visible = panelOpen && net.Active;
            nmMini.Visible = !panelOpen && menuScene;
            if (!panelOpen) return;

            nmIpMain.SetText(bestIp);
            nmIpMore.SetText(allLocalIps.Count > 0 ? string.Join("   ", allLocalIps.GetRange(0, Math.Min(3, allLocalIps.Count))) : "");
            nmOff.Visible = !net.Active;

            if (!net.Active)
            {
                string t = (ipText ?? "");
                bool valid = IPAddress.TryParse(t.Trim(), out _);
                string shown = t + (ipFocus && ((int)(now * 2f) % 2 == 0) ? "|" : "");
                if (shown.Length == 0 && !ipFocus) { nmFieldText.SetText("clique aqui e digite, ou COLAR"); nmFieldText.SetColor(cDim); }
                else { nmFieldText.SetText(shown); nmFieldText.SetColor(Color.white); }
                nmValid.SetText(t.Trim().Length > 0 && !valid ? "Isso nao parece um IP (ex.: 26.12.34.56)" : "");
                nmVerWarn.SetText(Plugin.GameVersion != Plugin.TestedGameVersion && Plugin.GameVersion != "?" ? $"ATENCAO: este mod foi feito para o Megabonk {Plugin.TestedGameVersion} e o seu jogo e {Plugin.GameVersion}. Pode nao funcionar direito." : "");
                for (int i = 0; i < nmRecent.Length; i++)
                {
                    nmRecent[i].Visible = i < recentIps.Length;
                    if (i < recentIps.Length) nmRecent[i].SetText(recentIps[i]);
                }
                return;
            }

            bool host = Coop.IsHost;
            string title = (host ? "VOCE E O HOST" : "VOCE E O AMIGO (cliente)");
            if (title != lastTitle) { lastTitle = title; nmTitle2.SetText(title); }
            bool versionKnown = Coop.PeerVersion != "?" && Coop.PeerVersion != null;
            bool versionOk = versionKnown && !Coop.VersionMismatch;
            for (int i = 0; i < nmRows.Length; i++) nmRows[i].Visible = false;
            string hint = "";
            if (host)
            {
                SetRow(0, haveRemote && !Coop.PeerLost, !haveRemote, haveRemote ? "Amigo conectado" + (string.IsNullOrEmpty(Coop.PeerName) ? "" : ": " + Coop.PeerName) : "Aguardando o amigo conectar...");
                SetRow(1, versionOk, !versionKnown, Coop.VersionMismatch ? $"Versoes diferentes (amigo {Coop.PeerVersion}, voce {Plugin.Version})" : "Mesma versao do mod");
                SetRow(2, Coop.ClientReady, haveRemote, "Seed compartilhada com o amigo");
                bool ready = Coop.ClientReady && !stalled && !Coop.VersionMismatch;
                SetRow(3, ready, true, ready ? "Pronto! Clique JOGAR no menu do jogo" : "Aguardando o amigo ficar pronto");
                if (remoteInRun || MyPlayer.Instance != null) SetRow(4, Coop.Synced, true, Coop.Synced ? "Mapas sincronizados" : "Verificando se os mapas sao iguais...");
                if (!haveRemote) hint = "Mande seu IP ao amigo. Os dois precisam estar na mesma rede (Radmin VPN) e na mesma versao do mod.";
            }
            else
            {
                SetRow(0, haveRemote && !Coop.PeerLost, !haveRemote, haveRemote ? "Conectado ao host" + (string.IsNullOrEmpty(Coop.PeerName) ? "" : ": " + Coop.PeerName) : "Conectando ao host...");
                SetRow(1, versionOk, !versionKnown, Coop.VersionMismatch ? $"Versoes diferentes (host {Coop.PeerVersion}, voce {Plugin.Version})" : "Mesma versao do mod");
                SetRow(2, Coop.HaveSession, haveRemote, "Sessao recebida do host");
                bool hostStarted = Coop.HasRunSeed && (Coop.RunId != Coop.ConsumedRunId || Coop.HostInRun);
                bool inRun = MyPlayer.Instance != null;
                SetRow(3, hostStarted || inRun, true, hostStarted || inRun ? (inRun ? "Voce esta na run do host" : "O host iniciou a run!") : "Esperando o host clicar JOGAR");
                if (inRun) SetRow(4, Coop.Synced, true, Coop.Synced ? "Mapas sincronizados" : "Verificando se os mapas sao iguais...");
                if (!haveRemote && joinStartedAt > 0 && now - joinStartedAt > 8f)
                    hint = "O host nao respondeu. Confira: o host ja clicou HOSPEDAR? O IP esta certo? Radmin ligado nos dois? Firewall liberado (UDP " + Plugin.PortCfg.Value + ")?";
                else if (hostStarted && !inRun) hint = Plugin.AutoJoin.Value ? "Entrando automaticamente... (ou clique JOGAR)" : "Clique JOGAR no menu do jogo para entrar.";
            }
            if (Coop.PeerLost) hint = "O amigo sumiu (jogo fechado ou VPN caiu). O jogo voltou ao normal; se ele voltar, reconecta sozinho.";
            if (Coop.VersionMismatch) hint = "Atualizem os dois para a MESMA versao do mod. A run so inicia depois.";
            nmHint.SetText(hint);
            nmPing.SetText(haveRemote ? "Ping: " + (rtt < 0 ? "?" : rtt.ToString("F0") + " ms") : "");
            nmAuto.Visible = !host;
            nmAuto.SetText(Plugin.AutoJoin.Value ? "Entrar auto: LIGADO" : "Entrar auto: desligado");
            nmRetry.Visible = !host && (!haveRemote && joinStartedAt > 0 && now - joinStartedAt > 8f || Coop.PeerLost);
        }

        // ------------------------------------------------------------ auto-enter: the client follows the host into the run
        float autoEnterSince = -1f, autoEnterNext;
        int autoEnterStep;

        void AutoEnterTick(float now)
        {
            if (!Plugin.AutoJoin.Value || !Coop.IsClient || lastScene != "MainMenu" || !Coop.HaveSession || Coop.VersionMismatch || (Plugin.AutoRun.Value != "off" && Plugin.AutoRun.Value != "ui")) { autoEnterSince = -1f; autoEnterStep = 0; return; }
            bool hostStarted = Coop.HasRunSeed && (Coop.RunId != Coop.ConsumedRunId || Coop.HostInRun);
            if (!hostStarted) { autoEnterSince = -1f; autoEnterStep = 0; return; }
            if (autoEnterSince < 0f)
            {
                autoEnterSince = now; autoEnterStep = 0;
                Coop.Toast("O host iniciou a run! Entrando em 3 s... (ou clique JOGAR)", 1, 5f);
                Plugin.Info("[autojoin] host started a run: entering in 3 s");
            }
            if (now - autoEnterSince < 3f || now < autoEnterNext) return;
            try
            {
                var mm = UnityEngine.Object.FindObjectOfType<MainMenu>();
                if (mm == null) { autoEnterNext = now + 1f; return; }
                if (autoEnterStep == 0)
                {
                    Plugin.Info("[autojoin] -> GoToMapSelection()");
                    mm.GoToMapSelection(); autoEnterStep = 1; autoEnterNext = now + 3f; return;
                }
                var ui = UnityEngine.Object.FindObjectOfType<MapSelectionUi>();
                if (ui == null && mm.mapSelectionUi != null) ui = mm.mapSelectionUi;
                if (ui == null) { autoEnterNext = now + 1.5f; return; }
                if (!ui.gameObject.activeInHierarchy) ui.gameObject.SetActive(true);
                Plugin.Info("[autojoin] -> MapSelectionUi.StartMap()");
                ui.StartMap();
                autoEnterStep = 0; autoEnterSince = now + 30f; autoEnterNext = now + 8f;    // do not retry for a while
            }
            catch (Exception e) { Plugin.Info("[autojoin] failed: " + e.Message); autoEnterNext = now + 4f; }
        }

        // ------------------------------------------------------------ toasts
        NUi.El[] nmToastBg = new NUi.El[5];
        NUi.El[] nmToastTx = new NUi.El[5];

        void NativeToastsTick()
        {
            float now = Time.realtimeSinceStartup;
            var live = new List<Coop.ToastItem>();
            foreach (var ti in Coop.Toasts) if (ti.Until > now) live.Add(ti);
            for (int i = 0; i < nmToastBg.Length; i++)
            {
                if (nmToastBg[i] == null)
                {
                    nmToastBg[i] = NUi.Box(null, 1920f / 2f - 520f, 30f + i * 64f, 1040f, 56f, new Color(0.05f, 0.05f, 0.08f, 0.85f), false);
                    nmToastTx[i] = NUi.Text(nmToastBg[i], 16, 0, 1008, 56, "", 30, Color.white, TextAlignmentOptions.Center);
                }
                if (i < live.Count)
                {
                    var t = live[i];
                    nmToastBg[i].Visible = true;
                    nmToastTx[i].SetText(t.Text); nmToastTx[i].SetColor(t.C);
                }
                else nmToastBg[i].Visible = false;
            }
        }
    }
}
