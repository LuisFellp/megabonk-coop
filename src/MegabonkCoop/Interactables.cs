using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace MegabonkCoop
{
    // Interactables that SPAWN enemies need special care: the client never spawns enemies of its own (the host does), so on the client their
    // Interact() threw a NullReferenceException half way (the game used the null Enemy our spawn block returned). Result seen in play: the shrine
    // of the two swords kept its "!" (alertIcon) and nothing happened.
    //   * Challenge shrine, quest egg, character fight : run LOCALLY on whoever interacts (like chests: the loot/reward is individual). The enemies
    //     they spawn belong to that player only (they are exempt from the sweep and are not mirrored).
    //   * Boss spawner : the stage boss is SHARED, so the client asks the HOST to summon it (Msg.Interact); the boss then reaches the client as a
    //     normal synced enemy.
    static class Inter
    {
        public static void BeginLocal() { if (Coop.IsClient) EnemySync.AllowLocal = true; }
        public static void EndLocal() { EnemySync.AllowLocal = false; }
    }

    // EVERY interactable's Interact() override gets the same treatment (patched dynamically at load, see ApplyAll): while it runs on the client, enemies
    // it spawns are allowed and become the client's own. Covers shrines, eggs, graves, coffins, character fights... including types not exercised in tests.
    public static class InterPatches
    {
        public static void PreAny() => Inter.BeginLocal();
        public static Exception FinAny(Exception __exception) { Inter.EndLocal(); return __exception; }

        // Only the types PROVEN to spawn enemies are wrapped. Wrapping every override caused thousands of NullReference errors (the IL2CPP linker
        // folds identical methods into one native address, so Harmony then redirects native calls made with a null 'this' into our managed trampoline).
        static readonly System.Collections.Generic.HashSet<string> Spawners = new System.Collections.Generic.HashSet<string>
        {
            "InteractableShrineChallenge", "InteractableEgg", "InteractableCharacterFight", "InteractableGravestone", "InteractableCoffin",
            "InteractableSkeletonKingStatue", "InteractableDesertGrave",
        };

        // item effects that spawn an entity through the enemy manager (the ghost allies): same rule, they are the owner's alone
        static void ApplyItems(Harmony harmony)
        {
            try
            {
                var pre = new HarmonyMethod(typeof(InterPatches).GetMethod(nameof(PreAny)));
                var fin = new HarmonyMethod(typeof(InterPatches).GetMethod(nameof(FinAny)));
                int ok = 0;
                foreach (var t in new[] { typeof(Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations.ItemGhost), typeof(Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations.ItemBobDead) })
                {
                    var m = t.GetMethod("SpawnGhost", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (m == null) { Plugin.Info($"[patch] {t.Name}.SpawnGhost not found"); continue; }
                    harmony.Patch(m, prefix: pre, finalizer: fin); ok++;
                }
                Plugin.Info($"[patch] {ok} item spawn methods wrapped for local spawns");
            }
            catch (Exception e) { Plugin.Info("[patch] item spawn wrapping failed: " + e.Message); }
        }

        public static void ApplyAll(Harmony harmony)
        {
            ApplyItems(harmony);
            int n = 0, bad = 0;
            try
            {
                var pre = new HarmonyMethod(typeof(InterPatches).GetMethod(nameof(PreAny)));
                var fin = new HarmonyMethod(typeof(InterPatches).GetMethod(nameof(FinAny)));
                foreach (var t in typeof(BaseInteractable).Assembly.GetTypes())
                {
                    try
                    {
                        if (t == typeof(BaseInteractable) || t == typeof(InteractableBossSpawner) || !typeof(BaseInteractable).IsAssignableFrom(t)) continue;
                        if (!Spawners.Contains(t.Name) && !Plugin.WrapAllInteractables.Value) continue;
                        var m = t.GetMethod("Interact", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        if (m == null) continue;
                        harmony.Patch(m, prefix: pre, finalizer: fin); n++;
                    }
                    catch (Exception e) { bad++; Plugin.Info($"[patch] interactable {t.Name} not patched: {e.GetBaseException().Message}"); }
                }
            }
            catch (Exception e) { Plugin.Info("[patch] interactable scan failed: " + e.Message); }
            Plugin.Info($"[patch] {n} interactable Interact() overrides wrapped for local spawns ({bad} failed)");
        }
    }

    // ---- boss spawner: the client asks the host
    [HarmonyPatch(typeof(InteractableBossSpawner), nameof(InteractableBossSpawner.Interact))]
    static class P_IntBoss
    {
        public static bool Forwarded;                 // set by the host while it executes a request from the friend
        static float nextAsk;

        [HarmonyPrefix]
        static bool Pre(ref bool __result)
        {
            if (!Coop.IsClient || !Plugin.SyncEnemies.Value || Coop.PeerLost || Coop.HostLeftRun || Forwarded) return true;
            float now = Time.realtimeSinceStartup;
            __result = true;
            if (now < nextAsk) return false;
            nextAsk = now + 3f;
            try { Coop.Net.SendReliable(Coop.Pack(Msg.Interact, w => w.Write((byte)1))); } catch { }
            Plugin.Info("[interact] boss spawner: asked the host to summon the boss");
            Coop.Toast("Pedido enviado ao host: o chefe e dos dois!", 1, 4f);
            return false;
        }
    }

    public static class InteractNet
    {
        // host side: the friend asked for something
        public static void Handle(BinaryReader br)
        {
            byte kind = br.ReadByte();
            if (!Coop.IsHost) return;
            if (kind != 1) return;
            try
            {
                foreach (var it in UnityEngine.Object.FindObjectsOfType<InteractableBossSpawner>())
                {
                    if (it == null) continue;
                    bool can = false; try { can = it.CanInteract(); } catch { }
                    if (!can) { Plugin.Info("[interact] friend asked for the boss, but the spawner cannot be used now (already summoned?)"); return; }
                    P_IntBoss.Forwarded = true;
                    try { it.Interact(); Plugin.Info("[interact] friend asked for the boss: summoned by the host"); Coop.Toast("O amigo chamou o chefe!", 1, 4f); }
                    finally { P_IntBoss.Forwarded = false; }
                    return;
                }
                Plugin.Info("[interact] friend asked for the boss, but this stage has no boss spawner");
            }
            catch (Exception e) { Plugin.Info("[interact] boss request failed: " + e.Message); }
        }
    }
}
