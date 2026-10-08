using System;
using System.Collections.Generic;
using System.IO;
using Actors.Enemies;
using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Managers;
using UnityEngine;

namespace MegabonkCoop
{
    // Host-authoritative enemies.
    //  host  : runs the game normally, broadcasts a snapshot of every live enemy at 20 Hz + died/removed events,
    //          applies damage events coming from the client, and makes enemies chase the nearer of the two players.
    //  client: never spawns enemies by itself. Creates "copies" from the host snapshots, freezes their AI and
    //          moves them to the host's positions. Hits on a copy are sent to the host instead of being applied.
    public static class EnemySync
    {
        public static bool NetSpawning;          // true only while WE spawn a client copy

        class Copy { public Enemy E; public Vector3 Target; public float LastSeen; }

        static readonly Dictionary<uint, Copy> copies = new Dictionary<uint, Copy>();        // host id -> copy
        static readonly Dictionary<uint, uint> localToHost = new Dictionary<uint, uint>();   // local id -> host id
        static readonly Dictionary<uint, Rigidbody> origTarget = new Dictionary<uint, Rigidbody>();
        static readonly Dictionary<uint, Transform> origFollow = new Dictionary<uint, Transform>();

        static float nextSnap, nextRetarget, nextSweep, nextRetargetLog;
        static readonly Dictionary<uint, float> diedEarly = new Dictionary<uint, float>();   // deaths that arrived before the enemy existed here
        static int swept, killedByHp;
        static GameObject ghostRef;                    // host: the friend's ghost (set by HostTick)
        static int spawnCounter, splitMoved, splitSeen;
        static float nextDistLog;
        static readonly Dictionary<uint, float> dyingUntil = new Dictionary<uint, float>();   // local ids of copies playing their death animation
        static int snapsSent, enemiesSent, snapsRecv, copiesSpawned, copiesRemoved, diedRecv, dmgSent, dmgApplied, spawnFails, retargeted;
        static bool loggedFirstSnap, loggedFirstSpawn, loggedFirstDamage, loggedFirstDied;
        static readonly HashSet<int> badTypes = new HashSet<int>();
        static readonly Dictionary<string, int> srcSent = new Dictionary<string, int>();      // client: weapons whose hits I forwarded
        static readonly Dictionary<string, int> srcApplied = new Dictionary<string, int>();   // host: weapons of the friend that I applied
        static int lastSrcTotal;

        static void CountSrc(Dictionary<string, int> d, string k)
        {
            k = string.IsNullOrEmpty(k) ? "(none)" : k;
            d.TryGetValue(k, out int n); d[k] = n + 1;
        }

        // which weapons actually deal damage through the sync? (answers "player two's weapons don't work")
        public static void LogSources()
        {
            var d = Coop.IsHost ? srcApplied : srcSent;
            int total = 0; foreach (var kv in d) total += kv.Value;
            if (total == lastSrcTotal || total == 0) return;
            lastSrcTotal = total;
            var parts = new List<string>();
            foreach (var kv in d) parts.Add($"{kv.Key}={kv.Value}");
            Plugin.Info($"[enemy] damage by source ({(Coop.IsHost ? "applied from friend" : "forwarded to host")}): {string.Join(", ", parts)}");
        }

        public static string Stats() =>
            Coop.IsHost ? $"enemySync[host] snaps={snapsSent} enemiesSent={enemiesSent} dmgApplied={dmgApplied} retargeted={retargeted}"
                        : $"enemySync[client] snaps={snapsRecv} copies={copies.Count} spawned={copiesSpawned} removed={copiesRemoved} died={diedRecv} hpKills={killedByHp} swept={swept} dmgSent={dmgSent} spawnFails={spawnFails}";

        static readonly HashSet<uint> hpScaled = new HashSet<uint>(); static int hpScaledCount; static float nextScaleLog;

        public static void Reset(string why)
        {
            hpScaled.Clear(); LocalOwned.Clear();
            if (copies.Count > 0 || origTarget.Count > 0 || why == "peer lost") Plugin.Info($"[enemy] reset ({why}): dropping {copies.Count} copies; {Stats()}");
            copies.Clear(); localToHost.Clear(); dyingUntil.Clear(); diedEarly.Clear(); origTarget.Clear(); origFollow.Clear();
            loggedFirstSnap = loggedFirstSpawn = loggedFirstDamage = loggedFirstDied = false;
        }

        // ------------------------------------------------------------------ HOST
        public static void HostTick(float now, MyPlayer me, GameObject ghost)
        {
            var mgr = EnemyManager.Instance;
            ghostRef = ghost;
            if (mgr == null || me == null || !Coop.Net.Connected) return;
            var dict = mgr.enemies;
            if (dict == null) return;

            if (now >= nextSnap)
            {
                nextSnap = now + 0.05f;
                var list = new List<Enemy>();
                foreach (var kv in dict)
                {
                    var e = kv.Value;
                    if (e != null && e.gameObject != null && e.gameObject.activeInHierarchy) list.Add(e);
                }
                // two players kill things twice as fast: every enemy that appears while the friend is in the run gets more HP (once)
                float k = Plugin.HpScale.Value;
                if (k > 1.001f && ghost != null && ghost.activeSelf)
                {
                    if (hpScaled.Count > 6000) hpScaled.Clear();
                    foreach (var e in list)
                    {
                        try { if (hpScaled.Add((uint)e.id)) { e.maxHp *= k; e.hp *= k; hpScaledCount++; } } catch { }
                    }
                    if (hpScaledCount > 0 && now > nextScaleLog) { nextScaleLog = now + 15f; Plugin.Info($"[enemy] HP x{k:F2} applied to {hpScaledCount} enemies so far (2 players)"); }
                }
                for (int i = 0; i < list.Count; i += 32)
                {
                    int n = Math.Min(32, list.Count - i);
                    var seg = list.GetRange(i, n);
                    Coop.Net.Send(Coop.Pack(Msg.EnemySnap, w =>
                    {
                        w.Write((byte)seg.Count);
                        foreach (var e in seg)
                        {
                            var p = e.transform.position;
                            w.Write(e.id);
                            w.Write((short)(int)e.enemyData.enemyName);
                            w.Write((int)e.enemyFlag);
                            w.Write(p.x); w.Write(p.y); w.Write(p.z);
                            w.Write(e.hp);
                            w.Write(e.transform.localScale.x);
                            w.Write((byte)Mathf.Clamp(e.waveNumber, 0, 255));
                        }
                    }));
                    snapsSent++; enemiesSent += n;
                }
                if (!loggedFirstSnap && list.Count > 0) { loggedFirstSnap = true; Plugin.Info($"[enemy] host: first snapshot, {list.Count} enemies"); }
            }

            if (Plugin.Verbose.Value && now >= nextDistLog && ghost != null && ghost.activeSelf)
            {
                nextDistLog = now + 15f;
                int nearHost = 0, nearFriend = 0, total = 0;
                Vector3 hp = me.transform.position, fp = ghost.transform.position;
                foreach (var kv in dict)
                {
                    var e = kv.Value; if (e == null) continue;
                    total++;
                    var ep = e.transform.position;
                    float dh = (ep - hp).sqrMagnitude, df = (ep - fp).sqrMagnitude;
                    if (dh < 60f * 60f) nearHost++;
                    if (df < 60f * 60f) nearFriend++;
                }
                Plugin.Info($"[enemy] distribution: {total} enemies, {nearHost} within 60 m of the host, {nearFriend} within 60 m of the friend; players are {Vector3.Distance(hp, fp):F0} m apart; split spawns moved {splitMoved}/{splitSeen}");
            }

            if (now >= nextRetarget && ghost != null && ghost.activeSelf)
            {
                nextRetarget = now + 0.3f;
                Retarget(dict, me, ghost);
            }
        }

        // enemies chase whichever player is nearer: the local player or the friend's ghost
        static void Retarget(Il2CppSystem.Collections.Generic.Dictionary<uint, Enemy> dict, MyPlayer me, GameObject ghost)
        {
            var ghostRb = ghost.GetComponent<Rigidbody>();
            if (ghostRb == null) return;
            Vector3 mp = me.transform.position, gp = ghost.transform.position;
            int changed = 0;
            foreach (var kv in dict)
            {
                var e = kv.Value;
                if (e == null) continue;
                Vector3 ep = e.transform.position;
                bool wantGhost = (ep - gp).sqrMagnitude < (ep - mp).sqrMagnitude;
                uint id = e.id;
                if (wantGhost)
                {
                    if (!origTarget.ContainsKey(id)) { origTarget[id] = e.target; origFollow[id] = e.followTarget; }
                    if (e.target != ghostRb) { e.target = ghostRb; e.followTarget = ghost.transform; changed++; }
                }
                else if (origTarget.TryGetValue(id, out var ot))
                {
                    e.target = ot; e.followTarget = origFollow[id];
                    origTarget.Remove(id); origFollow.Remove(id); changed++;
                }
            }
            retargeted += changed;
            if (changed > 0 && Plugin.Verbose.Value && Time.realtimeSinceStartup >= nextRetargetLog) { nextRetargetLog = Time.realtimeSinceStartup + 20f; Plugin.Info($"[enemy] retarget: {retargeted} switches so far, {origTarget.Count} enemies chase the friend right now"); }
        }

        static bool GroundY(float x, float z, float fromY, out float y)
        {
            y = 0f;
            if (Physics.Raycast(new Vector3(x, fromY + 150f, z), Vector3.down, out RaycastHit hit, 600f)) { y = hit.point.y; return true; }
            return false;
        }

        // every other wave enemy is moved from "around the host" to "around the friend" (same offset, ground-adjusted)
        public static void HostSplitSpawn(Enemy e)
        {
            if (!Plugin.SplitSpawns.Value || e == null || !Coop.Net.Connected) return;
            var me = MyPlayer.Instance; var g = ghostRef;
            if (me == null || g == null || !g.activeSelf) return;
            splitSeen++;
            if ((spawnCounter++ & 1) == 0) return;                 // keep this one around the host
            try
            {
                Vector3 ep = e.transform.position, hp = me.transform.position, fp = g.transform.position;
                if ((ep - hp).magnitude > 120f) return;            // not a normal "around the player" spawn
                Vector3 off = ep - hp;
                float oldGround = 0f; bool hadOld = GroundY(ep.x, ep.z, ep.y, out oldGround);
                float h = hadOld ? ep.y - oldGround : 0f;          // height above the ground (flyers keep theirs)
                var np = new Vector3(fp.x + off.x, ep.y, fp.z + off.z);
                if (GroundY(np.x, np.z, fp.y, out float ng)) np.y = ng + Mathf.Max(h, 0f);
                else np.y = fp.y + off.y;
                e.transform.position = np;
                if (e.rb != null) { try { e.rb.position = np; } catch { } }
                splitMoved++;
            }
            catch (Exception ex) { if (splitSeen < 5) Plugin.Info("[enemy] split spawn failed: " + ex.Message); }
        }

        // enemies spawned by an interactable that the CLIENT activated (challenge shrine, egg...): they are his alone (not mirrored, not swept)
        public static bool AllowLocal;
        public static readonly HashSet<uint> LocalOwned = new HashSet<uint>();
        public static void NoteLocal(Enemy e)
        {
            if (!AllowLocal || !Coop.IsClient || e == null) return;
            LocalOwned.Add(e.id);
            if (LocalOwned.Count <= 3) Plugin.Info($"[interact] local enemy spawned by an interactable (id {e.id}); it is exempt from the sweep");
        }

        public static int BlockedSpawns; static int blockedLogged;
        public static void NoteBlocked(string what)
        {
            BlockedSpawns++;
            if (blockedLogged < 6) { blockedLogged++; Plugin.Info($"[enemy] client: a native spawn was blocked ({what}) #{BlockedSpawns}"); }
        }

        public static bool ApplyingFriend;       // true while the host applies the friend's hit (so it is not counted as the host's)

        public static void HostEnemyDied(Enemy e)
        {
            if (!Coop.Net.Connected || e == null) return;
            uint id = e.id; CoopStats.HostKill(id); var p = e.transform.position;
            byte[] pkt = Coop.Pack(Msg.EnemyDied, w => { w.Write(id); w.Write(p.x); w.Write(p.y); w.Write(p.z); });
            Coop.Net.SendReliable(pkt);
            origTarget.Remove(id); origFollow.Remove(id);
        }

        public static void HostEnemyRemoved(Enemy e)
        {
            if (!Coop.Net.Connected || e == null) return;
            uint id = e.id;
            Coop.Net.SendReliable(Coop.Pack(Msg.EnemyRemoved, w => w.Write(id)));
            origTarget.Remove(id); origFollow.Remove(id);
        }

        static void HostApplyDamage(uint id, float dmg, bool crit, float knock, Vector3 dir, string srcName)
        {
            var mgr = EnemyManager.Instance;
            if (mgr == null || mgr.enemies == null) return;
            if (!mgr.enemies.TryGetValue(id, out var e) || e == null) return;
            var dc = new DamageContainer(1f, "coop-friend");
            dc.damage = dmg; dc.crit = crit; dc.knockback = knock; dc.direction = dir;
            dc.damageSource = string.IsNullOrEmpty(srcName) ? "coop-friend" : srcName; dc.enemy = e;
            CoopStats.ClientHit(id, dmg);
            ApplyingFriend = true;
            try { e.Damage(dc); } finally { ApplyingFriend = false; }
            dmgApplied++;
            CountSrc(srcApplied, srcName);
            if (!loggedFirstDamage) { loggedFirstDamage = true; Plugin.Info($"[enemy] host: applied first friend damage {dmg} (crit={crit}) to enemy {id}"); }
        }

        // ---------------------------------------------------------------- CLIENT
        public static bool IsCopy(Enemy e, out uint hostId)
        {
            hostId = 0;
            return e != null && localToHost.TryGetValue(e.id, out hostId);
        }

        public static void SendDamage(uint hostId, DamageContainer dc)
        {
            var d = dc.direction;
            float dmg = dc.damage, kb = dc.knockback; bool crit = dc.crit;
            string srcNameOut = dc.damageSource ?? "";
            Coop.Net.SendReliable(Coop.Pack(Msg.EnemyDamage, w =>
            {
                w.Write(hostId); w.Write(dmg); w.Write(crit); w.Write(kb);
                w.Write(srcNameOut);
                w.Write(d.x); w.Write(d.y); w.Write(d.z);
            }));
            dmgSent++;
            CountSrc(srcSent, dc.damageSource);
            if (!loggedFirstDamage) { loggedFirstDamage = true; Plugin.Info($"[enemy] client: first hit forwarded to host (enemy {hostId}, dmg {dmg})"); }
        }

        static Copy SpawnCopy(uint hostId, int type, int flag, Vector3 pos, float scale, int wave)
        {
            var mgr = EnemyManager.Instance;
            if (mgr == null) return null;
            var ed = DataManager.Instance.GetEnemyData((EEnemy)type);
            if (ed == null)
            {
                if (badTypes.Add(type)) Plugin.Info($"[enemy] client: no EnemyData for type {type}");
                spawnFails++; return null;
            }
            Enemy e = null;
            NetSpawning = true;
            try { e = mgr.SpawnEnemy(ed, pos, wave, true, (EEnemyFlag)flag, false, 1f); }
            catch (Exception ex) { Plugin.Info($"[enemy] client: SpawnEnemy threw for type {type}: {ex.Message}"); }
            finally { NetSpawning = false; }
            if (e == null) { spawnFails++; return null; }

            // freeze the AI: the host decides where it is
            try { if (e.enemyMovement != null) e.enemyMovement.enabled = false; } catch { }
            try { if (e.rb != null) { if (!e.rb.isKinematic) e.rb.velocity = Vector3.zero; e.rb.isKinematic = true; } } catch { }
            try { if (scale > 0.01f) e.transform.localScale = Vector3.one * scale; } catch { }

            var c = new Copy { E = e, Target = pos, LastSeen = Time.realtimeSinceStartup };
            copies[hostId] = c;
            localToHost[e.id] = hostId;
            copiesSpawned++;
            if (!loggedFirstSpawn) { loggedFirstSpawn = true; Plugin.Info($"[enemy] client: first copy spawned (host id {hostId} -> local id {e.id}, type {type}, scale {scale})"); }
            return c;
        }

        static void RemoveCopy(uint hostId, Copy c, bool died)
        {
            copies.Remove(hostId);
            if (c.E == null) return;
            try { dyingUntil[c.E.id] = Time.realtimeSinceStartup + 8f; } catch { }     // keep the sweep away while it dies
            try { localToHost.Remove(c.E.id); } catch { }
            copiesRemoved++;
            try
            {
                if (died)
                {
                    var e = c.E;
                    bool stageBoss = false, finalBoss = false;
                    try { stageBoss = e.IsStageBoss(); finalBoss = e.IsFinalBoss(); } catch { }
                    var dc = new DamageContainer(1f, "coop-host-kill");
                    dc.damage = e.hp + 1f; dc.enemy = e;
                    e.EnemyDied(dc);
                    if (stageBoss && !finalBoss)
                    {
                        // make sure the "stage boss defeated" event fires on the client too (portal / end of stage)
                        bool dead = false;
                        try { dead = EnemyManager.Instance != null && EnemyManager.Instance.stageBossIsDead; } catch { }
                        Plugin.Info($"[enemy] client: STAGE BOSS copy died (stageBossIsDead={dead})");
                        if (!dead) { try { EnemyManager.Instance.StageBossDied(); Plugin.Info("[enemy] client: fired StageBossDied() manually"); } catch (Exception ex2) { Plugin.Info("[enemy] client: StageBossDied failed: " + ex2.Message); } }
                    }
                }
                else EnemyManager.Instance?.RemoveEnemy(c.E);
            }
            catch (Exception ex) { Plugin.Info($"[enemy] client: removing copy {hostId} (died={died}) failed: {ex.Message}"); }
        }

        // The client must not have enemies of its own: anything in EnemyManager.enemies that is not a copy of a host enemy
        // (spawned by the client's own bosses/shrines/summoners) is removed. (idea from the chat: "sweep ids, kill the extras")
        static void SweepNative(float now)
        {
            if (!Plugin.SweepNative.Value || now < nextSweep) return;
            nextSweep = now + 1.5f;
            var mgr = EnemyManager.Instance;
            if (mgr == null || mgr.enemies == null) return;
            List<Enemy> extra = null;
            foreach (var kv in mgr.enemies)
            {
                var e = kv.Value;
                if (e == null) continue;
                if (localToHost.ContainsKey(e.id)) continue;
                if (LocalOwned.Contains(e.id)) continue;                                           // spawned by my own shrine/egg
                if (dyingUntil.TryGetValue(e.id, out float du) && now < du) continue;          // a copy that is dying
                (extra ??= new List<Enemy>()).Add(e);
            }
            if (dyingUntil.Count > 200) { var old = new List<uint>(); foreach (var kv in dyingUntil) if (now > kv.Value) old.Add(kv.Key); foreach (var k in old) dyingUntil.Remove(k); }
            if (extra == null) return;
            var names = new List<string>();
            foreach (var e in extra)
            {
                try
                {
                    if (names.Count < 6) names.Add(e.enemyData != null ? e.enemyData.enemyName.ToString() : "?");
                    mgr.RemoveEnemy(e);
                    if (e.gameObject != null) e.gameObject.SetActive(false);
                    swept++;
                }
                catch { }
            }
            Plugin.Info($"[enemy] sweep: removed {extra.Count} enemies that exist only on the client ({string.Join(",", names)}); total swept {swept}");
        }

        public static void ClientTick(float now, float dt)
        {
            try { SweepNative(now); } catch (Exception ex) { Plugin.Info("[enemy] sweep error: " + ex.Message); }
            if (copies.Count == 0) return;
            List<uint> gone = null;
            foreach (var kv in copies)
            {
                var c = kv.Value;
                if (c.E == null || c.E.gameObject == null) { (gone ??= new List<uint>()).Add(kv.Key); continue; }
                if (now - c.LastSeen > 2f) { (gone ??= new List<uint>()).Add(kv.Key); continue; }   // host stopped listing it
                var tr = c.E.transform;
                tr.position = Vector3.Lerp(tr.position, c.Target, Mathf.Clamp01(18f * dt));
            }
            if (gone != null)
                foreach (var id in gone)
                {
                    var c = copies[id];
                    bool vanished = c.E == null;
                    if (!vanished) RemoveCopy(id, c, false); else { copies.Remove(id); }
                }
        }

        // ------------------------------------------------------------ messages (both sides)
        public static void Handle(Msg type, BinaryReader br, float now)
        {
            switch (type)
            {
                case Msg.EnemySnap:
                    if (!Coop.IsClient || EnemyManager.Instance == null || MyPlayer.Instance == null) return;
                    snapsRecv++;
                    int n = br.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        uint id = br.ReadUInt32();
                        int et = br.ReadInt16();
                        int flag = br.ReadInt32();
                        var pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        float hp = br.ReadSingle();
                        float scale = br.ReadSingle();
                        int wave = br.ReadByte();
                        if (!copies.TryGetValue(id, out var c) || c.E == null)
                        {
                            if (diedEarly.TryGetValue(id, out float tdie) && now - tdie < 10f) continue;      // it already died: do not create a ghost
                            if (hp <= 0.01f) continue;
                            c = SpawnCopy(id, et, flag, pos, scale, wave);
                            if (c == null) continue;
                        }
                        c.Target = pos; c.LastSeen = now;
                        try { c.E.hp = hp; } catch { }
                        if (hp <= 0.01f && Plugin.SyncEnemies.Value)
                        {
                            // the host says it has 0 hp: do not wait for a Died event that may have been lost
                            killedByHp++;
                            RemoveCopy(id, c, true);
                        }
                    }
                    break;

                case Msg.EnemyDied:
                    {
                        uint id = br.ReadUInt32();
                        diedRecv++;
                        if (!Coop.IsClient) return;
                        if (copies.TryGetValue(id, out var c))
                        {
                            if (!loggedFirstDied) { loggedFirstDied = true; Plugin.Info($"[enemy] client: first death event (host id {id})"); }
                            RemoveCopy(id, c, true);
                        }
                        else diedEarly[id] = now;      // died before (or without) a copy: remember so no ghost is created later
                    }
                    break;

                case Msg.EnemyRemoved:
                    {
                        uint id = br.ReadUInt32();
                        if (!Coop.IsClient) return;
                        if (copies.TryGetValue(id, out var c)) RemoveCopy(id, c, false);
                    }
                    break;

                case Msg.EnemyDamage:
                    {
                        uint id = br.ReadUInt32();
                        float dmg = br.ReadSingle(); bool crit = br.ReadBoolean(); float kb = br.ReadSingle();
                        string srcName = br.ReadString();
                        var dir = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        if (Coop.IsHost) HostApplyDamage(id, dmg, crit, kb, dir, srcName);
                    }
                    break;
            }
        }
    }
}
