using Assets.Scripts.Inventory__Items__Pickups;
using HarmonyLib;

namespace MegabonkCoop
{
    // my player died -> tell the friend
    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.KillPlayer))]
    static class P_KillPlayer
    {
        [HarmonyPostfix]
        static void Post()
        {
            Coop.LocalDead = true;
            Plugin.Info("[player] my player died");
            if (Coop.Net.Active && Coop.Net.Connected)
            {
                string me = Coop.LocalName ?? "Amigo";
                Coop.Net.SendReliable(Coop.Pack(Msg.PlayerDied, w => w.Write(me)));
            }
        }
    }
}
