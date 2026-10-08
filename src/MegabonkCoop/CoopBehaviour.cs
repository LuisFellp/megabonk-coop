using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Assets.Scripts._Data.Hats;
using Assets.Scripts.Actors.Player;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MegabonkCoop
{
    public partial class CoopBehaviour : MonoBehaviour
    {
        public CoopBehaviour(IntPtr ptr) : base(ptr) { }

        static Net net => Coop.Net;

        // remote player
        GameObject ghost;               // root at the friend's player position (also the capsule fallback)
        Vector3 ghostTarget;
        float ghostYaw;                 // facing of the friend's character model
        bool haveRemote;
        bool remoteInRun;               // friend is inside a run (otherwise the ghost is hidden)
        int ghostCharId = -1;           // friend's ECharacter
        int builtCharId = -2;
        GameObject ghostModel;          // clone of the character prefab (children of ghost)
        Animator ghostAnim;
        string animParam;
        bool animParamIsBool;
        Vector3 feetOffset = new Vector3(0f, -1f, 0f);   // player transform -> feet, measured on the local player
        bool selfTest;
        float nextSession;

        // a dev/test instance (AutoRun) must NOT react to the real user's keyboard: Rewired reads devices even
        // without focus, so also turn the game's own input maps off.
        static bool Deaf => Plugin.AutoRun.Value != "off";
        static bool KeyDown(KeyCode k) => !Deaf && Input.GetKeyDown(k);
        static bool KeyHeld(KeyCode k) => !Deaf && Input.GetKey(k);
        static string TypedChars() => Deaf ? "" : Input.inputString;
        float nextDeaf;

        void KeepDeaf(float now)
        {
            if (!Deaf || now < nextDeaf) return;
            nextDeaf = now + 2f;
            try { Rewired.ReInput.players.GetPlayer(0).controllers.maps.SetAllMapsEnabled(false); } catch { }
            try { AudioListener.volume = 0f; } catch { }          // test instances are SILENT
        }
        bool lastAnimMoving;
        bool enemyErrLogged;
        int visualErrors;

        // run a visual update; after 3 failures stop trying (and say so once) instead of flooding the log
        void SafeVisual(Action a)
        {
            if (visualErrors >= 3) return;
            try { a(); }
            catch (Exception e)
            {
                visualErrors++;
                Plugin.Info($"[visual] error {visualErrors}/3: {e}");
                if (visualErrors >= 3) Plugin.Info("[visual] too many errors: friend visuals disabled until restart");
            }
        }
        int remoteHp = 1, remoteMaxHp = 1;
        bool remoteDead;
        byte remoteFlags;                          // bit0 crouching, bit1 jumping, bit2 grounded
        float crouchBase = 1f;                     // the model's own Y scale (1 for every prefab seen so far)
        float crouchK = 1f;                        // current vertical squash of the friend's model
        int remoteHat = -1, remoteSkin = -1;       // friend's equipped hat (EHat) and skin index
        int builtHat = -2, builtSkin = -2;
        GameObject ghostHat;
        string localHatNode;                       // name of MY hat attach node: the same node exists in every character model
        GUIStyle smallStyle;                  // F6: simulated friend next to me (tests model/arrow/minimap alone)

        // minimap marker (clone of the game's own player icon)
        GameObject mapIcon;
        Vector3 mapIconOffset;
        bool mapIconFailed;
        bool mapIconNoSrcLogged;

        // timers / stats
        float nextSend, nextSeed, nextPing, nextStatus, nextMapRetry;
        float lastRecvTime = -1f;
        bool stalled;
        float rtt = -1f;
        string lastScene = "";

        // HUD
        Texture2D arrowTex;
        GUIStyle labelStyle;

        void Awake()
        {
            Plugin.Info($"[env] game={Application.version} unity={Application.unityVersion} os={SystemInfo.operatingSystem} gpu={SystemInfo.graphicsDeviceName}");
            try { var cb = GUIUtility.systemCopyBuffer; Plugin.Info($"[env] clipboard read OK (len={(cb ?? "").Length})"); }
            catch (Exception e) { Plugin.Info("[env] clipboard read FAILED (paste button will not work): " + e.Message); }
            bestIp = FindBestLocalIp();
            ipText = Plugin.JoinIp.Value;
            Plugin.Info($"[env] best local ip for sharing = {bestIp}");
            Plugin.Info($"[env] Application.runInBackground={Application.runInBackground} (false = the game freezes when its window loses focus, e.g. alt-tab)");
            Plugin.Info($"[env] plugin={Plugin.Version} joinIp={Plugin.JoinIp.Value} port={Plugin.PortCfg.Value} verbose={Plugin.Verbose.Value}");
        }

        void Update()
        {
            TrackScene();
            KeepDeaf(Time.realtimeSinceStartup);
            if (Plugin.AutoRun.Value != "off") DevAuto();
            HandleChat();
            HandleTyping();
            NativeUiUpdate();
            DevShot(Time.realtimeSinceStartup);
            HandleMarkerKey(Time.realtimeSinceStartup); DevMarkerTest(Time.realtimeSinceStartup);
            HandleTeleportKey(Time.realtimeSinceStartup); DevTeleportTestTick(Time.realtimeSinceStartup);
            DevInteractTick(Time.realtimeSinceStartup);
            if (!logSaved && !string.IsNullOrEmpty(Plugin.DevLogDir.Value) && Time.realtimeSinceStartup > 30f) { logSaved = true; SaveLogToDesktop(); }
            AutoEnterTick(Time.realtimeSinceStartup);

            if (!autoDone && Time.realtimeSinceStartup > 8f)
            {
                autoDone = true;
                var mode = (Plugin.AutoStart.Value ?? "off").Trim().ToLowerInvariant();
                if (mode == "host") { Plugin.Info("[auto] AutoStart=host"); StartHost(); }
                else if (mode == "join") { Plugin.Info("[auto] AutoStart=join"); StartJoin(); }
            }
            if (KeyDown(KeyCode.F8)) StartHost("[key] F8");
            if (KeyDown(KeyCode.F9)) StartJoin("[key] F9");
            if (KeyDown(KeyCode.F10)) Disconnect("[key] F10");
            if (KeyDown(KeyCode.F7)) panelOpen = !panelOpen;
            if (KeyDown(KeyCode.F6) && !net.Active)
            {
                selfTest = !selfTest;
                Plugin.Info($"[key] F6 -> self test {(selfTest ? "ON" : "OFF")}");
                Coop.Toast(selfTest ? "TESTE: amigo simulado ao seu lado (entre numa run). F6 desliga." : "Teste desligado", 0, 5f);
                if (!selfTest) { DestroyRemote(); haveRemote = false; remoteInRun = false; }
            }
            if (net.Active && selfTest) { selfTest = false; DestroyRemote(); haveRemote = false; }
            if (selfTest && !net.Active) SafeVisual(UpdateSelfTest);
            if (!net.Active && Coop.ObjCheckAt > 0f && Time.realtimeSinceStartup >= Coop.ObjCheckAt) DoObjectCheck();   // solo: lets me test the checks alone
            if (!net.Active) return;

            var player = MyPlayer.Instance;
            float now = Time.realtimeSinceStartup;

            // ---- send state 20 Hz
            if (now >= nextSend)
            {
                nextSend = now + 0.05f;
                var p = player != null ? player.transform.position : Vector3.zero;
                float r = MyModelYaw(player);
                int ch = player != null ? (int)player.character : -1;
                if (player != null && Plugin.DevInteract.Value == "CHARS" && Time.realtimeSinceStartup > 30f) ch = (int)((Time.realtimeSinceStartup - 30f) / 4f) % 21;      // DEV: pretend to be every character in turn
                byte inRun = (byte)(player != null ? 1 : 0);
                int hp = 1, mhp = 1;
                try { var ph = player?.inventory?.playerHealth; if (ph != null) { hp = ph.hp; mhp = ph.maxHp; } } catch { }
                byte dead = (byte)(Coop.LocalDead ? 1 : 0);
                byte moveFlags = 0;
                try { var pm = player?.playerMovement; if (pm != null) moveFlags = (byte)((pm.crouching ? 1 : 0) | (pm.jumping ? 2 : 0) | (pm.grounded ? 4 : 0)); } catch { }
                float tStage = 0f, tRun = 0f, tDiff = 0f, tSwarm = 0f;
                try { tStage = Assets.Scripts.Utility.MyTime.stageTimer; tRun = Assets.Scripts.Utility.MyTime.runTimer; tDiff = Assets.Scripts.Utility.MyTime.difficultyTimer; tSwarm = Assets.Scripts.Utility.MyTime.finalSwarmTimer; } catch { }
                int hatId = -1, skinIdx = -1;
                try { ReadMyAppearance(player, out hatId, out skinIdx); } catch { }
                net.Send(Coop.Pack(Msg.PlayerState, w => { w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(r); w.Write(ch); w.Write(inRun); w.Write(hp); w.Write(mhp); w.Write(dead); w.Write(hatId); w.Write(skinIdx); w.Write(moveFlags); w.Write(tStage); w.Write(tRun); w.Write(tDiff); w.Write(tSwarm); }));
            }
            // ---- host re-announces seed
            if (Coop.IsHost && Coop.HasRunSeed && now >= nextSeed)
            {
                nextSeed = now + 0.5f;
                net.Send(Coop.Pack(Msg.Seed, w => { w.Write(Coop.RunSeed); w.Write(Coop.RunId); w.Write(Coop.HostMap); w.Write(Coop.HostStage); w.Write(Coop.HostTier); w.Write(Coop.HostSpawn.x); w.Write(Coop.HostSpawn.y); w.Write(Coop.HostSpawn.z); w.Write(Coop.HostSpawnRunId); w.Write(Coop.HostSpawnValid); }));
            }
            // ---- host keeps sending the shared session seed until the client acknowledges it
            if (Coop.IsHost && net.Connected && now >= nextSession)
            {
                nextSession = now + 1f;
                int ss = Coop.SessionSeed;
                net.Send(Coop.Pack(Msg.Session, w => { w.Write(ss); w.Write(Plugin.WireVersion); }));
            }
            // ---- ping every second
            if (now >= nextPing)
            {
                nextPing = now + 1f;
                float t = now;
                net.Send(Coop.Pack(Msg.Ping, w => w.Write(t)));
            }

            // ---- receive
            byte[] d;
            net.Pump(now);
            int budget = 400;                       // never loop forever on a flood of foreign datagrams
            while (budget-- > 0 && net.HasData())
            {
                d = net.Receive();
                if (d == null) continue;            // ignored (not from my friend)
                d = net.Unwrap(d);                 // acks / duplicates are consumed here
                if (d == null || d.Length < 1) continue;
                lastRecvTime = now;
                if (Coop.PeerLost) { Coop.PeerLost = false; Plugin.Info("[net] friend is back"); }
                if (stalled) { stalled = false; Plugin.Info("[net] packets resumed"); Coop.Toast("Conexao restabelecida", 1, 3f); }
                try
                {
                    using var br = new System.IO.BinaryReader(new System.IO.MemoryStream(d, 1, d.Length - 1));
                    switch ((Msg)d[0])
                    {
                        case Msg.PlayerState:
                            ghostTarget = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                            ghostYaw = br.ReadSingle();
                            ghostCharId = br.ReadInt32();
                            remoteInRun = br.ReadByte() != 0;
                            if (Coop.IsClient) Coop.HostInRun = remoteInRun;
                            remoteHp = br.ReadInt32(); remoteMaxHp = br.ReadInt32();
                            remoteDead = br.ReadByte() != 0;
                            remoteHat = br.ReadInt32(); remoteSkin = br.ReadInt32();
                            remoteFlags = br.ReadByte();
                            { float tS = br.ReadSingle(), tR = br.ReadSingle(), tD = br.ReadSingle(), tF = br.ReadSingle(); if (Coop.IsClient && remoteInRun && player != null) SyncTimers(now, tS, tR, tD, tF); }
                            if (!haveRemote) { haveRemote = true; { string myn = MyName(); net.SendReliable(Coop.Pack(Msg.Name, w => w.Write(myn))); } Plugin.Info($"[remote] first state received at {ghostTarget}"); Coop.Toast(Coop.IsHost ? "Amigo conectado! Inicie a run (o amigo espera o seu aviso)." : "Conectado ao host! Espere o aviso de que o host iniciou a run.", 1, 5f); }
                            break;
                        case Msg.Seed:
                            if (Coop.IsClient)
                            {
                                int sd = br.ReadInt32();
                                int rid = br.ReadInt32();
                                Coop.HostMap = br.ReadInt32(); Coop.HostStage = br.ReadInt32(); Coop.HostTier = br.ReadInt32();
                                { var hs = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()); int hsr = br.ReadInt32(); bool hsv = br.ReadBoolean(); if (hsv) { Coop.HostSpawn = hs; Coop.HostSpawnRunId = hsr; Coop.HostSpawnValid = true; } }
                                bool fresh = rid != Coop.RunId || !Coop.HasRunSeed;
                                Coop.RunSeed = sd; Coop.RunId = rid; Coop.HasRunSeed = true;
                                if (fresh)
                                {
                                    Plugin.Info($"[seed] client got host seed {sd} (runId {rid}, consumed {Coop.ConsumedRunId}); host run config: map={Coop.HostMap} stage={Coop.HostStage} tier={Coop.HostTier}");
                                    if (rid != Coop.ConsumedRunId)
                                    {
                                        if (MyPlayer.Instance != null) Coop.Toast("O HOST COMECOU UMA RUN NOVA e voce esta na antiga! Volte ao menu e clique JOGAR.", 3, 14f);
                                        else Coop.Toast("Host iniciou a run! Inicie a sua agora.", 1, 8f);
                                    }
                                }
                            }
                            break;
                        case Msg.SeedCheck:
                            {
                                int theirSeed = br.ReadInt32();
                                int theirFp = br.ReadInt32();
                                int theirGen = br.ReadInt32();
                                Coop.OnTheirResult(theirSeed, theirFp, theirGen);
                                // host answers with its own result so the client can confirm too
                                if (Coop.IsHost && Coop.HasActual) net.SendReliable(Coop.Pack(Msg.SeedCheck, w => { w.Write(Coop.ActualSeed); w.Write(Coop.ActualFp); w.Write(Coop.ActualGen); }));
                            }
                            break;
                        case Msg.Chat:
                            {
                                string who = br.ReadString();
                                string text = br.ReadString();
                                if (text.Length > 200) text = text.Substring(0, 200);
                                Coop.AddChat(who, text, false);
                            }
                            break;
                        case Msg.EnemySnap:
                        case Msg.EnemyDied:
                        case Msg.EnemyRemoved:
                        case Msg.EnemyDamage:
                            if (Plugin.SyncEnemies.Value) EnemySync.Handle((Msg)d[0], br, now);
                            break;
                        case Msg.Proj:
                        case Msg.ProjPos:
                        case Msg.ProjEnd:
                            if (Plugin.SyncProjectiles.Value) ProjectileSync.Handle((Msg)d[0], br);
                            break;
                        case Msg.TerrainCheck:
                            {
                                int tgen = br.ReadInt32();
                                int cnt = br.ReadByte();
                                var arr = new short[cnt];
                                for (int i = 0; i < cnt; i++) arr[i] = br.ReadInt16();
                                Coop.TerrainTheirs = arr; Coop.TerrainTheirsGen = tgen;
                                Plugin.Info($"[terraincheck] friend reported {cnt} ground samples");
                                if (Coop.IsHost && Coop.TerrainMine != null) { var mine = Coop.TerrainMine; int mg = Coop.TerrainMineGen; net.SendReliable(Coop.Pack(Msg.TerrainCheck, w => { w.Write(mg); w.Write((byte)mine.Length); foreach (var v in mine) w.Write(v); })); }
                                Coop.CompareTerrain();
                            }
                            break;
                        case Msg.Session:
                            if (Coop.IsClient)
                            {
                                int ss = br.ReadInt32();
                                string hostVer = br.BaseStream.Position < br.BaseStream.Length ? br.ReadString() : "<=0.8.0";
                                Coop.PeerVersion = hostVer;
                                if (hostVer != Plugin.WireVersion)
                                {
                                    if (!Coop.VersionMismatch) { Coop.VersionMismatch = true; Plugin.Info($"[version] MISMATCH: host {hostVer}, me {Plugin.Version}"); Coop.Toast($"VERSOES DIFERENTES! Host: {hostVer}, voce: {Plugin.Version}. Atualizem os DOIS para a mesma versao.", 3, 20f); }
                                    net.Send(Coop.Pack(Msg.SessionAck, w => { w.Write(-1); w.Write(Plugin.WireVersion); }));
                                    break;
                                }
                                Coop.VersionMismatch = false;
                                if (!Coop.HaveSession || Coop.SessionSeed != ss)
                                {
                                    Coop.SessionSeed = ss; Coop.HaveSession = true;
                                    Plugin.Info($"[seed] client received the host's session seed {ss}");
                                    Coop.Toast("Seed compartilhada recebida. Sincronizado com o host.", 1, 4f);
                                }
                                net.Send(Coop.Pack(Msg.SessionAck, w => { w.Write(ss); w.Write(Plugin.WireVersion); }));
                            }
                            break;
                        case Msg.SessionAck:
                            if (Coop.IsHost)
                            {
                                int ss = br.ReadInt32();
                                string cliVer = br.BaseStream.Position < br.BaseStream.Length ? br.ReadString() : "<=0.8.0";
                                Coop.PeerVersion = cliVer;
                                if (cliVer != Plugin.WireVersion)
                                {
                                    if (!Coop.VersionMismatch) { Coop.VersionMismatch = true; Plugin.Info($"[version] MISMATCH: friend {cliVer}, me {Plugin.Version}"); Coop.Toast($"VERSOES DIFERENTES! Voce: {Plugin.Version}, amigo: {cliVer}. Atualizem os DOIS para a mesma versao.", 3, 20f); }
                                    break;
                                }
                                if (ss == Coop.SessionSeed && !Coop.ClientReady)
                                {
                                    Coop.ClientReady = true;
                                    Plugin.Info("[seed] client acknowledged the session seed: READY");
                                    Coop.Toast("Amigo pronto e sincronizado. Pode iniciar a run!", 1, 6f);
                                }
                            }
                            break;
                        case Msg.ObjCheck:
                            {
                                Coop.ObjTheirsGen = br.ReadInt32();
                                Coop.ObjTheirs = br.ReadString();
                                Plugin.Info($"[objcheck] friend reported: {Coop.ObjTheirs}");
                                // host answers so the client can compare too (only if I already counted mine)
                                if (Coop.IsHost && Coop.ObjMine != null) { int og = Coop.ObjMineGen; string om = Coop.ObjMine; net.SendReliable(Coop.Pack(Msg.ObjCheck, w => { w.Write(og); w.Write(om); })); }
                                Coop.CompareObjects();
                            }
                            break;
                        case Msg.Const:
                            ConstSync.Handle(br);
                            break;
                        case Msg.Share:
                            Share.Handle(br);
                            break;
                        case Msg.Marker:
                            HandleMarker(br);
                            break;
                        case Msg.Catchup:
                            Share.HandleCatchup(br);
                            break;
                        case Msg.Interact:
                            InteractNet.Handle(br);
                            break;
                        case Msg.Stats:
                            CoopStats.Handle(br);
                            break;
                        case Msg.Name:
                            { string nm = br.ReadString(); if (nm.Length > 24) nm = nm.Substring(0, 24); Coop.PeerName = nm; Plugin.Info("[net] friend's name: " + nm); }
                            break;
                        case Msg.Event:
                            {
                                string who = br.ReadString();
                                string text = br.ReadString();
                                if (text.Length > 120) text = text.Substring(0, 120);
                                Coop.AddEvent(who, text);
                            }
                            break;
                        case Msg.PlayerDied:
                            {
                                string who = br.ReadString();
                                remoteDead = true;
                                Coop.Toast($"{who} morreu!", 3, 8f);
                            }
                            break;
                        case Msg.Portal:
                            {
                                string who = br.ReadString();
                                Coop.PortalKind = br.ReadByte();
                                if (Plugin.PortalFollow.Value)
                                {
                                    Coop.PortalFollowAt = now + 1f;
                                    Coop.Toast($"{who} entrou no portal! Indo junto em 1s...", 0, 4f);
                                }
                                else Coop.Toast($"{who} entrou no portal! Va ate o seu portal.", 0, 6f);
                            }
                            break;
                        case Msg.Ping:
                            { float t = br.ReadSingle(); net.Send(Coop.Pack(Msg.Pong, w => w.Write(t))); }
                            break;
                        case Msg.Pong:
                            rtt = (now - br.ReadSingle()) * 1000f;
                            break;
                        default:
                            Plugin.Info($"[net] unknown message type {d[0]} len={d.Length}");
                            break;
                    }
                }
                catch (Exception e) { Plugin.Info($"[net] bad packet type={d[0]} len={d.Length}: {e.Message}"); }
            }

            // ---- connection stall detection
            if (lastRecvTime > 0 && !stalled && now - lastRecvTime > 3f)
            {
                stalled = true;
                Coop.Toast("Sem sinal do amigo! (jogo fechado? VPN caiu?)", 2, 6f);
                Plugin.Info($"[net] WARNING no packets for {now - lastRecvTime:F1}s (friend closed game / VPN down / firewall?)");
            }

            if (stalled && !Coop.PeerLost && now - lastRecvTime > 12f)
            {
                Coop.PeerLost = true; Coop.InvincibleUntil = 0f;
                try { EnemySync.Reset("peer lost"); ProjectileSync.Reset(); } catch { }
                Plugin.Info("[net] no packets for 12 s: friend considered GONE, pause and normal damage work again until he returns");
                Coop.Toast("Amigo sumiu. O jogo volta ao normal (pode pausar) ate ele voltar.", 2, 6f);
            }

            // ---- client: the host left the run (died / back to the menu) while I am still playing: its enemies would freeze, so they become mine
            if (Coop.IsClient && haveRemote)
            {
                bool hostGone = !remoteInRun && MyPlayer.Instance != null && lastScene != "MainMenu" && !Coop.PeerLost && Coop.Synced;
                if (hostGone)
                {
                    if (hostGoneSince < 0f) hostGoneSince = now;
                    else if (!Coop.HostLeftRun && now - hostGoneSince > 8f)
                    {
                        Coop.HostLeftRun = true;
                        try { EnemySync.Reset("host left the run"); ProjectileSync.Reset(); } catch { }
                        Plugin.Info("[net] the host is no longer in the run: enemies are local again");
                        Coop.Toast("O host saiu da run. Voce segue sozinho: os inimigos voltam ao normal.", 2, 7f);
                    }
                }
                else
                {
                    hostGoneSince = -1f;
                    if (Coop.HostLeftRun && remoteInRun) { Coop.HostLeftRun = false; Plugin.Info("[net] the host is back in a run: enemies are the host's again"); }
                }
            }

            // ---- the game must never stay paused in coop; invincible whenever I cannot act (any window open)
            if (Plugin.NoPause.Value && net.Connected && !Coop.PeerLost && player != null) AntiPause(now, player);

            // ---- hash my map objects a few seconds after the map was built, compare with the friend
            if (Coop.ObjCheckAt > 0f && now >= Coop.ObjCheckAt) DoObjectCheck();

            // ---- follow the friend into the stage portal
            if (Coop.PortalFollowAt > 0f && now >= Coop.PortalFollowAt) DoPortalFollow();

            // ---- move ghost + minimap icon
            try { CoopStats.Tick(now); Share.Tick(now); DevShareTest(now); } catch (Exception e) { if (!enemyErrLogged) { enemyErrLogged = true; Plugin.Info("[share] tick error: " + e.Message); } }
            try { ConstSync.SenderTick(now); } catch (Exception e) { if (!enemyErrLogged) { enemyErrLogged = true; Plugin.Info("[const] tick error: " + e.Message); } }
            try { ProjectileSync.Tick(now, Time.deltaTime); } catch (Exception e) { if (!enemyErrLogged) { enemyErrLogged = true; Plugin.Info("[proj] tick error: " + e.Message); } }
            if (Plugin.SyncEnemies.Value && !Coop.PeerLost && !Coop.HostLeftRun)
            {
                try
                {
                    if (Coop.IsHost) EnemySync.HostTick(now, player, ghost);
                    else EnemySync.ClientTick(now, Time.deltaTime);
                }
                catch (Exception e) { if (!enemyErrLogged) { enemyErrLogged = true; Plugin.Info("[enemy] tick error (logged once): " + e); } }
            }
            SafeVisual(() => { ApplyRemoteVisual(player); UpdateMapIcon(player); });

            // ---- periodic status line
            if (Plugin.Verbose.Value && now >= nextStatus)
            {
                nextStatus = now + 15f;
                var lp = player != null ? player.transform.position : Vector3.zero;
                float dist = haveRemote ? Vector3.Distance(lp, ghostTarget) : -1f;
                Plugin.Info($"[status] role={(Coop.IsHost ? "host" : "client")} peer={(net.Connected ? "yes" : "no")} scene={lastScene} " +
                            $"rtt={(rtt < 0 ? "n/a" : rtt.ToString("F0") + "ms")} sent={net.SentPackets}pk/{net.SentBytes}B recv={net.RecvPackets}pk/{net.RecvBytes}B " +
                            $"seed={(Coop.HasRunSeed ? Coop.RunSeed.ToString() : "none")} me={lp} friend={(haveRemote ? ghostTarget.ToString() : "n/a")} dist={dist:F1} " +
                            $"ghost={(ghost != null)} mapIcon={(mapIcon != null)} player={(player != null)} {EnemySync.Stats()} {ProjectileSync.Stats()} {net.RelStats()}");
                EnemySync.LogSources();
            }
        }

        bool autoDone;

        // ---- DEV: drive the menus by code so a run can be started without touching the keyboard
        int devStep, devPortalTries, devMapTries;
        float devNext;

        // DEV: "PORTAL" = at 40 s THIS side takes the stage portal (to prove the other side follows, in both directions)
        bool portalDone;
        void DevPortal(float now)
        {
            if (portalDone || now < 40f || !Coop.Synced || MyPlayer.Instance == null) return;
            portalDone = true;
            Plugin.Info($"[portal-test] {(Coop.IsHost ? "host" : "client")} calls LoadNextStage()");
            try { Assets.Scripts.Managers.MapController.LoadNextStage(); } catch (Exception e) { Plugin.Info("[portal-test] threw: " + e.Message); }
        }

        // DEV: "MENU" = at 45 s THIS side quits to the main menu (what the friend must do when I leave the run)
        float chaosNext;
        bool menuDone; float menuLogAt;
        void DevMenu(float now)
        {
            if (!menuDone && now >= 45f && MyPlayer.Instance != null && Coop.Synced) { menuDone = true; menuLogAt = now + 4f; Plugin.Info("[menu-test] leaving to the main menu"); SceneManager.LoadScene("MainMenu"); }
            if (menuDone && now >= menuLogAt && menuLogAt < 100000f)
            {
                menuLogAt = now + 4f;
                int n = Assets.Scripts.Managers.EnemyManager.Instance != null && Assets.Scripts.Managers.EnemyManager.Instance.enemies != null ? Assets.Scripts.Managers.EnemyManager.Instance.enemies.Count : -1;
                Plugin.Info($"[menu-test] scene={lastScene} player={(MyPlayer.Instance != null)} remoteInRun={remoteInRun} hostLeftRun={Coop.HostLeftRun} enemies={n} blockedSpawns={EnemySync.BlockedSpawns}");
            }
        }

        // DEV: "CHAT" = at 40 s send a chat line through the real SendChat() path, and 5 s later a very long one
        int chatTestStep; float chatTestAt;
        void DevChat(float now)
        {
            if (now < 40f || !Coop.Synced || !net.Connected) return;
            if (chatTestStep == 0) { chatTestStep = 1; chatTestAt = now + 5f; chatText = "ola do " + (Coop.IsHost ? "host" : "cliente") + " (teste)"; SendChat(); chatText = ""; Plugin.Info("[chat-test] sent 1"); }
            else if (chatTestStep == 1 && now >= chatTestAt) { chatTestStep = 2; chatText = new string('x', 300); SendChat(); chatText = ""; Plugin.Info("[chat-test] sent 2 (300 chars, must be cut to 120)"); }
        }

        // DEV: "KILL" = my player dies at 45 s (to see what the other side and my own side do)
        bool killDone; float killLogAt;
        void DevKillSelf(float now)
        {
            if (!killDone && now >= 45f && MyPlayer.Instance != null && Coop.Synced)
            {
                killDone = true; killLogAt = now + 5f;
                Plugin.Info($"[kill-test] killing my player now (scene {lastScene})");
                try { MyPlayer.Instance.inventory.playerHealth.KillPlayer(); } catch (Exception e) { Plugin.Info("[kill-test] KillPlayer threw: " + e.Message); }
            }
            if (killDone && now >= killLogAt && killLogAt < 100000f)
            {
                killLogAt = now + 5f;
                Plugin.Info($"[kill-test] +{now - 45f:F0}s: scene={lastScene} player={(MyPlayer.Instance != null)} remoteDead={remoteDead} haveRemote={haveRemote} hostLeftRun={Coop.HostLeftRun} paused={Assets.Scripts.Utility.MyTime.paused} timeScale={Time.timeScale}");
            }
        }

        // DEV: "WEAPONS" = every 5 s equip the next weapon (from 35 s) so each weapon's projectiles go through the visual sync
        List<EWeapon> weaponQueue; float weaponNext; int weaponIdx;
        void DevGiveWeapons(float now)
        {
            if (now < 35f || MyPlayer.Instance == null || !Coop.Synced) return;
            if (weaponQueue == null)
            {
                weaponQueue = new List<EWeapon>();
                foreach (var v in Enum.GetValues(typeof(EWeapon))) { var w = (EWeapon)v; string only = Plugin.DevInteract.Value.StartsWith("WEAPON:") ? Plugin.DevInteract.Value.Substring(7) : null; if (w.ToString() != "None" && (only == null || w.ToString() == only)) weaponQueue.Add(w); }
                weaponNext = now; Plugin.Info($"[weapons-test] {weaponQueue.Count} weapons to try");
            }
            if (weaponIdx >= weaponQueue.Count || now < weaponNext) return;
            var ew = weaponQueue[weaponIdx++]; weaponNext = now + 5f;
            try
            {
                var wd = DataManager.Instance.GetWeapon(ew);
                if (wd == null) { Plugin.Info($"[weapons-test] {ew}: no WeaponData"); return; }
                MyPlayer.Instance.inventory.weaponInventory.AddWeapon(wd, null);
                Plugin.Info($"[weapons-test] {ew}: added (projectile sync so far: {ProjectileSync.Stats()})");
            }
            catch (Exception e) { Plugin.Info($"[weapons-test] {ew}: threw {e.GetType().Name}: {e.Message}"); }
        }

        // DEV: "ITEMS" = give the player EVERY item at 35 s, then watch what the sync layer sees (exceptions, blocked spawns)
        bool itemsGiven; float itemsLogAt; int itemsBlockedAtStart;
        void DevGiveItems(float now, string want)
        {
            if (!itemsGiven && now >= 35f && MyPlayer.Instance != null && Coop.Synced)
            {
                itemsGiven = true; itemsBlockedAtStart = EnemySync.BlockedSpawns;
                int ok = 0, bad = 0; var badNames = new List<string>();
                var inv = MyPlayer.Instance.inventory.itemInventory;
                foreach (var v in Enum.GetValues(typeof(Assets.Scripts.Inventory__Items__Pickups.Items.EItem)))
                {
                    var e = (Assets.Scripts.Inventory__Items__Pickups.Items.EItem)v;
                    if (want.StartsWith("ITEM:") && e.ToString() != want.Substring(5)) continue;
                    if (e.ToString() == "WeebHeadset") continue;        // a game debug item that breaks its own inventory sorting
                    try { inv.AddItem(e); ok++; } catch (Exception ex) { bad++; if (badNames.Count < 8) badNames.Add($"{e}:{ex.GetType().Name}"); }
                }
                Plugin.Info($"[items-test] gave {ok} items ({bad} threw: {string.Join(", ", badNames)}); blocked spawns so far {EnemySync.BlockedSpawns}");
                itemsLogAt = now + 10f;
            }
            if (itemsGiven && now >= itemsLogAt && itemsLogAt > 0f)
            {
                itemsLogAt = now + 10f;
                int n = Assets.Scripts.Managers.EnemyManager.Instance != null && Assets.Scripts.Managers.EnemyManager.Instance.enemies != null ? Assets.Scripts.Managers.EnemyManager.Instance.enemies.Count : -1;
                Plugin.Info($"[items-test] +{now - 35f:F0}s enemies={n} blocked spawns since the items: {EnemySync.BlockedSpawns - itemsBlockedAtStart}");
            }
        }

        // DEV: "*" = interact with ONE object of every distinct interactable type (3 s apart) and report exceptions / state afterwards
        List<BaseInteractable> scanQueue; float scanNext; int scanIdx;
        static string BaseName(string n) => System.Text.RegularExpressions.Regex.Replace(n ?? "?", @"\(Clone\)|[\s_\-]*\d+$|\s*\(\d+\)", "").Trim();

        void DevScanTick(float now)
        {
            if (now < 40f || MyPlayer.Instance == null || !Coop.Synced) return;
            if (scanQueue == null)
            {
                scanQueue = new List<BaseInteractable>(); scanNext = now; scanIdx = 0;
                var seen = new HashSet<string>();
                foreach (var it in UnityEngine.Object.FindObjectsOfType<BaseInteractable>())
                {
                    if (it == null || it.gameObject == null) continue;
                    string bn = BaseName(it.gameObject.name);
                    if (seen.Add(bn)) scanQueue.Add(it);
                }
                Plugin.Info($"[scan] {scanQueue.Count} distinct interactable types to try");
            }
            if (scanIdx >= scanQueue.Count || now < scanNext) return;
            var o = scanQueue[scanIdx++]; scanNext = now + 3f;
            if (o == null || o.gameObject == null) return;
            string name = BaseName(o.gameObject.name);
            bool canBefore = false, canAfter = false, ok = false; string exc = "none";
            int enBefore = Assets.Scripts.Managers.EnemyManager.Instance != null && Assets.Scripts.Managers.EnemyManager.Instance.enemies != null ? Assets.Scripts.Managers.EnemyManager.Instance.enemies.Count : -1;
            int blockedBefore = EnemySync.BlockedSpawns;
            try { canBefore = o.CanInteract(); } catch { }
            try { ok = o.Interact(); } catch (Exception e) { exc = e.GetType().Name + ": " + e.Message; }
            try { canAfter = o.CanInteract(); } catch { }
            int enAfter = Assets.Scripts.Managers.EnemyManager.Instance != null && Assets.Scripts.Managers.EnemyManager.Instance.enemies != null ? Assets.Scripts.Managers.EnemyManager.Instance.enemies.Count : -1;
            Plugin.Info($"[scan] {name}: can={canBefore} -> Interact()={ok} exception={exc} can-after={canAfter} enemies {enBefore}->{enAfter} blockedSpawns +{EnemySync.BlockedSpawns - blockedBefore} type={o.GetType().Name}");
        }

        bool logSaved;
        bool interactDone; float interactLogUntil, interactNextLog;
        void DevInteractTick(float now)
        {
            string want = Plugin.DevInteract.Value;
            if (string.IsNullOrEmpty(want)) return;
            if (want == "*") { DevScanTick(now); return; }
            if (want == "ITEMS" || want.StartsWith("ITEM:")) { DevGiveItems(now, want); return; }
            if (want == "WEAPONS" || want.StartsWith("WEAPON:")) { DevGiveWeapons(now); return; }
            if (want == "KILL") { DevKillSelf(now); return; }
            if (want == "CHAT") { DevChat(now); return; }
            if (want == "CHAOS")
            {
                DevGiveItems(now, "ITEMS"); DevGiveWeapons(now); DevChat(now);
                if (now > 40f && now >= chaosNext) { chaosNext = now + 9f; DoMarker(now); }
                return;
            }
            if (want == "MENU") { DevMenu(now); return; }
            if (want == "PORTAL") { DevPortal(now); return; }
            if (!interactDone && now >= 40f && MyPlayer.Instance != null && Coop.Synced)
            {
                interactDone = true;
                try
                {
                    foreach (var it in UnityEngine.Object.FindObjectsOfType<BaseInteractable>())
                    {
                        if (it == null || it.gameObject == null || !it.gameObject.name.StartsWith(want)) continue;
                        bool can = false; try { can = it.CanInteract(); } catch { }
                        int before = Assets.Scripts.Managers.EnemyManager.Instance != null && Assets.Scripts.Managers.EnemyManager.Instance.enemies != null ? Assets.Scripts.Managers.EnemyManager.Instance.enemies.Count : -1;
                        Plugin.Info($"[interact-test] '{it.gameObject.name}' at {it.transform.position}: CanInteract={can}; enemies before={before}, blocked spawns so far={EnemySync.BlockedSpawns}");
                        bool ok = false; try { ok = it.Interact(); } catch (Exception e) { Plugin.Info("[interact-test] Interact threw: " + e.Message); }
                        Plugin.Info($"[interact-test] Interact() returned {ok}");
                        interactLogUntil = now + 20f; interactNextLog = now + 2f;
                        break;
                    }
                    if (interactLogUntil == 0f) Plugin.Info($"[interact-test] no interactable named '{want}*' found");
                }
                catch (Exception e) { Plugin.Info("[interact-test] failed: " + e.Message); }
            }
            if (interactLogUntil > 0f && now < interactLogUntil && now >= interactNextLog)
            {
                interactNextLog = now + 2f;
                int n = Assets.Scripts.Managers.EnemyManager.Instance != null && Assets.Scripts.Managers.EnemyManager.Instance.enemies != null ? Assets.Scripts.Managers.EnemyManager.Instance.enemies.Count : -1;
                Plugin.Info($"[interact-test] +{now - (interactLogUntil - 20f):F0}s: enemies={n}, blocked spawns={EnemySync.BlockedSpawns}");
            }
        }

        float lastShot; int shotCount;
        void DevShot(float now)
        {
            if (Plugin.DevShotAt.Value <= 0f || now < Plugin.DevShotAt.Value + shotCount * 8f || shotCount >= 4) return;
            shotCount++;
            try
            {
                string dir = System.IO.Path.Combine(Application.dataPath, "../shots"); System.IO.Directory.CreateDirectory(dir);
                string f = System.IO.Path.Combine(dir, $"shot_{(Coop.IsHost ? "host" : Coop.IsClient ? "client" : "solo")}_{shotCount}.png");
                ScreenCapture.CaptureScreenshot(f);
                Plugin.Info("[shot] " + f);
            }
            catch (Exception e) { Plugin.Info("[shot] failed: " + e.Message); }
        }

        void DevAuto()
        {
            if (Plugin.AutoRun.Value == "ui") return;      // dev: deaf + silent but does not start anything by itself
            float now = Time.realtimeSinceStartup;
            if (now < devNext) return;
            try
            {
                switch (devStep)
                {
                    case 0:   // wait for the main menu
                        if (lastScene == "MainMenu" && now > 8f && UnityEngine.Object.FindObjectOfType<MainMenu>() != null
                            && (Plugin.AutoRun.Value != "client" || (Coop.IsClient && Coop.HasRunSeed && Coop.RunId != Coop.ConsumedRunId))
                            && (Plugin.AutoRun.Value != "host" || Coop.ClientReady))
                        {
                            Application.runInBackground = true;
                            Plugin.Info("[dev] AutoRun: MainMenu ready -> GoToMapSelection()");
                            UnityEngine.Object.FindObjectOfType<MainMenu>().GoToMapSelection();
                            devStep = 1; devNext = now + 3f;
                        }
                        break;
                    case 1:
                        {
                            var ui = UnityEngine.Object.FindObjectOfType<MapSelectionUi>();
                            var mm = UnityEngine.Object.FindObjectOfType<MainMenu>();
                            if (ui == null && mm != null && mm.mapSelectionUi != null)
                            {
                                ui = mm.mapSelectionUi;
                                if (!ui.gameObject.activeInHierarchy && devMapTries < 2)
                                {
                                    devMapTries++;
                                    Plugin.Info("[dev] AutoRun: map screen is closed, activating MainMenu.mapSelectionUi");
                                    ui.gameObject.SetActive(true);
                                    devNext = now + 2f; break;
                                }
                                if (!ui.gameObject.activeInHierarchy) Plugin.Info("[dev] AutoRun: map screen still not active; calling StartMap() on it anyway");
                            }
                            if (ui == null) { Plugin.Info("[dev] AutoRun: MapSelectionUi not found yet"); devNext = now + 1.5f; break; }
                            if (Plugin.AutoRun.Value == "client" && Plugin.DevClientMap.Value > 0 && ui.runConfig != null)
                            {
                                var other = DataManager.Instance.GetMap((Assets.Scripts._Data.MapsAndStages.EMap)Plugin.DevClientMap.Value);
                                if (other != null) { ui.runConfig.mapData = other; ui.runConfig.mapTierIndex = 1; if (other.stages != null && other.stages.Length > 0) ui.runConfig.stageData = other.stages[0]; Plugin.Info($"[dev] client deliberately picked {other.eMap} tier 2 to prove the host's config overrides it"); }
                            }
                            if (Plugin.AutoRun.Value != "client" && Plugin.DevHostMap.Value > 0 && ui.runConfig != null)
                            {
                                var pick = DataManager.Instance.GetMap((Assets.Scripts._Data.MapsAndStages.EMap)Plugin.DevHostMap.Value);
                                if (pick != null)
                                {
                                    ui.runConfig.mapData = pick; ui.runConfig.mapTierIndex = Plugin.DevHostTier.Value;
                                    if (pick.stages != null && pick.stages.Length > 0) ui.runConfig.stageData = pick.stages[0];
                                    Plugin.Info($"[dev] picked {pick.eMap} tier {Plugin.DevHostTier.Value + 1}, stage '{(ui.runConfig.stageData != null ? ui.runConfig.stageData.name : "?")}' ({(pick.stages != null ? pick.stages.Length : 0)} stages)");
                                }
                                else
                                {
                                    var names = new List<string>();
                                    try { foreach (var mdd in DataManager.Instance.maps) names.Add(mdd != null ? mdd.eMap.ToString() : "null"); } catch { }
                                    Plugin.Info($"[dev] GetMap({Plugin.DevHostMap.Value}) returned null. DataManager.maps = [{string.Join(", ", names)}]");
                                }
                            }
                            Plugin.Info("[dev] AutoRun: MapSelectionUi.StartMap()");
                            ui.StartMap();
                            devStep = 2; devNext = now + 2f;
                        }
                        break;
                    case 2:   // wait until the player exists in the run
                        if (MyPlayer.Instance != null && lastScene != "MainMenu")
                        {
                            Plugin.Info($"[dev] AutoRun: in run (scene {lastScene}); enabling self test in 6s");
                            devStep = 3; devNext = now + 6f;
                        }
                        break;
                    case 3:
                        if (Plugin.AutoRun.Value == "solo" && !selfTest) { selfTest = true; Plugin.Info("[dev] AutoRun: self test ON"); }
                        devStep = Plugin.DevPortalTest.Value && Plugin.AutoRun.Value == "host" ? 5 : 4;
                        devNext = Plugin.DevPortalTest.Value ? now + 22f : now + 3600f;
                        break;
                    case 5:   // DEV: try to enter the stage portal to exercise the stage change + the client following
                        {
                            devPortalTries++;
                            int total = 0; InteractablePortal usable = null;
                            foreach (var pt in UnityEngine.Object.FindObjectsOfType<InteractablePortal>())
                            {
                                total++;
                                bool can = false; try { can = pt.CanInteract(); } catch { }
                                Plugin.Info($"[dev] portal '{pt.gameObject.name}' at {pt.transform.position} CanInteract={can}");
                                if (can && usable == null) usable = pt;
                            }
                            // the real portal only exists after the boss: call the game's stage loader directly
                            Plugin.Info($"[dev] calling MapController.{(Plugin.DevFinalTest.Value ? "LoadFinalStage" : "LoadNextStage")}() ({total} portal objects exist)");
                            try { if (Plugin.DevFinalTest.Value) Assets.Scripts.Managers.MapController.LoadFinalStage(); else Assets.Scripts.Managers.MapController.LoadNextStage(); } catch (Exception ex) { Plugin.Info("[dev] LoadNextStage failed: " + ex.Message); }
                            devStep = 6; devNext = now + 3600f;
                        }
                        break;
                }
            }
            catch (Exception e) { Plugin.Info("[dev] AutoRun error: " + e.Message); devNext = now + 3f; }
        }

        // 81 coarse points (100 m apart, whole map) + 81 fine points (20 m apart around the centre = the playable area)
        // Only the TERRAIN's own collider counts. Anything else standing on it (enemies, gems, projectiles, players, props) is at a
        // different place on each PC at any given moment and made the check report "different ground" once enemies had spawned.
        static Transform TerrainRoot()
        {
            try
            {
                var c = UnityEngine.Object.FindObjectOfType<MapGenerationController>();
                var r = c != null ? c.proceduralMeshRenderer : null;
                if (r != null) return r.transform;
            }
            catch { }
            return null;
        }

        static bool terrainLogged, terrainDbg;

        // The terrain's real collider is the object "CombinedCollisionMesh" (layer 8). Not a child of the visual terrain mesh, and the world also has a flat
        // "WorldCollider" (layer 12, y=-100), props, shrines (layer 9), enemies... so a plain ray gave different answers whenever something stood on the ground.
        static int TerrainMask(out string how)
        {
            try
            {
                var go = GameObject.Find("CombinedCollisionMesh");
                if (go != null) { how = $"'CombinedCollisionMesh' layer {go.layer}"; return 1 << go.layer; }
                var root = TerrainRoot();
                if (root != null) { how = $"terrain renderer '{root.name}' layer {root.gameObject.layer}"; return 1 << root.gameObject.layer; }
            }
            catch { }
            how = "none"; return 0;
        }

        static short[] SampleTerrain(out int hits)
        {
            var h = new short[162];
            int idx = 0; hits = 0;
            int mask = TerrainMask(out string how);
            if (!terrainLogged) { terrainLogged = true; Plugin.Info($"[terraincheck] ground = {how} (mask {mask})"); }
            if (!terrainDbg)
            {
                terrainDbg = true;
                foreach (var pt in new[] { new Vector2(0, 0), new Vector2(100, 0) })
                {
                    try
                    {
                        var all = Physics.RaycastAll(new Vector3(pt.x, 400f, pt.y), Vector3.down, 900f);
                        var sb = new System.Text.StringBuilder();
                        foreach (var hh in all) { if (hh.collider == null) continue; sb.Append($" [{hh.collider.name} L{hh.collider.gameObject.layer} y={hh.point.y:F1}]"); }
                        Plugin.Info($"[terraincheck] rays at ({pt.x},{pt.y}) hit {all.Length}:{sb}");
                    }
                    catch (Exception e) { Plugin.Info("[terraincheck] debug ray failed: " + e.Message); }
                }
            }
            for (int pass = 0; pass < 2; pass++)
            {
                float step = pass == 0 ? 100f : 20f;
                for (int gx = -4; gx <= 4; gx++)
                    for (int gz = -4; gz <= 4; gz++)
                    {
                        short v = 0x7FFF;
                        var origin = new Vector3(gx * step, 400f, gz * step);
                        RaycastHit hit;
                        bool got = mask != 0 ? Physics.Raycast(origin, Vector3.down, out hit, 900f, mask, QueryTriggerInteraction.Ignore) : Physics.Raycast(origin, Vector3.down, out hit, 900f);
                        if (got) { v = (short)Mathf.Clamp(Mathf.RoundToInt(hit.point.y * 10f), -30000, 30000); hits++; }
                        h[idx++] = v;
                    }
            }
            return h;
        }

        short[] terrainPrev; int terrainStabTries;

        // ground height on a 9x9 grid (100 m apart) by ray-casting straight down
        void DoTerrainCheck()
        {
            try
            {
                var h = SampleTerrain(out int hits);
                Coop.TerrainMine = h; Coop.TerrainMineGen = Coop.ActualGen;
                int th = 17; foreach (var v in h) th = unchecked(th * 31 + v);
                Plugin.Info("[terraincheck] ARRAY=" + string.Join(",", h));
                Plugin.Info($"[terraincheck] mine: {hits}/162 ground hits; TERRAIN HASH={th}; coarse first row: {h[0]} {h[1]} {h[2]} {h[3]} {h[4]}; centre(coarse)={h[40]} centre(fine)={h[81 + 40]}; spawn(player)={(MyPlayer.Instance != null ? MyPlayer.Instance.transform.position.ToString() : "n/a")}");
                if (net.Active && net.Connected) { int mg = Coop.TerrainMineGen; net.SendReliable(Coop.Pack(Msg.TerrainCheck, w => { w.Write(mg); w.Write((byte)h.Length); foreach (var v in h) w.Write(v); })); }
                Coop.CompareTerrain();
            }
            catch (Exception e) { Plugin.Info("[terraincheck] failed: " + e.Message); }
        }

        float invWindowSince = -1f; bool invCapLogged;
        int pauseFixes;
        float lastAntiPauseLog;

        void AntiPause(float now, MyPlayer player)
        {
            try
            {
                bool wasPaused = false;
                try { wasPaused = Assets.Scripts.Utility.MyTime.paused; } catch { }
                if (wasPaused)
                {
                    Assets.Scripts.Utility.MyTime.Unpause();
                    pauseFixes++;
                    if (now - lastAntiPauseLog > 5f) { lastAntiPauseLog = now; Plugin.Info($"[pause] game was PAUSED (MyTime.paused): un-paused by the watchdog (x{pauseFixes})"); }
                }
                if (Time.timeScale < 0.001f)
                {
                    Time.timeScale = 1f;
                    pauseFixes++;
                    if (now - lastAntiPauseLog > 5f) { lastAntiPauseLog = now; Plugin.Info($"[pause] Time.timeScale was 0: set to 1 by the watchdog (x{pauseFixes})"); }
                }
            }
            catch (Exception e) { if (!enemyErrLogged) { enemyErrLogged = true; Plugin.Info("[pause] watchdog error: " + e.Message); } }
            try
            {
                // any window that takes the controls away (level up, chest, shop, menu...) protects me while it is open
                bool canInput = player.playerInput.CanInput();
                if (canInput) { invWindowSince = -1f; invCapLogged = false; }
                else
                {
                    if (invWindowSince < 0f) invWindowSince = now;
                    if (now - invWindowSince < 30f) { if (Coop.InvincibleUntil < now + 1.5f) Coop.InvincibleUntil = now + 1.5f; }
                    else if (!invCapLogged) { invCapLogged = true; Plugin.Info("[pause] a window has been open for 30 s: no more invincibility until it closes (leaving a chest screen open is not a shield)"); Coop.Toast("Tempo de protecao acabou. Feche a janela!", 2, 4f); }
                }
            }
            catch { }
        }

        void DoObjectCheck()
        {
            Coop.ObjCheckAt = 0f;
            if (Coop.RejoinThisGen) { Coop.SkipChecks("re-joined a run in progress"); return; }
            // the terrain colliders are baked over several frames (slower on a slow PC): rays that land on the world floor instead of the
            // terrain gave a false "different ground". Wait until two samples 1.5 s apart are identical (max ~15 s).
            {
                var cur = SampleTerrain(out _);
                bool same = terrainPrev != null && System.Linq.Enumerable.SequenceEqual(cur, terrainPrev);
                if (!same && terrainStabTries < 10)
                {
                    terrainStabTries++; terrainPrev = cur;
                    if (terrainStabTries == 1 || terrainStabTries % 4 == 0) Plugin.Info($"[terraincheck] ground not stable yet (sample {terrainStabTries}): waiting for the colliders");
                    Coop.ObjCheckAt = Time.realtimeSinceStartup + 1.5f;
                    return;
                }
                if (terrainStabTries >= 10 && !same) Plugin.Info("[terraincheck] ground still changing after 10 samples: checking anyway");
                terrainPrev = null; terrainStabTries = 0;
            }
            DoTerrainCheck();
            try
            {
                var counts = new SortedDictionary<string, int>();
                foreach (var it in UnityEngine.Object.FindObjectsOfType<BaseInteractable>())
                {
                    if (it == null) continue;
                    // GetType().Name is always "BaseInteractable" for these wrappers: use the real object name instead
                    string name = it.gameObject != null ? it.gameObject.name : "?";
                    name = System.Text.RegularExpressions.Regex.Replace(name, @"\(Clone\)|[\s_\-]*\d+$|\s*\(\d+\)", "").Trim();
                    if (name.Length == 0) name = "?";
                    counts.TryGetValue(name, out int n); counts[name] = n + 1;
                }
                var parts = new List<string>();
                foreach (var kv in counts) parts.Add($"{kv.Key}={kv.Value}");
                Coop.ObjMine = string.Join(";", parts); Coop.ObjMineGen = Coop.ActualGen;
                Plugin.Info($"[objcheck] mine: {Coop.ObjMine}");
                string mine = Coop.ObjMine;
                if (net.Active && net.Connected) { int og = Coop.ObjMineGen; net.SendReliable(Coop.Pack(Msg.ObjCheck, w => { w.Write(og); w.Write(mine); })); }
                Coop.CompareObjects();
            }
            catch (Exception e) { Plugin.Info("[objcheck] failed: " + e.Message); }
        }

        float nextTimerSync;
        int timerFixes;

        // the host's stage/run timers are the truth: after a re-join (or any drift) the client's timers are corrected
        void SyncTimers(float now, float stage, float run, float diff, float swarm)
        {
            if (now < nextTimerSync) return;
            nextTimerSync = now + 2f;
            // NEVER touch the game's timers while the map is being generated or checked: the generation reads them
            // (a correction 0.1 s after the start of a generation changed the objects and the ground of that map)
            if (!Coop.HasActual || Coop.ObjCheckAt > 0f) return;
            try
            {
                float mine = Assets.Scripts.Utility.MyTime.stageTimer;
                if (Mathf.Abs(mine - stage) > 1.5f)
                {
                    Assets.Scripts.Utility.MyTime.stageTimer = stage;
                    Assets.Scripts.Utility.MyTime.runTimer = run;
                    Assets.Scripts.Utility.MyTime.difficultyTimer = diff;
                    Assets.Scripts.Utility.MyTime.finalSwarmTimer = swarm;
                    timerFixes++;
                    if (timerFixes <= 5) Plugin.Info($"[timer] stage timer corrected {mine:F1}s -> host {stage:F1}s (run {run:F0}s)");
                }
            }
            catch (Exception e) { if (timerFixes == 0) { timerFixes = 1; Plugin.Info("[timer] sync failed: " + e.Message); } }
        }

        void DoPortalFollow()
        {
            Coop.PortalFollowAt = 0f;
            try
            {
                if (MyPlayer.Instance == null) { Plugin.Info("[portal] follow: I am not in a run, nothing to do"); return; }
                Plugin.Info($"[portal] follow: calling MapController.{(Coop.PortalKind == 1 ? "LoadFinalStage" : "LoadNextStage")}() (stage index now {Assets.Scripts.Managers.MapController.GetStageIndex()})");
                Coop.PortalFromNet = true;
                if (Coop.PortalKind == 1) Assets.Scripts.Managers.MapController.LoadFinalStage(); else Assets.Scripts.Managers.MapController.LoadNextStage();
            }
            catch (Exception e) { Plugin.Info("[portal] follow failed: " + e); Coop.Toast("Nao consegui entrar no portal sozinho. Va ate ele.", 2, 6f); }
            finally { Coop.PortalFromNet = false; }
        }

        void DrawHpBar(Rect r, float frac, bool dead)
        {
            if (smallStyle == null)
            {
                smallStyle = new GUIStyle(GUI.skin.label);
                smallStyle.fontSize = 12; smallStyle.fontStyle = FontStyle.Bold; smallStyle.alignment = TextAnchor.MiddleCenter;
            }
            frac = Mathf.Clamp01(frac);
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), Texture2D.whiteTexture);
            GUI.color = new Color(0.25f, 0.05f, 0.05f, 1f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = dead ? new Color(0.4f, 0.4f, 0.4f) : Color.Lerp(new Color(0.95f, 0.2f, 0.2f), new Color(0.3f, 0.95f, 0.35f), frac);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * frac, r.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(r, dead ? "morto" : $"{remoteHp}/{remoteMaxHp}", smallStyle);
            GUI.color = old;
        }

        float hostGoneSince = -1f;
        bool shareTested;
        void DevShareTest(float now)
        {
            if (shareTested || !Plugin.DevShareTest.Value || !Coop.IsHost || !Coop.Synced || MyPlayer.Instance == null || now < 30f) return;
            shareTested = true;
            var inv = MyPlayer.Instance.inventory;
            Plugin.Info($"[share] DEV test: host gives itself +25 xp +100 gold (gold now {inv.goldInt}, xp {inv.playerXp.xp}, level {inv.playerXp.level})");
            inv.playerXp.AddXp(25); inv.ChangeGold(100);
        }

        void Disconnect(string src)
        {
            Share.Reset(); CoopStats.Reset(); Coop.PeerName = null; joinStartedAt = -1f; Coop.HostLeftRun = false; hostGoneSince = -1f;
            Plugin.Info($"{src} -> disconnect");
            EnemySync.Reset("disconnect");
            ProjectileSync.Reset(); ConstSync.Reset();
            if (chatOpen) CloseChat();
            Coop.Toast("Desconectado", 2, 3f); Coop.ResetSync();
            net.Close(); DestroyRemote(); Coop.HasRunSeed = false; Coop.RunId = 0; Coop.ConsumedRunId = 0; Coop.HaveSession = false; Coop.ClientReady = false; haveRemote = false; remoteInRun = false; remoteDead = false; Coop.PortalFollowAt = 0f; lastRecvTime = -1f; rtt = -1f; stalled = false;
            Coop.PeerLost = false; Coop.InvincibleUntil = 0f; Coop.HostInRun = false; Coop.HostMap = -1; Coop.HostStage = -1; Coop.HostTier = -1; Coop.VersionMismatch = false; RunGate.Reset();
        }

        // ------------------------------------------------------------------ chat (T)
        bool chatOpen;
        string chatText = "";
        string myName;
        GUIStyle chatStyle;
        bool gameInputOff;
        bool noRewiredLogged;

        string MyName()
        {
            if (myName != null) { Coop.LocalName = myName; return myName; }
            try { myName = Steamworks.SteamFriends.GetPersonaName(); }
            catch (Exception e) { Plugin.Info("[chat] steam name unavailable: " + e.Message); }
            if (string.IsNullOrWhiteSpace(myName)) myName = Coop.IsHost ? "Host" : "Amigo";
            if (myName.Length > 24) myName = myName.Substring(0, 24);
            Coop.LocalName = myName;
            Plugin.Info($"[chat] my name = {myName}");
            return myName;
        }

        // While typing, stop the game from reading WASD/Esc/etc (it uses Rewired). Always restore.
        void SetGameInput(bool enabled)
        {
            if (gameInputOff == !enabled) return;
            try
            {
                Rewired.ReInput.players.GetPlayer(0).controllers.maps.SetAllMapsEnabled(enabled);
                gameInputOff = !enabled;
                // (no log line: it only added noise)
            }
            catch (Exception e)
            {
                if (!noRewiredLogged) { noRewiredLogged = true; Plugin.Info("[chat] could not toggle game input (character may move while typing): " + e.Message); }
            }
        }

        void OpenChat()
        {
            chatOpen = true; chatText = ""; ipFocus = false;
            SetGameInput(false);
        }

        void CloseChat()
        {
            chatOpen = false; chatText = "";
            SetGameInput(true);
        }

        void SendChat()
        {
            string t = (chatText ?? "").Trim();
            if (t.Length == 0) return;
            if (t.Length > 120) t = t.Substring(0, 120);
            string me = MyName();
            if (net.Connected) net.SendReliable(Coop.Pack(Msg.Chat, w => { w.Write(me); w.Write(t); }));
            else Coop.Toast("Ninguem conectado para ler o chat.", 2, 3f);
            Coop.AddChat(me, t, true);
        }

        void HandleChat()
        {
            if (!chatOpen)
            {
                if (net.Active && KeyDown(KeyCode.T)) OpenChat();
                return;
            }
            if (!net.Active) { CloseChat(); return; }
            if (KeyDown(KeyCode.Escape)) { CloseChat(); return; }
            // (the 'T' that opened the chat is dropped: OpenChat ran last frame)
            foreach (char c in TypedChars())
            {
                if (c == '\b') { if (chatText.Length > 0) chatText = chatText.Substring(0, chatText.Length - 1); }
                else if (c == '\n' || c == '\r') { SendChat(); CloseChat(); return; }
                else if (!char.IsControl(c) && chatText.Length < 120) chatText += c;
            }
            if ((KeyHeld(KeyCode.LeftControl) || KeyHeld(KeyCode.RightControl)) && KeyDown(KeyCode.V))
            {
                try { chatText = ((chatText + (GUIUtility.systemCopyBuffer ?? "")).Replace("\n", " ").Replace("\r", " ")); if (chatText.Length > 120) chatText = chatText.Substring(0, 120); }
                catch { }
            }
        }

        void ChatLabel(Rect r, string text, Color c)
        {
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, c.a * 0.9f);
            GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, chatStyle);
            GUI.color = c;
            GUI.Label(r, text, chatStyle);
            GUI.color = old;
        }

        void DrawChat()
        {
            if (chatStyle == null)
            {
                chatStyle = new GUIStyle(GUI.skin.label);
                chatStyle.fontSize = 18; chatStyle.fontStyle = FontStyle.Bold; chatStyle.alignment = TextAnchor.MiddleLeft;
                chatStyle.wordWrap = true;
            }
            float H = Screen.height, now = Time.realtimeSinceStartup;
            float inputH = 34f, lineH = 26f;
            int inL = chatOpen ? Mathf.Max(1, Mathf.CeilToInt(((chatText ?? "").Length + 2) / 58f)) : 0;
            float baseY = H - 44f - (chatOpen ? 10f + inL * 24f + 6f : 0f);   // just above the status line / input box

            // messages: newest at the bottom, fade out after 12s (all visible while typing)
            int shown = 0;
            float chatY = baseY;
            for (int i = Coop.Chat.Count - 1; i >= 0 && shown < 8; i--)
            {
                var m = Coop.Chat[i];
                float age = now - m.At;
                float a = chatOpen ? 1f : Mathf.Clamp01((12f - age) / 2f);
                if (a <= 0f) continue;
                var c = m.C; c.a = a;
                int lines = Mathf.Max(1, Mathf.CeilToInt(m.Text.Length / 58f));      // ~58 characters per line at this size
                float h = lines * lineH;
                chatY -= h;
                float y = chatY;
                var old = GUI.color;
                GUI.color = new Color(0, 0, 0, 0.35f * a);
                GUI.DrawTexture(new Rect(8, y + 1, 720, h - 2), Texture2D.whiteTexture);
                GUI.color = old;
                ChatLabel(new Rect(14, y, 710, h), m.Text, c);
                shown++;
            }

            if (chatOpen)
            {
                int inLines = Mathf.Max(1, Mathf.CeilToInt((chatText.Length + 2) / 58f));
                float ih = 10f + inLines * 24f;
                float y = H - 44f - ih;
                var old = GUI.color;
                GUI.color = new Color(0.05f, 0.06f, 0.09f, 0.92f);
                GUI.DrawTexture(new Rect(8, y, 720, ih), Texture2D.whiteTexture);
                GUI.color = old;
                string caret = ((int)(now * 2f) % 2 == 0) ? "|" : "";
                ChatLabel(new Rect(14, y + 4, 710, ih - 8), "> " + chatText + caret, Color.white);
                ChatLabel(new Rect(14, y - 22f, 710, 20), "Enter = enviar   Esc = cancelar", new Color(1f, 1f, 1f, 0.55f));
            }
        }

        // ------------------------------------------------------------ connection panel (IMGUI)
        bool panelOpen = true;
        bool ipFocus;
        bool panelErrLogged;

        void PasteIp()
        {
            try { ipText = (GUIUtility.systemCopyBuffer ?? "").Trim(); }
            catch (Exception e) { Plugin.Info("[panel] paste failed: " + e.Message); Coop.Toast("Nao consegui colar. Digite o IP.", 2, 4f); }
        }

        void HandleTyping()
        {
            if (ipText == null) ipText = Plugin.JoinIp.Value;
            if (!ipFocus || chatOpen) return;
            foreach (char c in TypedChars())
            {
                if (c == '\b') { if (ipText.Length > 0) ipText = ipText.Substring(0, ipText.Length - 1); }
                else if (c == '\n' || c == '\r') ipFocus = false;
                else if (!char.IsControl(c) && ipText.Length < 40) ipText += c;
            }
            if ((KeyHeld(KeyCode.LeftControl) || KeyHeld(KeyCode.RightControl)) && KeyDown(KeyCode.V)) PasteIp();
            if (KeyDown(KeyCode.Escape)) ipFocus = false;
        }
        string ipText;
        string bestIp = "?";

        static string FindBestLocalIp()
        {
            try
            {
                var all = new List<string>();
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.")) continue;
                        all.Add(ip);
                        Plugin.Info($"[env] local ip {ip} ({ni.Name})");
                    }
                }
                foreach (var ip in all) if (ip.StartsWith("26.")) return ip;   // Radmin VPN range
                if (all.Count > 0) return all[0];
            }
            catch (Exception e) { Plugin.Info("[env] ip lookup failed: " + e.Message); }
            return "?";
        }

        // ---- panel styling: opaque, readable, independent of Unity's translucent default skin
        Texture2D bgTex;
        GUIStyle pBtn, pField, pLabel, pTitle;

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(t);
            return t;
        }

        static void AllStates(GUIStyle st, Texture2D n, Texture2D h, Texture2D a)
        {
            st.normal.background = n; st.focused.background = n; st.hover.background = h; st.active.background = a;
            st.normal.textColor = Color.white; st.focused.textColor = Color.white; st.hover.textColor = Color.white; st.active.textColor = Color.white;
        }

        void MakePanelStyles()
        {
            if (pBtn != null) return;
            bgTex = Solid(new Color(0.07f, 0.08f, 0.11f, 0.97f));
            var n = Solid(new Color(0.18f, 0.42f, 0.78f, 1f));
            var h = Solid(new Color(0.27f, 0.56f, 0.95f, 1f));
            var a = Solid(new Color(0.10f, 0.28f, 0.58f, 1f));
            pBtn = new GUIStyle(GUI.skin.button);
            pBtn.fontSize = 15; pBtn.fontStyle = FontStyle.Bold; pBtn.alignment = TextAnchor.MiddleCenter;
            AllStates(pBtn, n, h, a);
            var fn = Solid(new Color(0.16f, 0.17f, 0.22f, 1f));
            var fh = Solid(new Color(0.22f, 0.24f, 0.30f, 1f));
            pField = new GUIStyle(GUI.skin.button);
            pField.fontSize = 15; pField.fontStyle = FontStyle.Normal; pField.alignment = TextAnchor.MiddleLeft;
            AllStates(pField, fn, fh, fn);
            pLabel = new GUIStyle(GUI.skin.label);
            pLabel.fontSize = 15; pLabel.normal.textColor = Color.white; pLabel.alignment = TextAnchor.MiddleLeft;
            pTitle = new GUIStyle(pLabel);
            pTitle.fontStyle = FontStyle.Bold; pTitle.normal.textColor = new Color(0.45f, 0.9f, 1f);
        }

        void DrawPanel()
        {
            MakePanelStyles();
            float s = Mathf.Clamp(Screen.height / 1080f, 0.85f, 2.2f);
            float sw = Screen.width / s, sh = Screen.height / s;
            var oldM = GUI.matrix; var oldC = GUI.color;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
            GUI.color = Color.white;
            try
            {
                const float w = 340f;
                if (!panelOpen)
                {
                    if (GUI.Button(new Rect((sw - 150f) / 2f, sh - 40f, 150, 30), "COOP  [F7]", pBtn)) panelOpen = true;
                    return;
                }
                float h = net.Active ? 172f : 262f;
                float x = (sw - w) / 2f, y = sh - h - 12f;
                GUI.DrawTexture(new Rect(x, y, w, h), bgTex);
                GUI.Label(new Rect(x + 12, y + 6, w - 50, 24), "MEGABONK COOP  v" + Plugin.Version, pTitle);
                if (GUI.Button(new Rect(x + w - 32, y + 6, 24, 24), "x", pBtn)) panelOpen = false;
                float cy = y + 38f;

                GUI.Label(new Rect(x + 12, cy, w - 110, 28), "Seu IP: " + bestIp, pLabel);
                if (GUI.Button(new Rect(x + w - 92, cy, 80, 28), "Copiar", pBtn))
                {
                    if (bestIp == "?") Coop.Toast("Nao achei seu IP. Veja no Radmin.", 2, 4f);
                    else { try { GUIUtility.systemCopyBuffer = bestIp; Coop.Toast("IP copiado: " + bestIp, 1, 3f); } catch (Exception e) { Plugin.Info("[panel] copy failed: " + e.Message); } }
                }
                cy += 36f;

                if (!net.Active)
                {
                    if (GUI.Button(new Rect(x + 12, cy, w - 24, 36), "HOSPEDAR  (voce e o host)", pBtn)) { ipFocus = false; StartHost("[ui] Hospedar"); }
                    cy += 46f;
                    GUI.Label(new Rect(x + 12, cy, w - 24, 22), "IP do host (seu amigo):", pLabel);
                    cy += 24f;
                    string shown = (ipText ?? "") + (ipFocus && ((int)(Time.realtimeSinceStartup * 2f) % 2 == 0) ? "|" : "");
                    if (shown.Length == 0 && !ipFocus) shown = "clique e digite, ou use Colar";
                    if (GUI.Button(new Rect(x + 12, cy, w - 106, 30), " " + shown, pField)) ipFocus = true;
                    if (GUI.Button(new Rect(x + w - 88, cy, 76, 30), "Colar", pBtn)) { ipFocus = false; PasteIp(); }
                    cy += 38f;
                    if (GUI.Button(new Rect(x + 12, cy, w - 24, 36), "CONECTAR NO HOST", pBtn))
                    {
                        ipFocus = false;
                        Plugin.JoinIp.Value = (ipText ?? "").Trim();
                        StartJoin("[ui] Conectar");
                    }
                }
                else
                {
                    string st = !haveRemote ? (Coop.IsHost ? "Aguardando o amigo conectar..." : "Conectando ao host...")
                              : stalled ? "Sem sinal do amigo!"
                              : Coop.Synced ? "SINCRONIZADO (mesmo mapa)" : "Conectado (mapa nao confirmado)";
                    GUI.Label(new Rect(x + 12, cy, w - 24, 24), (Coop.IsHost ? "HOST: " : "CLIENTE: ") + st, pLabel);
                    cy += 26f;
                    GUI.Label(new Rect(x + 12, cy, w - 24, 24), "Ping: " + (rtt < 0 ? "?" : rtt.ToString("F0") + " ms"), pLabel);
                    cy += 32f;
                    if (GUI.Button(new Rect(x + 12, cy, w - 24, 36), "DESCONECTAR", pBtn)) Disconnect("[ui] Desconectar");
                }
            }
            catch (Exception e)
            {
                if (!panelErrLogged) { panelErrLogged = true; Plugin.Info("[panel] draw error (logged once): " + e); }
            }
            finally { GUI.matrix = oldM; GUI.color = oldC; }
        }

        void StartHost(string src = "[auto]")
        {
            if (Coop.IsHost) { Plugin.Info($"{src} ignored: already hosting (F10 to reset)"); return; }
            MyName();
            net.Host();
            Coop.HasRunSeed = false; Coop.RunId = 0; Coop.ConsumedRunId = 0; Coop.ClientReady = false;
            Coop.SessionSeed = UnityEngine.Random.Range(1, int.MaxValue); Coop.HaveSession = true;
            Plugin.Info($"[seed] session seed drawn: {Coop.SessionSeed}");
            Plugin.Info($"{src} -> hosting. The seed is read from the game when the host starts a run.");
            Coop.Toast($"Hospedando na porta {Plugin.PortCfg.Value}. Aguardando o amigo conectar...", 0, 6f);
        }

        void StartJoin(string src = "[auto]")
        {
            if (joinStartedAt < 0f) joinStartedAt = Time.realtimeSinceStartup;
            if (Coop.IsClient) { Plugin.Info($"{src} ignored: already joined (F10 to reset)"); return; }
            Coop.HasRunSeed = false; Coop.RunId = 0; Coop.ConsumedRunId = 0; Coop.HaveSession = false; Coop.ClientReady = false;
            Plugin.Info($"{src} -> joining {Plugin.JoinIp.Value}");
            MyName();
            if (!net.Join(Plugin.JoinIp.Value)) { Coop.Toast($"IP invalido: '{Plugin.JoinIp.Value}'", 3, 5f); return; }
            Coop.Toast($"Conectando em {Plugin.JoinIp.Value}...", 0, 4f);
        }

        void TrackScene()
        {
            try
            {
                var n = SceneManager.GetActiveScene().name;
                if (n != lastScene)
                {
                    Plugin.Info($"[scene] '{lastScene}' -> '{n}'");
                    lastScene = n;
                    Coop.WindowOpen = false;
                    EnemySync.Reset("scene " + n);
                    ProjectileSync.Reset(); ConstSync.Reset();
                    panelOpen = (n == "MainMenu");
                    // old minimap clone dies with the scene; rebuild lazily
                    mapIcon = null; mapIconFailed = false; mapIconNoSrcLogged = false;
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ ghost
        void CreateGhost()
        {
            ghost = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            ghost.name = "CoopGhost";
            UnityEngine.Object.DontDestroyOnLoad(ghost);
            var col = ghost.GetComponent<Collider>();
            if (col != null) Destroy(col);
            ghost.GetComponent<Renderer>().material.color = new Color(0.2f, 0.8f, 1f);
            ghost.transform.position = ghostTarget;
            var grb = ghost.AddComponent<Rigidbody>();
            grb.isKinematic = true; grb.useGravity = false;
            Plugin.Info("[ghost] created (capsule fallback, kinematic rigidbody so enemies can target it)");
        }

        void DestroyRemote()
        {
            if (ghostModel != null) Destroy(ghostModel);
            ghostModel = null; ghostAnim = null; animParam = null; builtCharId = -2; ghostHat = null;
            if (ghost != null) Destroy(ghost);
            ghost = null;
            if (mapIcon != null) Destroy(mapIcon);
            mapIcon = null;
        }

        static float MyModelYaw(MyPlayer p)
        {
            if (p == null) return 0f;
            try { var ro = p.playerRenderer?.rendererObject; if (ro != null) return ro.transform.eulerAngles.y; } catch { }
            return p.transform.eulerAngles.y;
        }

        // F6: pretend my own character is a friend standing 3 m to my right
        void UpdateSelfTest()
        {
            var me = MyPlayer.Instance;
            if (me == null) { remoteInRun = false; haveRemote = true; ApplyRemoteVisual(null); return; }
            haveRemote = true; remoteInRun = true;
            ghostTarget = me.transform.position + me.transform.right * 3f;
            ghostYaw = MyModelYaw(me);
            ghostCharId = (int)me.character;
            try { var ph = me.inventory?.playerHealth; if (ph != null) { remoteHp = ph.hp; remoteMaxHp = ph.maxHp; } } catch { }
            remoteDead = false;
            ApplyRemoteVisual(me);
            UpdateMapIcon(me);
        }

        // Build the friend's character model from the game's own prefab (falls back to the capsule).
        Vector3 feetOffsetGood; bool hasGoodFeet;
        void TryBuildModel(MyPlayer me)
        {
            if (me == null) return;          // wait until I am in a run: the offset is measured on my own player
            builtCharId = ghostCharId;       // one attempt per character id
            if (ghostModel != null) { Destroy(ghostModel); ghostModel = null; ghostAnim = null; animParam = null; ghostHat = null; }
            var caps = ghost != null ? ghost.GetComponent<Renderer>() : null;
            if (caps != null) caps.enabled = true;
            if (ghostCharId < 0) return;
            try
            {
                // measure feet offset on the local player (when available)
                {
                    var ro = me.playerRenderer != null ? me.playerRenderer.rendererObject : null;
                    Vector3 m = feetOffset; bool have = false;
                    if (ro != null) { m = ro.transform.position - me.transform.position; have = true; }          // where the game puts the model
                    else if (me.feet != null) { m = me.feet.position - me.transform.position; have = true; }
                    // the measurement is only valid while MY character stands normally: when it is dead/falling/sliding the renderer is rotated and the
                    // vector comes out sideways (seen: (1.92, 0, 0) instead of (0, -1.92, 0)), which put the friend's model 2 m to the side
                    bool sane = have && Mathf.Abs(m.x) < 0.45f && Mathf.Abs(m.z) < 0.45f && m.y < -0.25f && m.y > -4f;
                    if (sane) { feetOffset = m; feetOffsetGood = m; hasGoodFeet = true; }
                    else if (hasGoodFeet) feetOffset = feetOffsetGood;
                    else feetOffset = new Vector3(0f, -1.92f, 0f);
                    Plugin.Info($"[model] measured feetOffset={m} (rendererObject={(ro != null)}){(sane ? "" : " -> IGNORED, using " + feetOffset.ToString())}");
                }

                var cd = DataManager.Instance.GetCharacterData((ECharacter)ghostCharId);
                if (cd == null || cd.prefab == null) { Plugin.Info($"[model] no CharacterData/prefab for char {ghostCharId}; keeping capsule"); return; }

                // The offset above was measured on MY character. Characters have different collider heights, so the
                // friend's feet are at (his height / my height) * my offset. (The F6 dummy can never show this error.)
                try
                {
                    var myCd = DataManager.Instance.GetCharacterData(me.character);
                    float myH = myCd != null ? myCd.colliderHeight : 0f, frH = cd.colliderHeight;
                    if (myH > 0.05f && frH > 0.05f)
                    {
                        var before = feetOffset;
                        feetOffset = new Vector3(feetOffset.x, feetOffset.y * (frH / myH), feetOffset.z);
                        Plugin.Info($"[model] collider heights: mine({me.character})={myH:F2} friend({(ECharacter)ghostCharId})={frH:F2} => feetOffset {before.y:F2} -> {feetOffset.y:F2}");
                    }
                    else Plugin.Info($"[model] collider heights unavailable (mine={myH}, friend={frH}); using my offset");
                }
                catch (Exception ex) { Plugin.Info("[model] collider height scaling failed: " + ex.Message); }

                // instantiate under an INACTIVE holder so the game's scripts never run on the clone
                var holder = new GameObject("CoopModelHolder");
                holder.SetActive(false);
                var go = UnityEngine.Object.Instantiate(cd.prefab, holder.transform);
                go.name = "CoopGhostModel";
                int nCol = 0, nRb = 0, nMb = 0;
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) { Destroy(c); nCol++; }
                foreach (var r in go.GetComponentsInChildren<Rigidbody>(true)) { Destroy(r); nRb++; }
                foreach (var m in go.GetComponentsInChildren<MonoBehaviour>(true)) { Destroy(m); nMb++; }

                go.transform.SetParent(ghost.transform, false);
                go.transform.localPosition = feetOffset;
                go.transform.localRotation = Quaternion.identity;
                Destroy(holder);
                go.SetActive(true);
                ghostModel = go;
                if (caps != null) caps.enabled = false;

                ghostAnim = go.GetComponentInChildren<Animator>(true);
                string animInfo = ghostAnim == null ? "no animator" : "animator present";
                if (ghostAnim != null)
                {
                    try { animInfo = DiscoverAnimator(ghostAnim); }
                    catch (Exception ex)
                    {
                        // parameter list unreadable in this build: fall back to the names found in the game's strings
                        animParam = "moving"; animParamIsBool = true;
                        animInfo = $"animator params unreadable ({ex.GetType().Name}); guessing bool 'moving'";
                    }
                }
                var rend = go.GetComponentInChildren<Renderer>(true);
                string matInfo = rend != null && rend.sharedMaterial != null ? $"{rend.sharedMaterial.name} / shader {rend.sharedMaterial.shader.name}" : "none";
                try
                {
                    var pr = me.playerRenderer;
                    string hp = "hatTransform=null";
                    if (pr != null && pr.hatTransform != null)
                    {
                        var chain = new List<string>(); var t = pr.hatTransform;
                        for (int g = 0; t != null && g < 12; g++) { chain.Add(t.name); t = t.parent; }
                        chain.Reverse();
                        hp = $"hatTransform='{pr.hatTransform.name}' path={string.Join("/", chain)} inRendererObject={(pr.rendererObject != null && pr.hatTransform.IsChildOf(pr.rendererObject.transform))}";
                    }
                    var heads = new List<string>();
                    void Walk(Transform tr, string path, int depth)
                    {
                        if (tr == null || depth > 14 || heads.Count >= 14) return;
                        string n = tr.name.ToLowerInvariant();
                        if (n.Contains("hat") || n.Contains("head") || n.Contains("helmet") || n.Contains("crown")) heads.Add(path + "/" + tr.name);
                        for (int i = 0; i < tr.childCount; i++) Walk(tr.GetChild(i), path + "/" + tr.name, depth + 1);
                    }
                    Walk(go.transform, "", 0);
                    Plugin.Info($"[hat] diag: local {hp}; hips='{(pr != null && pr.hips != null ? pr.hips.name : "?")}' torso='{(pr != null && pr.torso != null ? pr.torso.name : "?")}'; head-like nodes in the friend's model: {string.Join(" | ", heads)}");
                }
                catch (Exception ex) { Plugin.Info("[hat] diag failed: " + ex.Message); }
                Plugin.Info($"[model] built char={ghostCharId} prefab='{cd.prefab.name}' scale={go.transform.localScale} feetOffset={feetOffset} " +
                            $"removed: colliders={nCol} rigidbodies={nRb} scripts={nMb}; {animInfo}; material={matInfo}");
            }
            catch (Exception e)
            {
                Plugin.Info("[model] FAILED, keeping capsule: " + e);
                if (ghostModel != null) { Destroy(ghostModel); ghostModel = null; }
                if (caps != null) caps.enabled = true;
            }
        }

        // kept in its own method: if a type used here fails to load, only THIS call throws (and is caught above)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        string DiscoverAnimator(Animator a)
        {
            var names = new List<string>();
            int count = a.parameterCount;
            for (int i = 0; i < count; i++)
            {
                var p = a.GetParameter(i);
                names.Add($"{p.name}:{p.type}");
                string n = p.name.ToLowerInvariant();
                if (animParam == null && p.type == AnimatorControllerParameterType.Bool && (n.Contains("mov") || n.Contains("walk") || n.Contains("run")))
                { animParam = p.name; animParamIsBool = true; }
            }
            for (int i = 0; i < count && animParam == null; i++)
            {
                var p = a.GetParameter(i);
                if (p.type == AnimatorControllerParameterType.Float && p.name.ToLowerInvariant().Contains("speed"))
                { animParam = p.name; animParamIsBool = false; }
            }
            return $"animator params=[{string.Join(", ", names)}] driving='{animParam ?? "none"}'";
        }

        static void ReadMyAppearance(MyPlayer me, out int hat, out int skin)
        {
            hat = -1; skin = -1;
            if (Plugin.DevForceHat.Value > 0) hat = Plugin.DevForceHat.Value;
            var pr = me != null ? me.playerRenderer : null;
            if (pr == null) return;
            var hd = pr.currentHat;
            if (hd != null && Plugin.DevForceHat.Value <= 0) hat = (int)hd.eHat;
            var sd = pr.skinData;
            if (sd != null)
            {
                var list = DataManager.Instance.GetSkins(me.character);
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null && list[i].Pointer == sd.Pointer) { skin = i; break; }
            }
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        // the friend's skin (materials) and hat (mesh + material at the head attach node) on his model
        void ApplyAppearance(MyPlayer me)
        {
            builtHat = remoteHat; builtSkin = remoteSkin;
            if (ghostModel == null) return;
            try
            {
                if (remoteSkin >= 0)
                {
                    var list = DataManager.Instance.GetSkins((ECharacter)ghostCharId);
                    if (list != null && remoteSkin < list.Count && list[remoteSkin] != null && list[remoteSkin].materials != null)
                    {
                        var smr = ghostModel.GetComponentInChildren<SkinnedMeshRenderer>(true);
                        if (smr != null) { smr.sharedMaterials = list[remoteSkin].materials; Plugin.Info($"[hat] skin #{remoteSkin} applied to the friend's model"); }
                    }
                }
                if (ghostHat != null) { Destroy(ghostHat); ghostHat = null; }
                if (remoteHat > 0)
                {
                    var hd = DataManager.Instance.GetHat((EHat)remoteHat);
                    if (hd == null || hd.mesh == null) { Plugin.Info($"[hat] no HatData/mesh for hat {remoteHat}"); return; }
                    if (me != null && me.playerRenderer != null && me.playerRenderer.hatTransform != null) localHatNode = me.playerRenderer.hatTransform.name;
                    // the game's "PlayerHat" node is a child of the Head bone and is created at runtime; the character prefab only has the bone
                    Transform attach = FindDeep(ghostModel.transform, "Head");
                    string how = attach != null ? "bone 'Head'" : null;
                    if (attach == null) { attach = FindDeep(ghostModel.transform, localHatNode); if (attach != null) how = $"node '{localHatNode}'"; }
                    if (how == null) how = "model root (no Head bone found)";
                    if (attach == null) attach = ghostModel.transform;
                    var go = new GameObject("CoopHat");
                    go.transform.SetParent(attach, false);
                    var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = hd.mesh;
                    var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = hd.material;
                    string ori = "no orientation for this character";
                    if (hd.orientations != null)
                        foreach (var o in hd.orientations)
                            if (o != null && o.character == (ECharacter)ghostCharId)
                            { go.transform.localPosition = o.pos; go.transform.localEulerAngles = o.rot; go.transform.localScale = o.scale; ori = $"pos={o.pos} rot={o.rot} scale={o.scale}"; break; }
                    ghostHat = go;
                    Plugin.Info($"[hat] hat {(EHat)remoteHat} attached to {how}; {ori}");
                }
            }
            catch (Exception e) { Plugin.Info("[hat] applying appearance failed: " + e.Message); }
        }

        void ApplyRemoteVisual(MyPlayer me)
        {
            if (!haveRemote) return;
            if (!remoteInRun || remoteDead)
            {
                if (ghost != null && ghost.activeSelf) { ghost.SetActive(false); Plugin.Info("[ghost] friend not in a run: hidden"); }
                if (mapIcon != null && mapIcon.activeSelf) mapIcon.SetActive(false);
                return;
            }
            if (ghost == null) CreateGhost();
            if (!ghost.activeSelf) { ghost.SetActive(true); ghost.transform.position = ghostTarget; Plugin.Info("[ghost] friend entered a run: shown"); }
            if (mapIcon != null && !mapIcon.activeSelf) mapIcon.SetActive(true);
            if (builtCharId != ghostCharId && me != null) { TryBuildModel(me); builtHat = -2; builtSkin = -2; }
            if (ghostModel != null && (builtHat != remoteHat || builtSkin != remoteSkin)) ApplyAppearance(me);

            ConstSync.Apply(ghost.transform, remoteInRun && !remoteDead);
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 old = ghost.transform.position;
            Vector3 nw = Vector3.Lerp(old, ghostTarget, 15f * dt);
            ghost.transform.position = nw;

            if (ghostModel != null)
            {
                ghostModel.transform.rotation = Quaternion.Slerp(ghostModel.transform.rotation, Quaternion.Euler(0f, ghostYaw, 0f), 12f * dt);
                // crouch: the game squashes the player the same way (crouchScale / playerScale)
                float want = 1f;
                if ((remoteFlags & 1) != 0)
                {
                    want = 0.6f;
                    try { var pm = me != null ? me.playerMovement : null; if (pm != null && pm.playerScale.y > 0.01f) want = Mathf.Clamp(pm.crouchScale.y / pm.playerScale.y, 0.3f, 1f); } catch { }
                }
                crouchK = Mathf.Lerp(crouchK, want, Mathf.Clamp01(14f * dt));
                var ls = ghostModel.transform.localScale;
                ghostModel.transform.localScale = new Vector3(ls.x, crouchBase * crouchK, ls.z);
                if (ghostAnim != null && animParam != null)
                {
                    float speed = (nw - old).magnitude / dt;
                    bool mv = speed > 0.5f;
                    try
                    {
                        if (animParamIsBool) { if (mv != lastAnimMoving) { ghostAnim.SetBool(animParam, mv); lastAnimMoving = mv; } }
                        else ghostAnim.SetFloat(animParam, speed);
                    }
                    catch { }
                }
            }
        }

        // ---------------------------------------------------------------- minimap
        void UpdateMapIcon(MyPlayer player)
        {
            if (!haveRemote || player == null || mapIconFailed) return;
            if (mapIcon == null)
            {
                if (Time.realtimeSinceStartup < nextMapRetry) return;
                nextMapRetry = Time.realtimeSinceStartup + 2f;
                try
                {
                    MinimapPlayerIcon src = null;
                    int total = 0, active = 0;
                    foreach (var x in Resources.FindObjectsOfTypeAll<MinimapPlayerIcon>())
                    {
                        if (x == null || !x.gameObject.scene.IsValid()) continue;   // skip prefabs/assets
                        total++;
                        if (x.gameObject.activeInHierarchy) active++;
                        if (src == null || (x.gameObject.activeInHierarchy && !src.gameObject.activeInHierarchy)) src = x;
                    }
                    if (!mapIconNoSrcLogged || src != null)
                        Plugin.Info($"[minimap] MinimapPlayerIcon instances in scene '{lastScene}': total={total} active={active}");
                    if (src == null)
                    {
                        if (!mapIconNoSrcLogged) { mapIconNoSrcLogged = true; Plugin.Info($"[minimap] no MinimapPlayerIcon in scene '{lastScene}' yet (will keep retrying every 2s)"); }
                        return; // not in a run yet, retry later
                    }
                    mapIcon = UnityEngine.Object.Instantiate(src.gameObject);
                    mapIcon.name = "CoopMinimapIcon";
                    mapIcon.SetActive(true);
                    mapIconOffset = src.transform.position - player.transform.position;
                    var comp = mapIcon.GetComponent<MinimapPlayerIcon>();
                    if (comp != null) Destroy(comp); // stop it following the local player
                    int tinted = 0;
                    foreach (var r in mapIcon.GetComponentsInChildren<Renderer>(true))
                    {
                        try { r.material.color = new Color(0.2f, 0.8f, 1f); tinted++; } catch { }
                    }
                    Plugin.Info($"[minimap] clone created. layer={mapIcon.layer} offset={mapIconOffset} renderers tinted={tinted} " +
                                $"srcParent={(src.transform.parent != null ? src.transform.parent.name : "none")} srcPos={src.transform.position}");
                }
                catch (Exception e)
                {
                    mapIconFailed = true;
                    Plugin.Info($"[minimap] FAILED, using arrow only: {e}");
                    return;
                }
            }
            if (mapIcon != null) mapIcon.transform.position = ghostTarget + mapIconOffset;
        }

        // -------------------------------------------------------------------- HUD
        Camera GetCam()
        {
            var c = Camera.main;
            if (c != null) return c;
            try { return Camera.current; } catch { return null; }
        }

        void MakeAssets()
        {
            if (arrowTex == null)
            {
                const int n = 64;
                arrowTex = new Texture2D(n, n, TextureFormat.RGBA32, false);
                var clear = new Color(0, 0, 0, 0);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        // triangle pointing up: apex at top, base at bottom
                        float half = (n / 2f - 4f) * (n - 2f - y) / (n - 4f);
                        bool inside = y >= 2 && y <= n - 2 && Mathf.Abs(x - n / 2f) <= half;
                        arrowTex.SetPixel(x, y, inside ? Color.white : clear);
                    }
                arrowTex.Apply();
                arrowTex.filterMode = FilterMode.Bilinear;
                UnityEngine.Object.DontDestroyOnLoad(arrowTex);
            }
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label);
                labelStyle.fontSize = 18;
                labelStyle.fontStyle = FontStyle.Bold;
                labelStyle.alignment = TextAnchor.MiddleCenter;
            }
        }

        void Label(Rect r, string text, Color c)
        {
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.9f);
            GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, labelStyle);
            GUI.color = c;
            GUI.Label(r, text, labelStyle);
            GUI.color = old;
        }

        void DrawArrow(Vector2 pos, float angleDeg, float size, Color c)
        {
            var oldM = GUI.matrix; var oldC = GUI.color;
            GUIUtility.RotateAroundPivot(angleDeg, pos);
            GUI.color = new Color(0, 0, 0, 0.9f);
            GUI.DrawTexture(new Rect(pos.x - size / 2 - 3, pos.y - size / 2 - 3, size + 6, size + 6), arrowTex);
            GUI.color = c;
            GUI.DrawTexture(new Rect(pos.x - size / 2, pos.y - size / 2, size, size), arrowTex);
            GUI.matrix = oldM; GUI.color = oldC;
        }

        GUIStyle toastStyle;

        void DrawToasts()
        {
            if (Coop.Toasts.Count == 0) return;
            if (toastStyle == null)
            {
                toastStyle = new GUIStyle(GUI.skin.label);
                toastStyle.fontSize = 19; toastStyle.fontStyle = FontStyle.Bold;
                toastStyle.alignment = TextAnchor.MiddleCenter; toastStyle.wordWrap = true;
            }
            float W = Screen.width, now = Time.realtimeSinceStartup;
            Coop.Toasts.RemoveAll((Predicate<Coop.ToastItem>)(t => now > t.Until));
            float boxW = Mathf.Min(980f, W - 40f);
            float x = (W - boxW) / 2f;
            float y = 70f;
            foreach (var t in Coop.Toasts)
            {
                float a = Mathf.Clamp01((t.Until - now) / 0.8f);
                // rough line count (about 11 px per character at this size), box grows with the text
                int lines = Mathf.Max(1, Mathf.CeilToInt(t.Text.Length * 11f / (boxW - 40f)));
                float h = lines * 27f + 16f;
                var old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.72f * a);
                GUI.DrawTexture(new Rect(x, y, boxW, h), Texture2D.whiteTexture);
                var c = t.C; c.a = a;
                var r = new Rect(x + 16f, y + 8f, boxW - 32f, h - 16f);
                GUI.color = new Color(0f, 0f, 0f, a * 0.9f);
                GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), t.Text, toastStyle);
                GUI.color = c;
                GUI.Label(r, t.Text, toastStyle);
                GUI.color = old;
                y += h + 8f;
            }
        }

        void OnGUI()
        {
            MakeAssets();
            if (!NativeMenuUp) { DrawToasts(); DrawPanel(); }
            if (!NativeHudUp) DrawChat();
            if (NativeHudUp) return;
            if (!net.Active && !selfTest) return;
            float W = Screen.width, H = Screen.height;
            var blue = new Color(0.25f, 0.85f, 1f);

            // small status line, bottom-left
            string status = selfTest ? "COOP: TESTE (F6) - amigo simulado ao seu lado" : (haveRemote && remoteDead) ? "COOP: seu amigo morreu" : !haveRemote
                ? (Coop.IsHost ? "COOP: aguardando amigo conectar..." : "COOP: conectando ao host...")
                : stalled ? "COOP: sem sinal do amigo!" : Coop.Synced ? $"COOP: SINCRONIZADO  ping {(rtt < 0 ? "?" : rtt.ToString("F0"))}ms" : $"COOP: conectado (mapa nao confirmado)  ping {(rtt < 0 ? "?" : rtt.ToString("F0"))}ms";
            Label(new Rect(10, H - 34, 380, 26), status, selfTest ? new Color(1f, 0.85f, 0.3f) : haveRemote && !stalled && Coop.Synced ? new Color(0.4f, 1f, 0.5f) : haveRemote && !stalled ? blue : new Color(1f, 0.6f, 0.2f));
            if (!haveRemote || !remoteInRun || remoteDead) return;

            var cam = GetCam();
            var me = MyPlayer.Instance;
            if (cam == null || me == null) return;

            Vector3 target = ghostTarget + Vector3.up * 1.0f;
            Vector3 sp = cam.WorldToScreenPoint(target);
            float dist = Vector3.Distance(me.transform.position, ghostTarget);
            string txt = $"AMIGO  {dist:F0}m";

            const float m = 50f;
            bool inFront = sp.z > 0f;
            bool onScreen = inFront && sp.x > m && sp.x < W - m && sp.y > m && sp.y < H - m;
            if (onScreen)
            {
                float gx = sp.x, gy = H - sp.y;
                DrawHpBar(new Rect(gx - 60, gy - 84, 120, 14), remoteMaxHp > 0 ? remoteHp / (float)remoteMaxHp : 1f, false);
                Label(new Rect(gx - 90, gy - 62, 180, 26), txt, blue);
                DrawArrow(new Vector2(gx, gy - 22), 180f, 26f, blue); // points down at friend
                return;
            }

            // off-screen: arrow clamped to the screen edge, pointing toward the friend
            float sx = sp.x, sy = sp.y;
            if (!inFront) { sx = W - sx; sy = H - sy; }
            Vector2 c = new Vector2(W / 2f, H / 2f);
            Vector2 dir = new Vector2(sx - c.x, (H - sy) - c.y);
            if (dir.sqrMagnitude < 1f) dir = new Vector2(0, -1);
            float tx = Mathf.Abs(dir.x) > 0.01f ? (W / 2f - m) / Mathf.Abs(dir.x) : float.MaxValue;
            float ty = Mathf.Abs(dir.y) > 0.01f ? (H / 2f - m) / Mathf.Abs(dir.y) : float.MaxValue;
            float t = Mathf.Min(tx, ty);
            Vector2 p = c + dir * t;
            float ang = Mathf.Atan2(dir.x, -dir.y) * Mathf.Rad2Deg; // 0 = up, clockwise
            DrawArrow(p, ang, 42f, blue);
            // label on the inner side of the arrow
            Vector2 inward = (c - p).normalized * 40f;
            Label(new Rect(p.x + inward.x - 90, p.y + inward.y - 13, 180, 26), txt, blue);
            DrawHpBar(new Rect(p.x + inward.x - 60, p.y + inward.y + 16, 120, 14), remoteMaxHp > 0 ? remoteHp / (float)remoteMaxHp : 1f, false);
        }
    }
}
