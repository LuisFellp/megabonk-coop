using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace MegabonkCoop
{
    [BepInPlugin("luis.megabonkcoop", "Megabonk Coop", Plugin.Version)]
    public class Plugin : BasePlugin
    {
        public const string Version = "1.7.0";
        public const string TestedGameVersion = "1.0.69";
        public static string GameVersion = "?";
        public static BepInEx.Configuration.ConfigEntry<string> AutoStart;
        public static BepInEx.Configuration.ConfigEntry<bool> SyncEnemies;
        public static BepInEx.Configuration.ConfigEntry<bool> PortalFollow;
        public static BepInEx.Configuration.ConfigEntry<string> AutoRun;
        public static BepInEx.Configuration.ConfigEntry<bool> SyncProjectiles;
        public static BepInEx.Configuration.ConfigEntry<bool> GateClientStart;
        public static BepInEx.Configuration.ConfigEntry<int> DevForceSeed;
        public static BepInEx.Configuration.ConfigEntry<int> DevClientMap;
        public static BepInEx.Configuration.ConfigEntry<int> DevSeedMode;
        public static BepInEx.Configuration.ConfigEntry<bool> ShareSeed;
        public static BepInEx.Configuration.ConfigEntry<bool> GateHostStart;
        public static BepInEx.Configuration.ConfigEntry<bool> NoPause;
        public static BepInEx.Configuration.ConfigEntry<bool> SweepNative;
        public static BepInEx.Configuration.ConfigEntry<bool> SpawnTogether;
        public static BepInEx.Configuration.ConfigEntry<bool> BlockLeaderboard;
        public static BepInEx.Configuration.ConfigEntry<int> DevHostMap, DevHostTier, DevForceHat;
        public static BepInEx.Configuration.ConfigEntry<bool> DevPortalTest;
        public static BepInEx.Configuration.ConfigEntry<bool> SplitSpawns;
        public static BepInEx.Configuration.ConfigEntry<float> DevSpawnOffset;
        public static BepInEx.Configuration.ConfigEntry<bool> ShareXpGold, DevShareTest, NativeUi, AutoJoin, DevPingTest, DevFinalTest, DevTeleportTest, WrapAllInteractables;
        public static BepInEx.Configuration.ConfigEntry<string> RecentIps, DevInteract, DevLogDir;
        public static BepInEx.Configuration.ConfigEntry<float> DevShotAt, HpScale;
        public static BepInEx.Configuration.ConfigEntry<int> DevLoss, DevLatency, DevJitter;
        public static BepInEx.Configuration.ConfigEntry<string> DevVersion;
        public static string WireVersion => string.IsNullOrEmpty(DevVersion?.Value) ? Version : DevVersion.Value;   // what we tell the friend (dev: can be faked)
        public static Plugin Instance;
        public static BepInEx.Configuration.ConfigEntry<bool> Verbose;
        public static BepInEx.Configuration.ConfigEntry<string> JoinIp;
        public static BepInEx.Configuration.ConfigEntry<int> PortCfg;

        public override void Load()
        {
            Instance = this;
            Verbose = Config.Bind("Logging", "Verbose", true, "Log a status line every 5s (ping, packets, positions, scene). Keep ON while testing.");
            AutoStart = Config.Bind("Network", "AutoStart", "off", "off | host | join. Starts hosting/joining by itself ~8s after launch (same as F8/F9).");
            SyncEnemies = Config.Bind("Sync", "Enemies", true, "Host-authoritative enemies (spawns, positions, damage, deaths). Set false to turn the enemy sync off if it misbehaves.");
            PortalFollow = Config.Bind("Sync", "PortalFollow", true, "When the friend enters the stage portal, enter yours too (after 1s).");
            AutoRun = Config.Bind("Dev", "AutoRun", "off", "DEV ONLY. off | solo | host | client. Drives the menus by code (no key presses). solo also turns on the F6 dummy. client waits for the host's run first.");
            NativeUi = Config.Bind("Interface", "NativeUi", true, "Use the game's own UI (uGUI/TextMeshPro) for the coop menu and notices instead of IMGUI. Falls back to IMGUI by itself if it cannot be created.");
            AutoJoin = Config.Bind("Interface", "AutoJoin", true, "Client: enter the run by itself ~3 s after the host starts it (no need to click PLAY).");
            RecentIps = Config.Bind("Interface", "RecentIPs", "", "Last hosts you joined (filled by the menu).");
            DevShotAt = Config.Bind("Dev", "ShotAt", 0f, "DEV ONLY: take a screenshot of the game at this second (0 = never) into the shots folder next to the game.");
            DevPingTest = Config.Bind("Dev", "PingTest", false, "DEV ONLY: the host sends a map marker at 40 s.");
            HpScale = Config.Bind("Sync", "HpScale", 1.5f, "HOST: multiplies the HP of every enemy while the friend is in the run too (1 = off). Two players deal more damage, so 1.5 keeps the game from being trivial.");
            DevFinalTest = Config.Bind("Dev", "FinalTest", false, "DEV ONLY (with PortalTest): the host loads the FINAL stage instead of the next one.");
            DevTeleportTest = Config.Bind("Dev", "TeleportTest", false, "DEV ONLY: teleport to the friend at 45 s (needs the players to be far apart).");
            DevInteract = Config.Bind("Dev", "Interact", "", "DEV ONLY: at 40 s call Interact() on the first interactable whose object name starts with this (e.g. ChallengeShrine, BossSpawner) and log what the game does.");
            DevLogDir = Config.Bind("Dev", "LogDir", "", "DEV ONLY: when set, 'save log' writes here instead of the Desktop, and does it by itself at 30 s.");
            WrapAllInteractables = Config.Bind("Dev", "WrapAllInteractables", false, "DEV ONLY: wrap EVERY interactable (not only the known spawners). Causes NullReference spam on the ChargeShrine, leave off.");
            ShareXpGold = Config.Bind("Sync", "ShareXpGold", true, "XP and gold GAINED by one player are also gained, in full, by the other (a sum, not a split). Spending stays individual.");
            DevShareTest = Config.Bind("Dev", "ShareTest", false, "DEV ONLY: the host gives itself +25 xp and +100 gold at 30 s to prove the sharing.");
            SyncProjectiles = Config.Bind("Sync", "Projectiles", true, "Show the friend's projectiles on your screen (visual only; their damage is handled by the enemy sync).");
            GateClientStart = Config.Bind("Sync", "GateClientStart", true, "Client cannot start a run before the host did. Prevents the two players ending up in different maps.");
            DevForceSeed = Config.Bind("Dev", "ForceSeed", 0, "DEV ONLY. Solo: force this map seed (0 = off). Used to find out which seed controls the terrain.");
            DevClientMap = Config.Bind("Dev", "ClientMap", 0, "DEV ONLY (AutoRun=client): pick this EMap (1 Forest, 2 Desert, 4 Hell, 8 Graveyard) before starting, to prove the host config overrides it.");
            DevSeedMode = Config.Bind("Dev", "SeedMode", 0, "DEV ONLY (experiments). 0 none | 1 UnityEngine.Random.InitState(ForceSeed or 4242) at generation start | 2 = 1 + replace MyRandom.random with a System.Random of the same seed.");
            ShareSeed = Config.Bind("Sync", "ShareSeed", true, "Seed both of the game's random generators identically on both PCs when a map is generated (this is what keeps terrain and objects identical).");
            GateHostStart = Config.Bind("Sync", "GateHostStart", true, "Host cannot start a run before the friend is connected AND has received the shared seed.");
            NoPause = Config.Bind("Sync", "NoPause", true, "The game never pauses in coop (a paused player would make the other one take all the damage). You are invincible while the level-up/chest window is open.");
            SweepNative = Config.Bind("Sync", "SweepNativeEnemies", true, "Client: remove enemies that the client's own game spawned (they do not exist on the host).");
            SpawnTogether = Config.Bind("Sync", "SpawnTogether", true, "Client spawns next to the host at the start of a run.");
            BlockLeaderboard = Config.Bind("Sync", "BlockLeaderboardUpload", true, "Do not upload the score of a coop run to the Steam leaderboards (a coop run is not comparable with solo runs).");
            DevHostMap = Config.Bind("Dev", "HostMap", 0, "DEV ONLY (AutoRun=host/solo): pick this EMap (1 Forest, 2 Desert, 4 Hell, 8 Graveyard) before starting.");
            DevHostTier = Config.Bind("Dev", "HostTier", 0, "DEV ONLY: tier index (0..2) used together with HostMap.");
            DevForceHat = Config.Bind("Dev", "ForceHat", 0, "DEV ONLY: pretend my character wears this EHat id (to test hat replication without a hat in the save).");
            DevPortalTest = Config.Bind("Dev", "PortalTest", false, "DEV ONLY (AutoRun=host): try to enter the stage portal ~25 s into the run to test the stage transition + the follow.");
            SplitSpawns = Config.Bind("Sync", "SplitSpawns", true, "Host: every other wave enemy is placed around the FRIEND instead of around the host (so both players get enemies).");
            DevSpawnOffset = Config.Bind("Dev", "SpawnOffset", 2f, "DEV ONLY: how far from the host the client spawns (metres). Use a big value to test enemies around two distant players.");
            DevLatency = Config.Bind("Dev", "Latency", 0, "DEV ONLY: delay every OUTGOING datagram by this many ms (one way) to simulate a VPN.");
            DevJitter = Config.Bind("Dev", "Jitter", 0, "DEV ONLY: +/- this many ms of random extra delay (can reorder packets).");
            DevLoss = Config.Bind("Dev", "PacketLoss", 0, "DEV ONLY: randomly drop this percentage of OUTGOING datagrams (to test the reliable channel).");
            DevVersion = Config.Bind("Dev", "FakeVersion", "", "DEV ONLY: report this version to the friend instead of the real one (tests the version check).");
            JoinIp = Config.Bind("Network", "JoinIP", "127.0.0.1", "IP of the host (use the host's Radmin VPN IP). Used when you press F9.");
            PortCfg = Config.Bind("Network", "Port", 27055, "UDP port. Must be the same on both PCs. Host must allow it in the firewall.");
            ClassInjector.RegisterTypeInIl2Cpp<CoopBehaviour>();
            var go = new GameObject("MegabonkCoop");
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<CoopBehaviour>();
            // patch class by class: if a game update removes a method we hook, only THAT feature is lost (and logged), not the whole mod
            var harmony = new Harmony("luis.megabonkcoop");
            int pOk = 0, pBad = 0;
            foreach (var t in typeof(Plugin).Assembly.GetTypes())
            {
                bool has = false;
                try { has = t.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0; } catch { }
                if (!has) continue;
                try { harmony.CreateClassProcessor(t).Patch(); pOk++; }
                catch (System.Exception e) { pBad++; Log.LogWarning($"[patch] FAILED {t.Name}: {e.GetBaseException().Message}"); }
            }
            try { InterPatches.ApplyAll(harmony); } catch (System.Exception e) { Log.LogWarning("[patch] interactables: " + e.Message); }
            GameVersion = Application.version ?? "?";
            Log.LogInfo($"[patch] {pOk} patch classes applied, {pBad} failed. Game version {GameVersion} (mod built for {TestedGameVersion})");
            Log.LogInfo("Megabonk Coop loaded. F8 = host, F9 = join (IP from config), F10 = disconnect");
        }

        public static void Info(string m) => Instance.Log.LogInfo($"[{UnityEngine.Time.realtimeSinceStartup.ToString("F1").PadLeft(7)}s] {m}");
    }
}
