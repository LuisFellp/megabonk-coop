using System;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Utility;
using Assets.Scripts.Managers;
using Assets.Scripts.Steam;
using HarmonyLib;
using UnityEngine;

namespace MegabonkCoop
{
    // ---------------------------------------------------------------- no pause
    // A paused player would freeze HIS game while the other keeps playing (he then takes all the damage at once when
    // he leaves the window) and a paused host freezes the client's enemies. Like the other coop mods: never pause.
    [HarmonyPatch(typeof(MyTime), nameof(MyTime.Pause))]
    static class P_NoPause
    {
        static int blocked;
        [HarmonyPrefix]
        static bool Pre()
        {
            if (!Plugin.NoPause.Value || !Coop.Net.Active || !Coop.Net.Connected || Coop.PeerLost) return true;
            blocked++;
            if (blocked <= 3 || blocked % 50 == 0) Plugin.Info($"[pause] MyTime.Pause blocked (coop: the game does not pause) x{blocked}");
            return false;
        }
    }

    // ---------------------------------------------------------------- invincible while a window is open
    static class Inv
    {
        public static void Open(string what)
        {
            Coop.WindowOpen = true;
            if (!Plugin.NoPause.Value || !Coop.Net.Active || !Coop.Net.Connected || Coop.PeerLost) return;
            Coop.InvincibleUntil = Math.Max(Coop.InvincibleUntil, Time.realtimeSinceStartup + 30f);      // safety cap, shortened on close
            Plugin.Info($"[pause] {what} opened: invincible while choosing (max 30 s)");
            Coop.Toast("Voce esta INVENCIVEL enquanto escolhe (o jogo nao pausa no coop).", 0, 4f);
        }

        public static void Close(string what)
        {
            Coop.WindowOpen = false;
            if (!Plugin.NoPause.Value || !Coop.Net.Active) return;
            Coop.InvincibleUntil = Time.realtimeSinceStartup + 1.5f;     // short grace after closing
            Plugin.Info($"[pause] {what} closed: 1.5 s grace");
        }
    }

    [HarmonyPatch(typeof(LevelupScreen), nameof(LevelupScreen.ShowLevelupScreen))]
    static class P_LevelShow { [HarmonyPostfix] static void Post() => Inv.Open("LevelupScreen.Show"); }

    [HarmonyPatch(typeof(LevelupScreen), nameof(LevelupScreen.Open))]
    static class P_LevelOpen { [HarmonyPostfix] static void Post() => Inv.Open("LevelupScreen.Open"); }

    [HarmonyPatch(typeof(LevelupScreen), nameof(LevelupScreen.OnClose))]
    static class P_LevelClose { [HarmonyPostfix] static void Post() => Inv.Close("LevelupScreen"); }

    [HarmonyPatch(typeof(LevelupScreen), nameof(LevelupScreen.CloseLevelupScreen))]
    static class P_LevelClose2 { [HarmonyPostfix] static void Post() => Inv.Close("LevelupScreen.Close"); }

    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Damage))]
    static class P_InvDamage { [HarmonyPrefix] static bool Pre() => !Coop.IsInvincible; }

    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.DamagePlayer))]
    static class P_InvDamagePlayer { [HarmonyPrefix] static bool Pre() => !Coop.IsInvincible; }

    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.DamagePlayerExternal))]
    static class P_InvDamageExt { [HarmonyPrefix] static bool Pre() => !Coop.IsInvincible; }

    // ---------------------------------------------------------------- spawn together
    // The spawn point is not controlled by the shared RNG (it differed between the two PCs even with identical ground).
    // The host reports where it spawned; the client spawns next to it.
    [HarmonyPatch(typeof(MyPlayer), nameof(MyPlayer.Spawn))]
    static class P_Spawn
    {
        [HarmonyPrefix]
        static void Pre(ref Vector3 position)
        {
            if (!Coop.Net.Active || !Coop.Net.Connected) return;
            if (Coop.IsHost)
            {
                Coop.HostSpawn = position; Coop.HostSpawnValid = true; Coop.HostSpawnRunId = Coop.RunId;
                Plugin.Info($"[spawn] host spawned at {position} (runId {Coop.RunId}); announcing");
            }
            else if (Coop.IsClient && Plugin.SpawnTogether.Value)
            {
                if (Coop.HostSpawnValid && Coop.HostSpawnRunId == Coop.RunId)
                {
                    var was = position;
                    position = Coop.HostSpawn + new Vector3(Plugin.DevSpawnOffset.Value, 0f, 0f);
                    Plugin.Info($"[spawn] client spawn moved {was} -> {position} (next to the host)");
                    Coop.Toast("Voce nasceu ao lado do host.", 1, 3f);
                }
                else Plugin.Info($"[spawn] client spawns at {position}: host spawn not known for runId {Coop.RunId} (valid={Coop.HostSpawnValid}, hostRun={Coop.HostSpawnRunId})");
            }
        }
    }

    // ---------------------------------------------------------------- crypt (RsgController) uses its own generators
    // (DeliriumPulse also forces ConsistentRandom + job RNGs). Same idea as the main map: seed everything at the start.
    [HarmonyPatch(typeof(RsgController._GenerateMap_d__41), nameof(RsgController._GenerateMap_d__41.MoveNext))]
    static class P_CryptSeed
    {
        [HarmonyPrefix]
        static void Pre(RsgController._GenerateMap_d__41 __instance)
        {
            if (__instance.__1__state != 0) return;
            if (!Plugin.ShareSeed.Value || !Coop.Net.Active || !Coop.Net.Connected) return;
            if (!(Coop.IsHost || (Coop.IsClient && Coop.HaveSession))) return;
            int s = Coop.Mix(Coop.SessionSeed ^ 0x43525950, Coop.LastGenIdx);      // 'CRYP'
            Plugin.Info($"[seed] crypt generation: before RsgController.seed={RsgController.seed} customSeed={RsgController.customSeed}");
            RsgController.seed = s;
            UnityEngine.Random.InitState(s);
            Utility.MyRandom.random = new Il2CppSystem.Random(s);
            Plugin.Info($"[seed] crypt generation: shared RNG seeded => {s}");
        }
    }

    // ---------------------------------------------------------------- no leaderboard upload in coop
    // (the game also threw an IndexOutOfRange in Leaderboards.GetTomes when the client died)
    [HarmonyPatch(typeof(Leaderboards), nameof(Leaderboards.UploadScore))]
    static class P_NoLeaderboard
    {
        [HarmonyPrefix]
        static bool Pre(int score)
        {
            if (!Plugin.BlockLeaderboard.Value || !Coop.Net.Active || !Coop.Net.Connected) return true;
            Plugin.Info($"[steam] leaderboard upload of score {score} blocked (coop run)");
            return false;
        }
    }

    // ---------------------------------------------------------------- stage change (portal)
    // The InteractablePortal object may not exist (or not be usable) on the other PC, so we follow with the game's own
    // stage loaders: whoever loads the next stage tells the friend, who calls the same loader.
    static class StageSync
    {
        public static void Announce(byte kind)
        {
            if (Coop.PortalFromNet || !Coop.Net.Active || !Coop.Net.Connected) return;
            if (Assets.Scripts.Actors.Player.MyPlayer.Instance == null)
            {
                Plugin.Info($"[portal] stage loader called from the menu (start of a run): not a portal, nothing to announce");
                return;
            }
            string me = Coop.LocalName ?? "Amigo";
            Plugin.Info($"[portal] I am loading the {(kind == 1 ? "FINAL" : "next")} stage: telling the friend");
            Coop.Net.SendReliable(Coop.Pack(Msg.Portal, w => { w.Write(me); w.Write(kind); }));
            Coop.Toast("Voce entrou no portal. O amigo vai junto.", 0, 4f);
        }
    }

    [HarmonyPatch(typeof(MapController), nameof(MapController.LoadNextStage))]
    static class P_LoadNext { [HarmonyPrefix] static void Pre() => StageSync.Announce(0); }

    [HarmonyPatch(typeof(MapController), nameof(MapController.LoadFinalStage))]
    static class P_LoadFinal { [HarmonyPrefix] static void Pre() => StageSync.Announce(1); }
}
