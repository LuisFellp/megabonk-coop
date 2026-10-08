using System;
using System.Collections.Generic;
using System.IO;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using Assets.Scripts._Data.MapsAndStages;
using HarmonyLib;
using UnityEngine;

namespace MegabonkCoop
{
    // Visual-only projectile replication. Damage is NOT done here (EnemySync forwards it to the host);
    // this only lets each player SEE the other one shooting.
    //  sender  : every one of MY projectiles gets an id; I announce it (weapon + start) and then stream its REAL
    //            position at ~15 Hz until it disappears (so homing / bouncing / any speed just works);
    //  receiver: instantiates the weapon's attack prefab with all scripts/colliders stripped and glides it to the
    //            streamed positions. The copy is destroyed on "end" or when updates stop.
    public static class ProjectileSync
    {
        // ---- receiver side
        class Viz { public GameObject Go; public Vector3 Target; public float LastUpdate; public float Born; }
        static readonly Dictionary<uint, Viz> live = new Dictionary<uint, Viz>();

        // ---- sender side
        class Tracked { public uint Id; public ProjectileBase P; public float Born; }
        static readonly Dictionary<IntPtr, Tracked> tracked = new Dictionary<IntPtr, Tracked>();
        static uint nextId = 1;
        static float nextSend;

        static int sent, recv, spawned, fails, skippedAura, posSent;
        static readonly HashSet<int> logged = new HashSet<int>();
        static GameObject holder;

        public static string Stats() => $"proj started={sent} posPk={posSent} recv={recv} spawned={spawned} fails={fails} aura/skipped={skippedAura} live={live.Count} tracking={tracked.Count}";

        public static void Reset()
        {
            foreach (var v in live.Values) if (v.Go != null) UnityEngine.Object.Destroy(v.Go);
            live.Clear(); tracked.Clear();
        }

        // ============================================================ sender
        public static void OnLocalProjectile(ProjectileBase p)
        {
            if (!Plugin.SyncProjectiles.Value || !Coop.Net.Active || !Coop.Net.Connected || p == null) return;
            var wd = p.weaponBase != null ? p.weaponBase.weaponData : null;
            if (wd == null) return;
            if (wd.isAura) { skippedAura++; return; }
            if (tracked.Count >= 80) return;

            var key = p.Pointer;
            if (tracked.TryGetValue(key, out var old))       // pooled object being reused: close the old one first
            {
                uint oid = old.Id;
                Coop.Net.Send(Coop.Pack(Msg.ProjEnd, w => { w.Write((byte)1); w.Write(oid); }));
                tracked.Remove(key);
            }

            var t = p.transform;
            Vector3 pos = t.position, dir = p.direction;
            if (dir.sqrMagnitude < 0.0001f) dir = t.forward;
            dir.Normalize();
            uint id = nextId++;
            int wid = (int)wd.eWeapon;
            tracked[key] = new Tracked { Id = id, P = p, Born = Time.realtimeSinceStartup };

            Coop.Net.Send(Coop.Pack(Msg.Proj, w =>
            {
                w.Write(id); w.Write((short)wid);
                w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
                w.Write(dir.x); w.Write(dir.y); w.Write(dir.z);
            }));
            sent++;
            if (logged.Add(-wid - 1000)) Plugin.Info($"[proj] streaming weapon {wd.eWeapon} ({wd.damageSourceName}) projectile type={p.GetType().Name}");
        }

        static void SenderTick(float now)
        {
            if (tracked.Count == 0 || now < nextSend || !Coop.Net.Connected) return;
            nextSend = now + 0.066f;

            List<IntPtr> dead = null;
            var pos = new List<KeyValuePair<uint, Vector3>>();
            foreach (var kv in tracked)
            {
                var tr = kv.Value;
                bool gone = tr.P == null || now - tr.Born > 10f;
                if (!gone) { try { gone = !tr.P.gameObject.activeInHierarchy; } catch { gone = true; } }
                if (gone) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                pos.Add(new KeyValuePair<uint, Vector3>(tr.Id, tr.P.transform.position));
            }

            for (int i = 0; i < pos.Count; i += 40)
            {
                int n = Math.Min(40, pos.Count - i);
                var seg = pos.GetRange(i, n);
                Coop.Net.Send(Coop.Pack(Msg.ProjPos, w =>
                {
                    w.Write((byte)seg.Count);
                    foreach (var e in seg) { w.Write(e.Key); w.Write(e.Value.x); w.Write(e.Value.y); w.Write(e.Value.z); }
                }));
                posSent++;
            }

            if (dead != null)
            {
                var ids = new List<uint>();
                foreach (var k in dead) { ids.Add(tracked[k].Id); tracked.Remove(k); }
                for (int i = 0; i < ids.Count; i += 40)
                {
                    var seg = ids.GetRange(i, Math.Min(40, ids.Count - i));
                    Coop.Net.Send(Coop.Pack(Msg.ProjEnd, w => { w.Write((byte)seg.Count); foreach (var id in seg) w.Write(id); }));
                }
            }
        }

        // ============================================================ receiver
        public static void Handle(Msg type, BinaryReader br)
        {
            float now = Time.realtimeSinceStartup;
            switch (type)
            {
                case Msg.Proj:
                    {
                        uint id = br.ReadUInt32();
                        int wid = br.ReadInt16();
                        var pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        var dir = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        recv++;
                        if (live.Count >= 120 || Assets.Scripts.Actors.Player.MyPlayer.Instance == null) return;
                        Spawn(id, wid, pos, dir, now);
                    }
                    break;
                case Msg.ProjPos:
                    {
                        int n = br.ReadByte();
                        for (int i = 0; i < n; i++)
                        {
                            uint id = br.ReadUInt32();
                            var p = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                            if (live.TryGetValue(id, out var v)) { v.Target = p; v.LastUpdate = now; }
                        }
                    }
                    break;
                case Msg.ProjEnd:
                    {
                        int n = br.ReadByte();
                        for (int i = 0; i < n; i++)
                        {
                            uint id = br.ReadUInt32();
                            if (live.TryGetValue(id, out var v)) { if (v.Go != null) UnityEngine.Object.Destroy(v.Go); live.Remove(id); }
                        }
                    }
                    break;
            }
        }

        static void Spawn(uint id, int wid, Vector3 pos, Vector3 dir, float now)
        {
            try
            {
                var wd = DataManager.Instance.GetWeapon((EWeapon)wid);
                if (wd == null || wd.attack == null)
                {
                    fails++;
                    if (logged.Add(wid)) Plugin.Info($"[proj] no attack prefab for weapon id {wid}");
                    return;
                }
                if (holder == null) { holder = new GameObject("CoopProjHolder"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder); }

                // instantiate under an INACTIVE holder so none of the game's scripts ever run on the copy
                // wd.attack is only the attack CONTAINER (invisible); the real projectile is WeaponAttack.prefabProjectile
                GameObject src = wd.attack;
                string srcKind = "attack container";
                try
                {
                    var wa = wd.attack.GetComponent<Assets.Scripts.Inventory__Items__Pickups.Weapons.Attacks.WeaponAttack>();
                    if (wa != null && wa.prefabProjectile != null) { src = wa.prefabProjectile; srcKind = "WeaponAttack.prefabProjectile"; }
                }
                catch { }
                var go = UnityEngine.Object.Instantiate(src, holder.transform);
                go.name = "CoopProj_" + wd.eWeapon;
                int nCol = 0, nRb = 0, nMb = 0;
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) { UnityEngine.Object.Destroy(c); nCol++; }
                foreach (var r in go.GetComponentsInChildren<Rigidbody>(true)) { UnityEngine.Object.Destroy(r); nRb++; }
                foreach (var m in go.GetComponentsInChildren<MonoBehaviour>(true)) { UnityEngine.Object.Destroy(m); nMb++; }

                go.transform.SetParent(null, true);
                go.transform.position = pos;
                if (dir.sqrMagnitude > 0.0001f) go.transform.rotation = Quaternion.LookRotation(dir);
                go.SetActive(true);

                // some weapons (the lightning strike) have a prefab with NO renderer or particle at all (its visual is made at runtime by scripts we
                // strip): draw a short vertical bolt so the friend's strike is visible
                bool noVis = false;
                try { noVis = go.GetComponentsInChildren<Renderer>(true).Length == 0 && go.GetComponentsInChildren<ParticleSystem>(true).Length == 0; } catch { }
                if (noVis)
                {
                    try
                    {
                        var bolt = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        var bc = bolt.GetComponent<Collider>(); if (bc != null) UnityEngine.Object.Destroy(bc);
                        bolt.transform.SetParent(go.transform, false);
                        go.transform.rotation = Quaternion.identity;
                        bolt.transform.localScale = new Vector3(0.35f, 40f, 0.35f);
                        bolt.transform.localPosition = new Vector3(0f, 20f, 0f);
                        bolt.GetComponent<Renderer>().material.color = new Color(0.75f, 0.9f, 1f);
                        if (logged.Add(wid + 7000)) Plugin.Info($"[proj] weapon {wd.eWeapon} has no visual of its own: drawing a fallback bolt");
                    }
                    catch (Exception e) { if (logged.Add(wid + 7100)) Plugin.Info("[proj] fallback bolt failed: " + e.Message); }
                }

                live[id] = new Viz { Go = go, Target = pos, LastUpdate = now, Born = noVis ? now - 11.4f : now };
                spawned++;
                if (logged.Add(wid + 5000))
                {
                    int nRend = 0, nPart = 0;
                    try { nRend = go.GetComponentsInChildren<Renderer>(true).Length; nPart = go.GetComponentsInChildren<ParticleSystem>(true).Length; } catch { }
                    Plugin.Info($"[proj] first visual copy of weapon {wd.eWeapon}: {srcKind} '{src.name}' renderers={nRend} particleSystems={nPart} removed colliders={nCol} rigidbodies={nRb} scripts={nMb} at {pos}");
                }
            }
            catch (Exception e)
            {
                fails++;
                if (logged.Add(wid + 9000)) Plugin.Info($"[proj] visual copy of weapon {wid} failed: {e.Message}");
            }
        }

        public static void Tick(float now, float dt)
        {
            SenderTick(now);
            if (live.Count == 0) return;
            List<uint> gone = null;
            foreach (var kv in live)
            {
                var v = kv.Value;
                if (v.Go == null || now - v.LastUpdate > 1.2f || now - v.Born > 12f) { (gone ??= new List<uint>()).Add(kv.Key); continue; }
                var tr = v.Go.transform;
                Vector3 cur = tr.position;
                Vector3 nw = Vector3.Lerp(cur, v.Target, Mathf.Clamp01(25f * dt));
                Vector3 d = nw - cur;
                if (d.sqrMagnitude > 0.0004f) tr.rotation = Quaternion.LookRotation(d.normalized);
                tr.position = nw;
            }
            if (gone != null)
                foreach (var id in gone)
                {
                    if (live[id].Go != null) UnityEngine.Object.Destroy(live[id].Go);
                    live.Remove(id);
                }
        }
    }

    // my projectile was created -> start streaming it to the friend
    [HarmonyPatch(typeof(ProjectileBase), nameof(ProjectileBase.Set))]
    static class P_ProjectileSet
    {
        static bool logged;
        [HarmonyPostfix]
        static void Post(ProjectileBase __instance)
        {
            try { ProjectileSync.OnLocalProjectile(__instance); }
            catch (Exception e) { if (!logged) { logged = true; Plugin.Info("[proj] sender error (logged once): " + e.Message); } }
        }
    }

    // client: cannot start a run before the host did (that is what made the maps differ)
    static class RunGate
    {
        // make MY run use the host's map / stage / tier (each player picks them in his own menu)
        static void ApplyHostConfig(MapSelectionUi ui)
        {
            try
            {
                var rc = ui != null ? ui.runConfig : null;
                if (rc == null || Coop.HostMap <= 0) { Plugin.Info($"[run] cannot apply host config (runConfig={(rc != null)}, hostMap={Coop.HostMap})"); return; }
                var md = DataManager.Instance.GetMap((EMap)Coop.HostMap);
                if (md == null) { Plugin.Info($"[run] host map {Coop.HostMap} not found in DataManager"); return; }
                string before = $"{(rc.mapData != null ? rc.mapData.eMap.ToString() : "?")} tier {rc.mapTierIndex}";
                rc.mapData = md;
                if (Coop.HostStage >= 0 && md.stages != null && Coop.HostStage < md.stages.Length) rc.stageData = md.stages[Coop.HostStage];
                if (Coop.HostTier >= 0) rc.mapTierIndex = Coop.HostTier;
                string after = $"{md.eMap} stage {Coop.HostStage} tier {rc.mapTierIndex}";
                Plugin.Info($"[run] client: forcing the HOST's map. was [{before}] -> now [{after}]");
                if (before != $"{md.eMap} tier {rc.mapTierIndex}") Coop.Toast($"Usando o mapa do host: {md.eMap}, nivel {rc.mapTierIndex + 1}", 1, 6f);
            }
            catch (System.Exception e) { Plugin.Info("[run] applying host config failed: " + e.Message); }
        }

        static int blockedTries; static float blockedSince;
        public static void Reset() { blockedTries = 0; blockedSince = 0f; }
        // soft-lock escape: waiting for a host that never starts (or is gone) must not trap the player.
        // The 3rd attempt within 15 s, or a friend that vanished, lets the start through (without sync guarantees).
        static bool Escape(string why)
        {
            float now = Time.realtimeSinceStartup;
            if (Coop.PeerLost) { Plugin.Info($"[gate] escape ({why}): friend is gone"); return true; }
            if (now - blockedSince > 15f) { blockedTries = 0; blockedSince = now; }
            if (++blockedTries >= 3)
            {
                blockedTries = 0;
                Plugin.Info($"[gate] escape ({why}): 3rd attempt, starting anyway");
                Coop.Toast("Liberado: iniciando mesmo sem confirmacao do host (pode nao sincronizar).", 2, 6f);
                return true;
            }
            return false;
        }

        public static bool Allow(MapSelectionUi ui = null)
        {
            if (Coop.Net.Active && Coop.Net.Connected && Coop.VersionMismatch)
            {
                Plugin.Info($"[gate] start blocked: mod versions differ (peer {Coop.PeerVersion}, me {Plugin.Version})");
                Coop.Toast($"Nao da para iniciar: versoes diferentes (amigo {Coop.PeerVersion}, voce {Plugin.Version}). Atualizem os dois.", 3, 8f);
                return false;
            }
            // host: do not start before the friend is connected and has the shared seed
            if (Coop.IsHost && Plugin.GateHostStart.Value && !Coop.ClientReady && !Escape("host"))
            {
                Plugin.Info($"[gate] host tried to start before the friend is ready (connected={Coop.Net.Connected}, ready={Coop.ClientReady}): blocked");
                Coop.Toast(Coop.Net.Connected ? "Aguardando o amigo receber a seed compartilhada... (1-2 s)" : "Aguardando o amigo CONECTAR antes de iniciar a run. (Para jogar sozinho: Desconectar)", 2, 6f);
                return false;
            }
            if (Coop.IsHost) { Coop.Toast("Iniciando a run sincronizada...", 0, 3f); Share.NewRun(); }
            if (Coop.IsClient && Plugin.GateClientStart.Value && !Coop.HaveSession && !Escape("client-session"))
            {
                Plugin.Info("[gate] client has no session seed yet: blocked");
                Coop.Toast("Ainda sincronizando com o host... tente de novo em 1-2 s.", 2, 4f);
                return false;
            }
            if (!Coop.IsClient || !Plugin.GateClientStart.Value) return true;
            // fresh host run, OR the host is still in its run and I want back in (after dying): both are allowed
            if (Coop.HasRunSeed && (Coop.RunId != Coop.ConsumedRunId || Coop.HostInRun))
            {
                Coop.StartedFromMenu = true; ApplyHostConfig(ui);
                if (Coop.HostInRun) Share.RequestCatchup(); else Share.NewRun();
                return true;
            }
            if (Escape("client-fresh")) return true;
            Plugin.Info($"[gate] client tried to start a run without a fresh host run (hasSeed={Coop.HasRunSeed} runId={Coop.RunId} consumed={Coop.ConsumedRunId}): blocked");
            Coop.Toast("Espere o HOST iniciar a run primeiro! Voce entra quando aparecer 'Host iniciou a run'.", 2, 6f);
            return false;
        }
    }

    [HarmonyPatch(typeof(MapSelectionUi), nameof(MapSelectionUi.StartMap))]
    static class P_GateA { [HarmonyPrefix] static bool Pre(MapSelectionUi __instance) => RunGate.Allow(__instance); }

    // NOTE: MapMenu.StartMap is called by the game continuously at boot; patching it throws. Only MapSelectionUi is gated.
}
