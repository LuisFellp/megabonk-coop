using System;
using System.IO;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups;
using Inventory__Items__Pickups.Xp_and_Levels;
using HarmonyLib;
using UnityEngine;

namespace MegabonkCoop
{
    // 100 % shared XP and gold: whatever one player GAINS, the other gains too, in full (a sum, not a split).
    // Spending gold (chests, shops) stays individual: each one pays for his own.
    // Gains are batched for 0.25 s and sent reliably; the receiver applies them with `applying` set so they are never echoed back.
    public static class Share
    {
        public static bool applying;
        static int pendXp, pendGold, totalSentXp, totalSentGold, totalRecvXp, totalRecvGold;
        static float nextSend, nextLog, nextRecvLog; static int accX, accG, accN;

        static bool Ready => Plugin.ShareXpGold.Value && Coop.Net.Active && Coop.Net.Connected && !Coop.PeerLost && MyPlayer.Instance != null;

        // Running totals of everything the PAIR gained in this run (own gains + the friend's). A player who died and re-joins the run starts
        // from zero and asks the host for these totals, so he comes back with the same level and gold earnings (a revive with parity).
        public static long runXp, runGold;
        static bool catchupPending; static float catchupAt;

        static bool Counting => Plugin.ShareXpGold.Value && Coop.Net.Active && Coop.Net.Connected;

        public static void OnXp(int amount) { if (applying || amount <= 0) return; if (Counting) runXp += amount; if (!Ready) return; pendXp += amount; }
        public static void OnGold(int amount) { if (applying || amount <= 0) return; if (Counting) runGold += amount; if (!Ready) return; pendGold += amount; }

        public static void NewRun() { runXp = runGold = 0; catchupPending = false; CoopStats.Reset(); Plugin.Info("[share] new run: shared totals reset"); }

        // called when I re-join a run in progress: my character starts from zero
        public static void RequestCatchup()
        {
            runXp = runGold = 0; catchupPending = true; catchupAt = Time.realtimeSinceStartup + 7f;
            Plugin.Info("[share] re-joining a run in progress: will ask the host for the shared totals in 7 s");
        }

        public static void Reset() { pendXp = pendGold = 0; catchupPending = false; }

        public static void HandleCatchup(BinaryReader br)
        {
            byte kind = br.ReadByte();
            if (kind == 0)
            {
                if (!Coop.IsHost || !Plugin.ShareXpGold.Value) return;
                int x = (int)Math.Min(runXp, 100000000L), g = (int)Math.Min(runGold, 100000000L);
                Coop.Net.SendReliable(Coop.Pack(Msg.Catchup, w => { w.Write((byte)1); w.Write(x); w.Write(g); }));
                Plugin.Info($"[share] friend asked for a catch-up: sending {x} xp, {g} gold");
            }
            else
            {
                int x = br.ReadInt32(), g = br.ReadInt32();
                var me = MyPlayer.Instance; if (me == null || me.inventory == null || !Plugin.ShareXpGold.Value) return;
                applying = true;
                try
                {
                    int lv0 = 0, lv1 = 0; try { lv0 = me.inventory.playerXp.level; } catch { }
                    if (x > 0) me.inventory.playerXp.AddXp(x);
                    if (g > 0) me.inventory.ChangeGold(g);
                    try { lv1 = me.inventory.playerXp.level; } catch { }
                    runXp += x; runGold += g;
                    Plugin.Info($"[share] catch-up applied: +{x} xp, +{g} gold (level {lv0}->{lv1})");
                    Coop.Toast($"Voce voltou com o progresso da dupla: nivel {lv1}", 1, 5f);
                }
                catch (Exception e) { Plugin.Info("[share] catch-up failed: " + e.Message); }
                finally { applying = false; }
            }
        }

        public static void Tick(float now)
        {
            if (catchupPending && now >= catchupAt && Ready)
            {
                catchupPending = false;
                Coop.Net.SendReliable(Coop.Pack(Msg.Catchup, w => w.Write((byte)0)));
                Plugin.Info("[share] asked the host for the shared totals");
            }
            if (now < nextSend) return;
            nextSend = now + 0.25f;
            if (pendXp == 0 && pendGold == 0) return;
            if (!Ready) { pendXp = pendGold = 0; return; }
            int x = pendXp, g = pendGold; pendXp = pendGold = 0;
            totalSentXp += x; totalSentGold += g;
            Coop.Net.SendReliable(Coop.Pack(Msg.Share, w => { w.Write(x); w.Write(g); }));
            if (now > nextLog) { nextLog = now + 10f; Plugin.Info($"[share] sent +{x} xp, +{g} gold (totals sent: {totalSentXp} xp, {totalSentGold} gold)"); }
        }

        public static void Handle(BinaryReader br)
        {
            int x = br.ReadInt32(), g = br.ReadInt32();
            if (!Plugin.ShareXpGold.Value || x < 0 || g < 0 || x > 1000000 || g > 1000000) return;
            var me = MyPlayer.Instance; if (me == null || me.inventory == null) return;
            applying = true;
            try
            {
                int goldBefore = 0, xpBefore = 0, lvBefore = 0;
                try { goldBefore = me.inventory.goldInt; xpBefore = me.inventory.playerXp.xp; lvBefore = me.inventory.playerXp.level; } catch { }
                if (x > 0) me.inventory.playerXp.AddXp(x);
                if (g > 0) me.inventory.ChangeGold(g);
                totalRecvXp += x; totalRecvGold += g; runXp += x; runGold += g;
                int goldAfter = 0, xpAfter = 0, lvAfter = 0;
                try { goldAfter = me.inventory.goldInt; xpAfter = me.inventory.playerXp.xp; lvAfter = me.inventory.playerXp.level; } catch { }
                // small drops arrive several times a second: log them as one summary every 10 s (big ones, and level-ups, are logged one by one)
                accX += x; accG += g; accN++;
                float t = Time.realtimeSinceStartup;
                if (x >= 20 || g >= 40 || lvAfter != lvBefore)
                    Plugin.Info($"[share] received +{x} xp, +{g} gold from the friend: gold {goldBefore}->{goldAfter}, xp {xpBefore}->{xpAfter}, level {lvBefore}->{lvAfter} (totals: {totalRecvXp} xp, {totalRecvGold} gold)");
                else if (t >= nextRecvLog)
                {
                    nextRecvLog = t + 10f;
                    Plugin.Info($"[share] received in the last 10 s: +{accX} xp, +{accG} gold in {accN} messages (now gold {goldAfter}, level {lvAfter}; totals {totalRecvXp} xp, {totalRecvGold} gold)");
                    accX = accG = accN = 0;
                }
            }
            catch (Exception e) { Plugin.Info("[share] apply failed: " + e.Message); }
            finally { applying = false; }
        }
    }

    [HarmonyPatch(typeof(PlayerXp), nameof(PlayerXp.AddXp))]
    static class P_ShareXp { [HarmonyPostfix] static void Post(int amount) => Share.OnXp(amount); }

    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.ChangeGold))]
    static class P_ShareGold { [HarmonyPostfix] static void Post(int amount) => Share.OnGold(amount); }
}
