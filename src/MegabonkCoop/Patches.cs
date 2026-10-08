using Assets.Scripts._Data.MapsAndStages;
using Assets.Scripts.Game.Other;
using Assets.Scripts.Managers;
using HarmonyLib;
using UnityEngine;

namespace MegabonkCoop
{
    // The real map seed lives in MapGenerationController.mapSeed (+ MapGenerator.seed).
    // The map is built by the coroutine MapGenerationController.GenerateMap(); we hook its MoveNext.
    // Observed on 1.0.69: the coroutine re-rolls mapSeed during its FIRST step, so anything we set in the
    // prefix is overwritten. We therefore (a) also set the controller's testSeed field (looks like a debug
    // override) and (b) re-apply the seed right after the first step.
    [HarmonyPatch(typeof(MapGenerationController._GenerateMap_d__39), nameof(MapGenerationController._GenerateMap_d__39.MoveNext))]
    static class MapGenCoroutinePatch
    {
        static bool inGen;
        static int steps;
        static bool applyForced;      // client: this generation must use the host's seed
        static int forcedSeed;

        // map / stage / tier the game is about to generate
        public static void ReadRunConfig(out int map, out int stage, out int tier, out string text)
        {
            map = -1; stage = -1; tier = -1; text = "?";
            try
            {
                var rc = MapController.runConfig;
                if (rc == null) { text = "runConfig=null"; return; }
                var md = rc.mapData;
                map = md != null ? (int)md.eMap : -1;
                tier = rc.mapTierIndex;
                string sname = "?";
                if (md != null && md.stages != null && rc.stageData != null)
                {
                    for (int i = 0; i < md.stages.Length; i++)
                        if (md.stages[i] != null && md.stages[i].Pointer == rc.stageData.Pointer) { stage = i; break; }
                    sname = rc.stageData.name;
                }
                text = $"map={(md != null ? md.eMap.ToString() : "?")}({map}) stage={stage} '{sname}' tier={tier} challenge={(rc.challenge != null ? rc.challenge.name : "none")}";
            }
            catch (System.Exception e) { text = "runConfig unreadable: " + e.Message; }
        }

        static string Seeds() =>
            $"mapSeed={MapGenerationController.mapSeed} MapGenerator.seed={MapGenerator.seed} RsgController.seed={RsgController.seed}";

        static void Apply(MapGenerationController c, int seed)
        {
            // ONLY mapSeed. MapGenerator.seed stays at the game's own value (105 on the host): changing it altered the
            // client's terrain, which is what made the friend look like he was flying above the ground.
            MapGenerationController.mapSeed = seed;
        }

        // Fingerprint of the generated map: where the game put the portals/platform + the terrain mesh bounds.
        static int Fingerprint(MapGenerationController c)
        {
            unchecked
            {
                int h = 17;
                string dbg = "";
                void Add(string name, GameObject go)
                {
                    if (go == null) { h = h * 31 + 1; dbg += $" {name}=null"; return; }
                    var p = go.transform.position;
                    h = h * 31 + Mathf.RoundToInt(p.x * 10f);
                    h = h * 31 + Mathf.RoundToInt(p.y * 10f);
                    h = h * 31 + Mathf.RoundToInt(p.z * 10f);
                    dbg += $" {name}={p}";
                }
                if (c != null)
                {
                    Add("spawnPortal", c.spawnPortal);
                    Add("bossPortal", c.bossPortal);
                    Add("spawnPlatform", c.spawnPlatform);
                    try
                    {
                        var r = c.proceduralMeshRenderer;
                        if (r != null)
                        {
                            var b = r.bounds;
                            h = h * 31 + Mathf.RoundToInt(b.center.x * 100f);
                            h = h * 31 + Mathf.RoundToInt(b.center.y * 100f);
                            h = h * 31 + Mathf.RoundToInt(b.center.z * 100f);
                            h = h * 31 + Mathf.RoundToInt(b.size.x * 100f);
                            h = h * 31 + Mathf.RoundToInt(b.size.y * 100f);
                            h = h * 31 + Mathf.RoundToInt(b.size.z * 100f);
                            dbg += $" terrainBounds=c{b.center} s{b.size}";
                        }
                        else dbg += " terrainRenderer=null";
                    }
                    catch (System.Exception e) { dbg += " terrainBounds=ERR(" + e.Message + ")"; }
                }
                // terrain shape: the portals are at fixed spots and the bounds are constant, so hash the real heights
                if (c != null)
                {
                    bool meshOk = false;
                    try
                    {
                        var mf = c.proceduralMeshRenderer != null ? c.proceduralMeshRenderer.GetComponent<MeshFilter>() : null;
                        var mesh = mf != null ? mf.sharedMesh : null;
                        if (mesh != null)
                        {
                            var verts = mesh.vertices;
                            int step = System.Math.Max(1, verts.Length / 600);
                            int vh = 7;
                            for (int i = 0; i < verts.Length; i += step) vh = vh * 31 + Mathf.RoundToInt(verts[i].y * 100f);
                            h = h * 31 + vh;
                            meshOk = verts.Length > 0;
                            dbg += $" meshVerts={verts.Length} meshHash={vh}";
                        }
                        else dbg += " mesh=null";
                    }
                    catch (System.Exception e) { dbg += " meshERR(" + e.Message + ")"; }
                    if (!meshOk)
                    {
                        try   // fallback: ray-cast a 9x9 grid of terrain heights
                        {
                            int rh = 11, hits = 0;
                            for (int gx = -4; gx <= 4; gx++)
                                for (int gz = -4; gz <= 4; gz++)
                                {
                                    if (Physics.Raycast(new Vector3(gx * 100f, 300f, gz * 100f), Vector3.down, out RaycastHit hit, 600f))
                                    { rh = rh * 31 + Mathf.RoundToInt(hit.point.y * 100f); hits++; }
                                    else rh = rh * 31 + 1;
                                }
                            h = h * 31 + rh;
                            dbg += $" rayGrid hits={hits}/81 hash={rh}";
                        }
                        catch (System.Exception e) { dbg += " rayERR(" + e.Message + ")"; }
                    }
                }
                Plugin.Info("[mapgen] fingerprint inputs:" + dbg);
                return h;
            }
        }

        static bool sharedActive; static int sharedSeedNow;

        [HarmonyPrefix]
        static void Pre(MapGenerationController._GenerateMap_d__39 __instance)
        {
            if (__instance.__1__state != 0)
            {
                // the generator yields between frames: anything else that draws from the global RNGs in between (particles, tumbleweeds, storm...) would
                // desync the two machines, so both reseed deterministically at EVERY step (shared seed + step number)
                if (inGen && sharedActive)
                {
                    int sx = Coop.Mix(sharedSeedNow, steps + 1000);
                    UnityEngine.Random.InitState(sx);
                    Utility.MyRandom.random = new Il2CppSystem.Random(sx);
                }
                return;
            }
            inGen = true; sharedActive = false; steps = 0; applyForced = false;
            Coop.ResetSync(true);
            Coop.LocalDead = false;
            string role = Coop.IsHost ? "host" : Coop.IsClient ? "client" : "solo";
            // ---- shared RNG: both machines seed BOTH of the game's generators identically before anything is drawn
            if (Plugin.ShareSeed.Value && (Coop.IsHost || (Coop.IsClient && Coop.HaveSession)))
            {
                bool menuStart = Coop.StartedFromMenu; Coop.StartedFromMenu = false;
                bool freshRun = Coop.HasRunSeed && Coop.RunId != Coop.ConsumedRunId;
                // a client that starts from the MENU while the host is still inside its run re-joins THAT run (same index)
                bool rejoin = !freshRun && menuStart && Coop.HostInRun && Coop.HasRunSeed;
                int idx = Coop.IsHost ? Coop.RunId + 1 : ((freshRun || rejoin) ? Coop.RunId : Coop.RunId + 1);   // the host's run number for THIS generation
                Coop.LastGenIdx = idx;
                if (rejoin) { Plugin.Info($"[seed] client REJOINS the host's current run (index {idx})"); Coop.RejoinThisGen = true; } else Coop.RejoinThisGen = false;
                int shared = Coop.Mix(Coop.SessionSeed, idx);
                UnityEngine.Random.InitState(shared);
                Utility.MyRandom.random = new Il2CppSystem.Random(shared);
                sharedActive = true; sharedSeedNow = shared;
                Plugin.Info($"[seed] shared RNG seeded ({role}): session={Coop.SessionSeed} runIndex={idx} => {shared} (UnityEngine.Random + MyRandom.random)");
            }
            else if (Coop.IsClient) Plugin.Info("[seed] WARNING client has no session seed yet: terrain/objects will differ");
            if (Plugin.DevSeedMode.Value >= 1)
            {
                int x = Plugin.DevForceSeed.Value != 0 ? Plugin.DevForceSeed.Value : 4242;
                UnityEngine.Random.InitState(x);
                if (Plugin.DevSeedMode.Value >= 2) Utility.MyRandom.random = new Il2CppSystem.Random(x);
                Plugin.Info($"[dev] SeedMode {Plugin.DevSeedMode.Value}: UnityEngine.Random.InitState({x}){(Plugin.DevSeedMode.Value >= 2 ? " + MyRandom.random = new Random(x)" : "")} at generation start");
            }
            ReadRunConfig(out int cMap, out int cStage, out int cTier, out string cText);
            Plugin.Info($"[run] {role} generating: {cText}   (host announced: map={Coop.HostMap} stage={Coop.HostStage} tier={Coop.HostTier})");
            if (Coop.IsHost) { Coop.HostMap = cMap; Coop.HostStage = cStage; Coop.HostTier = cTier; }
            else if (Coop.IsClient && Coop.HostMap > 0 && (cMap != Coop.HostMap || cTier != Coop.HostTier || (Coop.HostStage >= 0 && cStage != Coop.HostStage)))
            {
                Plugin.Info("[run] WARNING client config differs from the host's even after the start gate");
                Coop.Toast($"Seu mapa/nivel e diferente do host (host: {(EMap)Coop.HostMap} nivel {Coop.HostTier + 1}; voce: {(EMap)cMap} nivel {cTier + 1})!", 3, 12f);
            }
            Plugin.Info($"[mapgen] START role={role} before: {Seeds()} testSeed={__instance.__4__this?.testSeed} hostRunId={Coop.RunId} consumed={Coop.ConsumedRunId}");

            if (Coop.IsHost)
            {
                Coop.RunId++;                         // new run: host does NOT force anything, the game rolls
                Coop.HasRunSeed = false;
            }
            else if (!Coop.IsClient && Plugin.DevForceSeed.Value != 0)
            {
                applyForced = true; forcedSeed = Plugin.DevForceSeed.Value;
                Apply(__instance.__4__this, forcedSeed);
                Plugin.Info($"[dev] solo ForceSeed {forcedSeed}: applied in prefix; now: {Seeds()}");
            }
            else if (Coop.IsClient)
            {
                if (Coop.HasRunSeed && (Coop.RunId != Coop.ConsumedRunId || Coop.RejoinThisGen))
                {
                    applyForced = true; forcedSeed = Coop.RunSeed; Coop.ConsumedRunId = Coop.RunId;
                    Apply(__instance.__4__this, forcedSeed);
                    Plugin.Info($"[mapgen] client: applying host seed {forcedSeed} (runId {Coop.RunId}) in prefix; now: {Seeds()}");
                    Coop.Toast("Gerando mapa com a seed do host...", 0, 3f);
                }
                else
                {
                    Plugin.Info("[mapgen] WARNING client has no FRESH host seed (host did not start a new run yet, or this one was already used)");
                    Coop.Toast("Host ainda nao iniciou uma run nova! Volte ao menu e espere o aviso 'Host iniciou a run'.", 2, 9f);
                }
            }
        }

        [HarmonyPostfix]
        static void Post(MapGenerationController._GenerateMap_d__39 __instance, bool __result)
        {
            if (!inGen) return;
            steps++;
            if (steps == 1)
            {
                Plugin.Info($"[mapgen] after 1st step: {Seeds()}");
                if (Coop.IsHost)
                {
                    // the game has rolled its seed now: that is the truth. Announce it to the client.
                    Coop.RunSeed = MapGenerationController.mapSeed; Coop.HasRunSeed = true;
                    Plugin.Info($"[mapgen] host: real seed is {Coop.RunSeed} (runId {Coop.RunId}), announcing");
                    if (Coop.Net.Connected) Coop.Toast("Run iniciada! O amigo ja pode iniciar a dele.", 1, 5f);
                    else Coop.Toast("Run iniciada, mas nenhum amigo conectado.", 2, 5f);
                }
                else if (applyForced && MapGenerationController.mapSeed != forcedSeed)
                {
                    Plugin.Info($"[mapgen] client: game rolled mapSeed {MapGenerationController.mapSeed} vs host {forcedSeed} => {(MapGenerationController.mapSeed == forcedSeed ? "ALREADY IDENTICAL (shared RNG works)" : "different, re-applying the host seed")}");
                    Apply(__instance.__4__this, forcedSeed);
                    Plugin.Info($"[mapgen] client: after re-apply: {Seeds()}");
                }
            }
            if (__result) return; // still running

            inGen = false;
            int actual = MapGenerationController.mapSeed;
            int fp = Fingerprint(__instance.__4__this);
            Coop.ActualSeed = actual; Coop.ActualFp = fp; Coop.HasActual = true; Coop.ActualGen = Coop.LastGenIdx;
            Coop.ObjCheckAt = UnityEngine.Time.realtimeSinceStartup + 6f;   // interactables spawn a bit after the map
            Plugin.Info($"[mapgen] DONE after {steps} steps: {Seeds()} fingerprint={fp}");
            if (Coop.IsHost || Coop.IsClient)
            {
                if (Coop.Net.Connected) Coop.Net.SendReliable(Coop.Pack(Msg.SeedCheck, w => { w.Write(actual); w.Write(fp); w.Write(Coop.LastGenIdx); }));
                Coop.OnLocalActual();
            }
        }
    }
}
