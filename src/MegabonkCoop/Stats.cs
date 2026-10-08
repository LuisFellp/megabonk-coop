using System;
using System.Collections.Generic;
using System.IO;

namespace MegabonkCoop
{
    // Damage and kills per player. The HOST counts everything (its own hits, and the friend's hits when it applies them) and sends the
    // totals to the friend every 2 s. A kill goes to whoever hit the enemy last.
    public static class CoopStats
    {
        public static float HostDmg, ClientDmg;
        public static int HostKills, ClientKills;
        public static bool Any => HostDmg > 0f || ClientDmg > 0f || HostKills > 0 || ClientKills > 0;
        static readonly Dictionary<uint, byte> lastHit = new Dictionary<uint, byte>();
        static float nextSend, nextLog;

        public static void Reset()
        {
            HostDmg = ClientDmg = 0f; HostKills = ClientKills = 0; lastHit.Clear();
        }

        public static void HostHit(uint id, float dmg)
        {
            if (dmg <= 0f || float.IsNaN(dmg) || float.IsInfinity(dmg)) return;
            HostDmg += dmg; lastHit[id] = 0;
        }

        public static void ClientHit(uint id, float dmg)
        {
            if (dmg <= 0f || float.IsNaN(dmg) || float.IsInfinity(dmg)) return;
            ClientDmg += dmg; lastHit[id] = 1;
        }

        public static void HostKill(uint id)
        {
            if (!lastHit.TryGetValue(id, out byte who)) return;
            lastHit.Remove(id);
            if (who == 0) HostKills++; else ClientKills++;
            if (lastHit.Count > 4000) lastHit.Clear();
        }

        public static void Tick(float now)
        {
            if (!Coop.IsHost || !Coop.Net.Connected || !Any || now < nextSend) return;
            nextSend = now + 2f;
            float hd = HostDmg, cd = ClientDmg; int hk = HostKills, ck = ClientKills;
            Coop.Net.Send(Coop.Pack(Msg.Stats, w => { w.Write(hd); w.Write(cd); w.Write(hk); w.Write(ck); }));
            if (now > nextLog) { nextLog = now + 15f; Plugin.Info($"[stats] host dealt {hd:F0} dmg / {hk} kills, friend dealt {cd:F0} dmg / {ck} kills"); }
        }

        static bool loggedRecv;
        public static void Handle(BinaryReader br)
        {
            if (Coop.IsHost) return;
            float hd = br.ReadSingle(), cd = br.ReadSingle(); int hk = br.ReadInt32(), ck = br.ReadInt32();
            HostDmg = hd; ClientDmg = cd; HostKills = hk; ClientKills = ck;
            if (!loggedRecv) { loggedRecv = true; Plugin.Info($"[stats] received from host: host {hd:F0} dmg / {hk} kills, me {cd:F0} dmg / {ck} kills"); }
        }

        public static string Compact(float v) => v >= 1e6f ? (v / 1e6f).ToString("0.#") + "m" : v >= 1e3f ? (v / 1e3f).ToString("0.#") + "k" : v.ToString("0");

        // "Dano 12k x 9k   Abates 40 x 31"  (me first)
        public static string Line()
        {
            bool host = Coop.IsHost;
            float mine = host ? HostDmg : ClientDmg, theirs = host ? ClientDmg : HostDmg;
            int mk = host ? HostKills : ClientKills, tk = host ? ClientKills : HostKills;
            return $"Dano  voce {Compact(mine)}  amigo {Compact(theirs)}     Abates  voce {mk}  amigo {tk}";
        }
    }
}
