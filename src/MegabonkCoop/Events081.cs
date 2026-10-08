using Assets.Scripts._Data.Tomes;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using HarmonyLib;

namespace MegabonkCoop
{
    // What each player picks up is announced in the chat of the other one (and written to the log):
    // "Luis pegou: arma FireStaff", "item Borgor", "tomo Damage", "buff Rage".
    static class Ev
    {
        public static bool Ready => Coop.Net.Active && Coop.Net.Connected && Assets.Scripts.Actors.Player.MyPlayer.Instance != null;
        static string last; static float lastAt, windowStart; static int count, skipped;
        public static void Say(string text)
        {
            if (!Ready) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (text == last && now - lastAt < 0.5f) return;          // the game sometimes calls the same add twice in a row
            last = text; lastAt = now;
            // at most 6 announcements per 5 s (a run start adds many starting items at once); the rest is only logged
            if (now - windowStart > 5f) { windowStart = now; count = 0; if (skipped > 0) { Coop.SendEvent($"pegou mais {skipped} coisas em sequencia"); skipped = 0; } }
            if (++count > 6) { skipped++; Plugin.Info($"[event] (not announced, rate limit) pegou: {text}"); return; }
            Coop.SendEvent("pegou: " + text);
        }
    }

    [HarmonyPatch(typeof(WeaponInventory), nameof(WeaponInventory.AddWeapon))]
    static class P_EvWeapon
    {
        [HarmonyPostfix]
        static void Post(WeaponInventory __instance, WeaponData weaponData)
        {
            if (weaponData == null) return;
            int lvl = 1; try { lvl = __instance.GetWeaponLevel(weaponData.eWeapon); } catch { }
            Ev.Say(lvl > 1 ? $"arma {weaponData.eWeapon} (nivel {lvl})" : $"arma {weaponData.eWeapon}");
        }
    }

    [HarmonyPatch(typeof(ItemInventory), nameof(ItemInventory.AddItem), new[] { typeof(EItem) })]
    static class P_EvItem1 { [HarmonyPostfix] static void Post(EItem eItem) => Ev.Say($"item {eItem}"); }

    [HarmonyPatch(typeof(ItemInventory), nameof(ItemInventory.AddItem), new[] { typeof(EItem), typeof(int) })]
    static class P_EvItem2 { [HarmonyPostfix] static void Post(EItem eItem, int count) => Ev.Say(count > 1 ? $"item {eItem} x{count}" : $"item {eItem}"); }

    [HarmonyPatch(typeof(TomeInventory), nameof(TomeInventory.AddTome))]
    static class P_EvTome
    {
        [HarmonyPostfix]
        static void Post(TomeInventory __instance, TomeData tomeData, ERarity rarity)
        {
            if (tomeData == null) return;
            int lvl = 1; try { lvl = __instance.GetTomeLevel(tomeData.eTome); } catch { }
            Ev.Say($"tomo {tomeData.eTome} (nivel {lvl}, {rarity})");
        }
    }

    // buffs picked up from the floor (xp / gold / silver are too frequent to announce)
    [HarmonyPatch(typeof(Pickup), nameof(Pickup.ApplyPickup))]
    static class P_EvPickup
    {
        [HarmonyPostfix]
        static void Post(Pickup __instance)
        {
            if (__instance == null) return;
            var t = __instance.ePickup;
            if (t == Assets.Scripts.Inventory__Items__Pickups.Pickups.EPickup.Xp ||
                t == Assets.Scripts.Inventory__Items__Pickups.Pickups.EPickup.Gold ||
                t == Assets.Scripts.Inventory__Items__Pickups.Pickups.EPickup.Silver) return;
            Ev.Say($"buff {t}");
        }
    }
}
