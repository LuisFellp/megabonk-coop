using System;
using Actors.Enemies;
using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Managers;
using HarmonyLib;

namespace MegabonkCoop
{
    static class SyncOn
    {
        public static bool Client => Coop.IsClient && Plugin.SyncEnemies.Value && !Coop.PeerLost && !Coop.HostLeftRun;
        public static bool Host => Coop.IsHost && Plugin.SyncEnemies.Value && !Coop.PeerLost;
    }

    // ---- the client never spawns enemies on its own (copies are spawned with EnemySync.NetSpawning = true)
    [HarmonyPatch(typeof(EnemyManager), nameof(EnemyManager.CanSpawnEnemy))]
    static class P_CanSpawn
    {
        [HarmonyPostfix] static void Post(ref bool __result) { if (SyncOn.Client && !EnemySync.NetSpawning && !EnemySync.AllowLocal) __result = false; }
    }

    [HarmonyPatch(typeof(EnemyManager), nameof(EnemyManager.SpawnEnemy),
        new[] { typeof(EnemyData), typeof(int), typeof(bool), typeof(EEnemyFlag), typeof(bool) })]
    static class P_SpawnA
    {
        [HarmonyPrefix] static bool Pre(ref Enemy __result)
        {
            if (SyncOn.Client && !EnemySync.NetSpawning && !EnemySync.AllowLocal) { EnemySync.NoteBlocked("spawn"); __result = null; return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(EnemyManager), nameof(EnemyManager.SpawnEnemy),
        new[] { typeof(EnemyData), typeof(UnityEngine.Vector3), typeof(int), typeof(bool), typeof(EEnemyFlag), typeof(bool), typeof(float) })]
    static class P_SpawnB
    {
        [HarmonyPrefix] static bool Pre(ref Enemy __result)
        {
            if (SyncOn.Client && !EnemySync.NetSpawning && !EnemySync.AllowLocal) { EnemySync.NoteBlocked("spawn"); __result = null; return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(EnemyManager), nameof(EnemyManager.SpawnBoss))]
    static class P_SpawnBoss
    {
        [HarmonyPrefix] static bool Pre(ref Enemy __result)
        {
            if (SyncOn.Client && !EnemySync.NetSpawning && !EnemySync.AllowLocal) { EnemySync.NoteBlocked("spawn"); __result = null; return false; }
            return true;
        }
    }

    // ---- client: remember the enemies his own shrines/eggs spawned
    [HarmonyPatch(typeof(EnemyManager), nameof(EnemyManager.SpawnEnemy),
        new[] { typeof(EnemyData), typeof(int), typeof(bool), typeof(EEnemyFlag), typeof(bool) })]
    static class P_SpawnALocal { [HarmonyPostfix] static void Post(Enemy __result) => EnemySync.NoteLocal(__result); }

    [HarmonyPatch(typeof(EnemyManager), nameof(EnemyManager.SpawnEnemy),
        new[] { typeof(EnemyData), typeof(UnityEngine.Vector3), typeof(int), typeof(bool), typeof(EEnemyFlag), typeof(bool), typeof(float) })]
    static class P_SpawnBLocal { [HarmonyPostfix] static void Post(Enemy __result) => EnemySync.NoteLocal(__result); }

    [HarmonyPatch(typeof(EnemyManager), nameof(EnemyManager.SpawnBoss))]
    static class P_SpawnBossLocal { [HarmonyPostfix] static void Post(Enemy __result) => EnemySync.NoteLocal(__result); }

    // ---- host: share the wave spawns between the two players (the manager only places them around the host)
    [HarmonyPatch(typeof(EnemyManager), nameof(EnemyManager.SpawnEnemy),
        new[] { typeof(EnemyData), typeof(int), typeof(bool), typeof(EEnemyFlag), typeof(bool) })]
    static class P_SpawnSplit
    {
        [HarmonyPostfix] static void Post(Enemy __result) { if (SyncOn.Host) EnemySync.HostSplitSpawn(__result); }
    }

    // ---- client: hits on a copy are forwarded to the host instead of being applied locally
    static class DamageIntercept
    {
        public static bool Run(Enemy e, DamageContainer dc)
        {
            if (SyncOn.Host && e != null && dc != null && !EnemySync.ApplyingFriend) CoopStats.HostHit(e.id, dc.damage);
            if (!SyncOn.Client || e == null || dc == null) return true;
            if (!EnemySync.IsCopy(e, out uint hostId)) return true;
            EnemySync.SendDamage(hostId, dc);
            return false;      // skip the local damage
        }
    }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.Damage))]
    static class P_Damage { [HarmonyPrefix] static bool Pre(Enemy __instance, DamageContainer __0) => DamageIntercept.Run(__instance, __0); }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.DamageFromPlayerWeapon))]
    static class P_DamageWeapon { [HarmonyPrefix] static bool Pre(Enemy __instance, DamageContainer __0) => DamageIntercept.Run(__instance, __0); }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.DamageFromPlayerOther))]
    static class P_DamageOther { [HarmonyPrefix] static bool Pre(Enemy __instance, DamageContainer __0) => DamageIntercept.Run(__instance, __0); }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.DamageExternal))]
    static class P_DamageExternal { [HarmonyPrefix] static bool Pre(Enemy __instance, DamageContainer __0) => DamageIntercept.Run(__instance, __0); }

    // ---- host: announce deaths and removals
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.EnemyDied), new Type[0])]
    static class P_DiedA { [HarmonyPostfix] static void Post(Enemy __instance) { if (SyncOn.Host) EnemySync.HostEnemyDied(__instance); } }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.EnemyDied), new[] { typeof(DamageContainer) })]
    static class P_DiedB { [HarmonyPostfix] static void Post(Enemy __instance) { if (SyncOn.Host) EnemySync.HostEnemyDied(__instance); } }

    [HarmonyPatch(typeof(EnemyManager), nameof(EnemyManager.RemoveEnemy))]
    static class P_Remove { [HarmonyPostfix] static void Post(Enemy __0) { if (SyncOn.Host) EnemySync.HostEnemyRemoved(__0); } }
}
