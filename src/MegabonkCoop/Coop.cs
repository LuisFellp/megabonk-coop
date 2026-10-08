using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MegabonkCoop
{
    public enum Msg : byte { PlayerState = 1, Seed = 2, Ping = 3, Pong = 4, SeedCheck = 5, Chat = 6, EnemySnap = 10, EnemyDied = 11, EnemyRemoved = 12, EnemyDamage = 13, ObjCheck = 7, TerrainCheck = 8, Event = 40, Const = 41, Session = 9, SessionAck = 17, Proj = 14, ProjPos = 15, ProjEnd = 16, Portal = 20, PlayerDied = 21, Share = 42, Marker = 43, Catchup = 44, Name = 45, Stats = 46, Interact = 47 }

    // Shared state between the MonoBehaviour and Harmony patches.
    public static class Coop
    {
        public static readonly Net Net = new Net();
        public static bool IsHost => Net.Active && Net.IsHost;
        public static bool IsClient => Net.Active && !Net.IsHost;

        // ---- run seed protocol
        // The GAME rolls its own map seed inside the map-generation coroutine, so:
        //  * host lets the game roll, reads the real seed and announces (RunSeed, RunId);
        //  * client overwrites the game's seed with the host's one right after the game rolled.
        // ---- shared RNG ("fixed seed created at connection"): the HOST draws a session seed when it starts hosting and
        // sends it; at the start of EVERY map generation both PCs seed BOTH of the game's random generators with
        // Mix(sessionSeed, runIndex). (Verified offline: with both generators seeded identically two generations
        // produced identical ground and identical objects.)
        public static int SessionSeed;
        public static bool HaveSession;      // client: received the host's session seed
        public static bool ClientReady;      // host: the client acknowledged the session seed
        public static int Mix(int a, int b)
        {
            unchecked
            {
                uint h = (uint)a * 2654435761u ^ (uint)b * 40503u;
                h ^= h >> 16; h *= 0x85ebca6bu; h ^= h >> 13; h *= 0xc2b2ae35u; h ^= h >> 16;
                return (int)(h & 0x7fffffff);
            }
        }

        // ---- v0.7: no pause (invincible while a window is open), spawn together, rejoin the same run
        public static float InvincibleUntil;
        public static bool WindowOpen;              // a level-up / chest window is open (set by the Inv hooks)
        public static bool HostLeftRun;             // client: the host died / went back to the menu while I am still playing -> enemies are mine again
        public static bool PeerLost;                // no packets for a long time: the friend is gone, behave like solo (pause works again, no window shield)
        public static bool IsInvincible => Net.Active && Net.Connected && !PeerLost && Time.realtimeSinceStartup < InvincibleUntil;
        public static Vector3 HostSpawn;            // where the HOST spawned in the current run
        public static bool HostSpawnValid;
        public static int HostSpawnRunId = -1;      // RunId that HostSpawn belongs to
        public static bool HostInRun;               // client: the host is inside a run right now
        public static bool StartedFromMenu;         // set by the start gate: this generation was started from the menu (not a portal)
        public static int LastGenIdx;
        public static bool RejoinThisGen;           // this generation is a re-join of the host's current run               // run index used for the last shared-RNG seeding (also used for the crypt)

        public static int RunSeed;
        public static bool HasRunSeed;
        public static int RunId;            // host: counts its map generations. client: last id received
        public static int ConsumedRunId;    // client: last RunId already used for a generation
        // The run configuration the HOST used (the player picks map/stage/tier in his own menu: it must be the same on both!)
        public static int HostMap = -1, HostStage = -1, HostTier = -1;

        // What the game ACTUALLY ended up with for the finished generation + a fingerprint of the map.
        public static int ActualSeed;
        public static int ActualGen = -1;       // run index of the generation that produced ActualSeed
        public static int ActualFp;
        public static bool HasActual;

        // ---- "synced" = same seed AND same ground AND same objects. Each check reports once; Evaluate() joins them.
        public static bool Synced;
        public static bool? SeedOk, TerrainOk, ObjOk;
        public static int PendingSeed, PendingFp;
        public static bool HasPending;

        public static float ObjCheckAt;                 // realtime at which to run the ground + objects checks (0 = nothing scheduled)
        public static string ObjMine, ObjTheirs;        // "Type=count;Type=count;..."
        public static short[] TerrainMine, TerrainTheirs;
        public static int TerrainMineGen = -1, TerrainTheirsGen = -1, ObjMineGen = -1, ObjTheirsGen = -1;   // ground height samples (decimetres), 0x7FFF = no ground there

        // keepPending: at the START of a generation the friend may already have finished that same generation (he took the portal first and
        // generates faster); wiping his result made this side never compare, leaving it at "map not confirmed". Old results are dropped by generation index.
        public static void ResetSync(bool keepPending = false)
        {
            Synced = false; HasActual = false; if (!keepPending) HasPending = false;
            SeedOk = TerrainOk = ObjOk = null;
            ObjMine = null; ObjTheirs = null; TerrainMine = null; TerrainTheirs = null; ObjCheckAt = 0f;
            TerrainMineGen = TerrainTheirsGen = ObjMineGen = ObjTheirsGen = -1;
        }

        static void Evaluate()
        {
            if (SeedOk == false || TerrainOk == false || ObjOk == false) { Synced = false; return; }   // the failing check already warned
            if (SeedOk == true && TerrainOk == true && ObjOk == true)
            {
                if (Synced) return;            // the three checks call Evaluate several times (and the friend's answers repeat them): announce once
                Synced = true;
                Plugin.Info("[sync] VERDICT: seed OK, ground OK, objects OK => SYNCED");
                Toast("SINCRONIZADO! Mesma seed, mesmo chao e mesmos objetos", 1, 8f);
            }
        }

        // ------------------------------------------------------------------ 1) seed
        public static int PendingGen;
        public static void OnTheirResult(int seed, int fp, int gen)
        {
            // a result from an OLDER generation than mine is stale: never compare it (it caused a false MISMATCH)
            if (HasActual && gen < ActualGen) { Plugin.Info($"[seedcheck] ignoring a stale result from run index {gen} (mine is {ActualGen})"); return; }
            if (HasActual && gen == ActualGen) CompareSeed(seed, fp);
            else { PendingSeed = seed; PendingFp = fp; PendingGen = gen; HasPending = true; Plugin.Info($"[seedcheck] friend finished run index {gen} (seed {seed}); I have not finished that one yet"); }
        }

        // used when re-joining a run that is already in progress: its objects/ground were modified by 10+ minutes of play
        public static void SkipChecks(string why)
        {
            if (SeedOk != false) { TerrainOk = true; ObjOk = true; }
            Plugin.Info($"[sync] ground/object checks skipped: {why}");
            Toast("Voce voltou para a run do host. (O mapa dela ja foi modificado: checagem de objetos pulada.)", 1, 6f);
            Evaluate();
        }

        public static void OnLocalActual()
        {
            if (HasPending && PendingGen == ActualGen) { HasPending = false; CompareSeed(PendingSeed, PendingFp); }
            else if (HasPending && PendingGen < ActualGen) { HasPending = false; Plugin.Info($"[seedcheck] dropped a pending result of run index {PendingGen} (mine is {ActualGen})"); }
        }

        static void CompareSeed(int theirSeed, int theirFp)
        {
            bool ok = theirSeed == ActualSeed;
            SeedOk = ok;
            Plugin.Info($"[seedcheck] seed mine={ActualSeed} friend={theirSeed} => {(ok ? "MATCH" : "MISMATCH")}");
            if (ok) Toast("Mesma seed nos dois. Conferindo o chao e os objetos...", 0, 4f);
            else Toast("SEEDS DIFERENTES! Voltem ao menu: o HOST inicia a run e o cliente so depois do aviso.", 3, 12f);
            Evaluate();
        }

        // ------------------------------------------------------------------ 2) ground height
        static string Matrix(short[] a, short[] b, int off)
        {
            var sb = new System.Text.StringBuilder();
            for (int r = 0; r < 9; r++)
            {
                sb.Append("\n   ");
                for (int col = 0; col < 9; col++)
                {
                    int i = off + r * 9 + col;
                    if (i >= a.Length || i >= b.Length) { sb.Append("   ?  "); continue; }
                    short x = a[i], y = b[i];
                    if (x == 0x7FFF && y == 0x7FFF) sb.Append("   .  ");
                    else if (x == 0x7FFF || y == 0x7FFF) sb.Append("  ONE ");
                    else sb.Append(((x - y) / 10f).ToString("+0.0;-0.0;0.0").PadLeft(6));
                }
            }
            return sb.ToString();
        }

        public static void CompareTerrain()
        {
            if (TerrainMine == null || TerrainTheirs == null) return;
            if (TerrainMineGen != TerrainTheirsGen) { Plugin.Info($"[terraincheck] not comparing: my run index {TerrainMineGen} vs friend {TerrainTheirsGen}"); return; }
            int n = Math.Min(TerrainMine.Length, TerrainTheirs.Length);
            int bad = 0, both = 0, badFine = 0, bothFine = 0;
            var offs = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                short x = TerrainMine[i], y = TerrainTheirs[i];
                bool fine = i >= 81;
                if (x == 0x7FFF && y == 0x7FFF) continue;
                both++; if (fine) bothFine++;
                int d = (x == 0x7FFF || y == 0x7FFF) ? 99999 : (x - y);
                if (Math.Abs(d) > 5) { bad++; if (fine) badFine++; }
                if (d != 99999) { offs.TryGetValue(d, out int c0); offs[d] = c0 + 1; }
            }
            // the most common height difference = a constant vertical shift between the two worlds (if any)
            int bestD = 0, bestN = 0; foreach (var kv in offs) if (kv.Value > bestN) { bestN = kv.Value; bestD = kv.Key; }
            // a comparison with (almost) nothing in it proves nothing: say so instead of reporting "same ground"
            if (both < 40)
            {
                TerrainOk = true;
                Plugin.Info($"[terraincheck] WARNING only {both} comparable ground points: the ground check is INCONCLUSIVE (treated as not failed, but not verified)");
                Evaluate();
                return;
            }
            bool ok = bad <= Math.Max(6, both / 8);
            TerrainOk = ok;
            Plugin.Info($"[terraincheck] {both} points, {bad} differ by >0.5 m (fine grid around centre: {badFine}/{bothFine}); most common difference = {bestD / 10f:+0.0;-0.0;0.0} m on {bestN} points => {(ok ? "SAME GROUND" : "DIFFERENT GROUND")}");
            Plugin.Info("[terraincheck] COARSE diff matrix (mine - friend, metres; rows x=-400..400, cols z=-400..400):" + Matrix(TerrainMine, TerrainTheirs, 0));
            if (n >= 162) Plugin.Info("[terraincheck] FINE diff matrix around the centre (20 m spacing):" + Matrix(TerrainMine, TerrainTheirs, 81));
            if (!ok) Toast($"O CHAO E DIFERENTE ({bad} de {both} pontos)! Voltem ao menu e reiniciem a run (host primeiro).", 3, 14f);
            Evaluate();
        }

        // ------------------------------------------------------------------ 3) objects
        static Dictionary<string, int> ParseCounts(string s)
        {
            var d = new Dictionary<string, int>();
            foreach (var part in (s ?? "").Split(';'))
            {
                int i = part.LastIndexOf('=');
                if (i <= 0) continue;
                if (int.TryParse(part.Substring(i + 1), out int n)) d[part.Substring(0, i)] = n;
            }
            return d;
        }

        // same seed => (almost) the same objects: compare the number per TYPE and tolerate a tiny difference
        public static void CompareObjects()
        {
            if (ObjMine == null || ObjTheirs == null) return;
            if (ObjMineGen != ObjTheirsGen) { Plugin.Info($"[objcheck] not comparing: my run index {ObjMineGen} vs friend {ObjTheirsGen}"); return; }
            var a = ParseCounts(ObjMine); var b = ParseCounts(ObjTheirs);
            var keys = new HashSet<string>(a.Keys); keys.UnionWith(b.Keys);
            int total = 0, diff = 0;
            var diffs = new List<string>();
            foreach (var k in keys)
            {
                a.TryGetValue(k, out int x); b.TryGetValue(k, out int y);
                total += x;
                if (x != y) { diff += Math.Abs(x - y); diffs.Add($"{k} {x} vs {y}"); }
            }
            int tol = Math.Max(3, (int)(total * 0.03f));
            string list = diffs.Count == 0 ? "-" : string.Join(", ", diffs);
            bool ok = diff <= tol;
            ObjOk = ok;
            Plugin.Info($"[objcheck] total={total} differing={diff} (tolerance {tol}) types: {list} => {(ok ? "OK" : "DIFFERENT")}");
            Plugin.Info($"[objcheck] mine:   {ObjMine}");
            Plugin.Info($"[objcheck] theirs: {ObjTheirs}");
            if (!ok) Toast($"OBJETOS DIFERENTES: {diff} de {total} ({list}). Reiniciem a run: host primeiro.", 3, 12f);
            Evaluate();
        }

        // ---- on-screen toasts (drawn by CoopBehaviour.OnGUI)
        public struct ToastItem { public string Text; public Color C; public float Until; }
        public static readonly List<ToastItem> Toasts = new List<ToastItem>();

        // kind: 0 info (blue), 1 good (green), 2 warning (orange), 3 bad (red)
        public static void Toast(string text, int kind = 0, float secs = 4f)
        {
            Color c = kind == 1 ? new Color(0.4f, 1f, 0.5f) : kind == 2 ? new Color(1f, 0.7f, 0.25f) : kind == 3 ? new Color(1f, 0.35f, 0.35f) : new Color(0.45f, 0.9f, 1f);
            Plugin.Info("[toast] " + text);
            float now = Time.realtimeSinceStartup;
            // the same message already on screen: refresh it instead of stacking a copy (the sync verdict used to show 3 identical lines)
            for (int i = 0; i < Toasts.Count; i++)
                if (Toasts[i].Text == text && Toasts[i].Until > now) { var t = Toasts[i]; t.Until = now + secs; Toasts[i] = t; return; }
            Toasts.Add(new ToastItem { Text = text, C = c, Until = now + secs });
            if (Toasts.Count > 5) Toasts.RemoveAt(0);
        }

        // ---- player events
        public static string LocalName;          // shown in toasts/chat on the friend's screen
        public static bool LocalDead;            // set when MY player died, cleared when a new map generation starts
        public static bool PortalFromNet;        // true while WE trigger the portal because the friend entered it
        public static byte PortalKind;           // 0 = next stage, 1 = final stage
        public static float PortalFollowAt;      // realtime when we should follow the friend into the portal (0 = none)

        // ---- version handshake: both PCs must run the same mod version (a mismatch silently breaks the sync)
        public static bool VersionMismatch;
        public static string PeerVersion = "?";
        public static string PeerName;                // the friend's Steam name (sent once when the link is up); null until known
        public static string FriendLabel => string.IsNullOrEmpty(PeerName) ? "AMIGO" : PeerName.ToUpperInvariant();

        // ---- gameplay events shown in the chat (weapon / item / tome / buff picked up)
        public static void SendEvent(string text)
        {
            Plugin.Info($"[event] me: {text}");
            if (Net.Active && Net.Connected)
            {
                string me = LocalName ?? "Amigo";
                Net.SendReliable(Pack(Msg.Event, w => { w.Write(me); w.Write(text); }));
            }
        }

        public static void AddEvent(string who, string text)
        {
            Plugin.Info($"[event] friend <{who}>: {text}");
            Chat.Add(new ChatLine { Text = $"* {who} {text}", C = new Color(1f, 0.92f, 0.45f), At = Time.realtimeSinceStartup });
            if (Chat.Count > 30) Chat.RemoveAt(0);
        }

        // ---- chat
        public struct ChatLine { public string Text; public Color C; public float At; }
        public static readonly List<ChatLine> Chat = new List<ChatLine>();

        public static void AddChat(string name, string text, bool mine)
        {
            Plugin.Info($"[chat] {(mine ? "me" : "friend")} <{name}> {text}");
            Chat.Add(new ChatLine
            {
                Text = $"{name}: {text}",
                C = mine ? new Color(0.65f, 1f, 0.7f) : new Color(0.55f, 0.9f, 1f),
                At = Time.realtimeSinceStartup
            });
            if (Chat.Count > 30) Chat.RemoveAt(0);
        }

        public static byte[] Pack(Msg type, Action<BinaryWriter> body)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((byte)type);
            body(w);
            return ms.ToArray();
        }
    }
}
