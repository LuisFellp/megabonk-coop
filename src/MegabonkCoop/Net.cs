using System;
using System.Net;
using System.Collections.Generic;
using System.Net.Sockets;
using UnityEngine;

namespace MegabonkCoop
{
    // Minimal UDP link between exactly two peers (host + client).
    public class Net
    {
        public static int Port => Plugin.PortCfg.Value;
        UdpClient udp;
        IPEndPoint peer;
        public long SentPackets, SentBytes, RecvPackets, RecvBytes;
        public bool Active => udp != null;
        public bool IsHost { get; private set; }
        public bool Connected => peer != null;

        // ------------------------------------------------------------------ reliable messages (on top of UDP)
        // envelope: [30][epoch u32][seq u32][inner message]   ack: [31][epoch u32][seq u32]
        // Critical, one-shot events (damage, deaths, stage change, chat, checks) use this. The sender resends every 150 ms
        // for up to 4 s until acknowledged; the receiver drops duplicates, so a hit is never applied twice.
        const byte RelEnvelope = 30, RelAck = 31;
        class Pend { public byte[] Data; public float First, Last; }
        readonly Dictionary<uint, Pend> pending = new Dictionary<uint, Pend>();
        readonly HashSet<uint> seen = new HashSet<uint>();
        readonly Queue<uint> seenOrder = new Queue<uint>();
        uint myEpoch = (uint)new System.Random().Next(1, int.MaxValue), peerEpoch, nextSeq = 1;
        public long RelSent, RelResent, RelAcked, RelDup, RelDropped;

        public string RelStats() => $"rel sent={RelSent} resent={RelResent} acked={RelAcked} dup={RelDup} dropped={RelDropped} pending={pending.Count}";

        public void SendReliable(byte[] inner)
        {
            if (udp == null || peer == null || inner == null) return;
            uint seq = nextSeq++;
            var pkt = new byte[9 + inner.Length];
            pkt[0] = RelEnvelope;
            Buffer.BlockCopy(BitConverter.GetBytes(myEpoch), 0, pkt, 1, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, pkt, 5, 4);
            Buffer.BlockCopy(inner, 0, pkt, 9, inner.Length);
            float now = Time.realtimeSinceStartup;
            pending[seq] = new Pend { Data = pkt, First = now, Last = now };
            RelSent++;
            Send(pkt);
        }

        // call every frame: resend what was not acknowledged yet
        public void Pump(float now)
        {
            if (delayed.Count > 0)
            {
                for (int i = delayed.Count - 1; i >= 0; i--)
                    if (delayed[i].Due <= now) { var dd = delayed[i]; delayed.RemoveAt(i); RawSend(dd.Data); }
            }
            if (pending.Count == 0 || udp == null || peer == null) return;
            List<uint> drop = null;
            foreach (var kv in pending)
            {
                var p = kv.Value;
                if (now - p.First > 4f) { (drop ??= new List<uint>()).Add(kv.Key); continue; }
                if (now - p.Last >= 0.15f) { p.Last = now; RelResent++; Send(p.Data); }
            }
            if (drop != null) foreach (var k in drop) { pending.Remove(k); RelDropped++; }
        }

        // Takes a raw datagram; returns the message to process, or null if it was only protocol (ack / duplicate).
        public byte[] Unwrap(byte[] d)
        {
            if (d == null || d.Length == 0) return d;
            if (d[0] == RelAck && d.Length >= 9)
            {
                uint ep = BitConverter.ToUInt32(d, 1), seq = BitConverter.ToUInt32(d, 5);
                if (ep == myEpoch && pending.Remove(seq)) RelAcked++;
                return null;
            }
            if (d[0] == RelEnvelope && d.Length >= 10)
            {
                uint ep = BitConverter.ToUInt32(d, 1), seq = BitConverter.ToUInt32(d, 5);
                var ack = new byte[9]; ack[0] = RelAck;
                Buffer.BlockCopy(d, 1, ack, 1, 4); Buffer.BlockCopy(d, 5, ack, 5, 4);
                Send(ack);                                           // always acknowledge (the previous ack may have been lost)
                if (ep != peerEpoch) { peerEpoch = ep; seen.Clear(); seenOrder.Clear(); }   // the other side restarted its link
                if (!seen.Add(seq)) { RelDup++; return null; }
                seenOrder.Enqueue(seq);
                if (seenOrder.Count > 4096) seen.Remove(seenOrder.Dequeue());
                var inner = new byte[d.Length - 9];
                Buffer.BlockCopy(d, 9, inner, 0, inner.Length);
                return inner;
            }
            return d;
        }

        public void Host()
        {
            Close();
            IsHost = true;
            udp = new UdpClient(Port);
            IgnoreConnReset();
            Plugin.Info($"Hosting on UDP {Port}");
        }

        public bool Join(string ip)
        {
            if (!IPAddress.TryParse((ip ?? "").Trim(), out var addr))
            {
                Plugin.Info($"[net] invalid IP '{ip}'");
                return false;
            }
            Close();
            IsHost = false;
            udp = new UdpClient(0);
            IgnoreConnReset();
            peer = new IPEndPoint(addr, Port);
            Plugin.Info($"Joining {addr}:{Port}");
            return true;
        }

        // Windows reports ICMP "port unreachable" as a recv exception on UDP; ignore it.
        void IgnoreConnReset()
        {
            try { udp.Client.IOControl((IOControlCode)(-1744830452), new byte[] { 0 }, null); } catch { }
        }

        readonly System.Random lossRng = new System.Random();
        public long LossDropped;

        public void Send(byte[] data)
        {
            if (udp == null || peer == null) return;
            if (Plugin.DevLoss.Value > 0 && lossRng.Next(100) < Plugin.DevLoss.Value) { LossDropped++; return; }   // simulated packet loss
            if (Plugin.DevLatency.Value > 0)       // simulated VPN latency + jitter
            {
                float d = (Plugin.DevLatency.Value + ((float)lossRng.NextDouble() * 2f - 1f) * Plugin.DevJitter.Value) / 1000f;
                delayed.Add(new Delayed { Due = Time.realtimeSinceStartup + Mathf.Max(0f, d), Data = data });
                return;
            }
            RawSend(data);
        }

        struct Delayed { public float Due; public byte[] Data; }
        readonly List<Delayed> delayed = new List<Delayed>();

        void RawSend(byte[] data)
        {
            if (udp == null || peer == null) return;
            try { udp.Send(data, data.Length, peer); SentPackets++; SentBytes += data.Length; } catch (Exception e) { Plugin.Info("send: " + e.Message); }
        }

        // Returns next datagram or null. Host learns the client's endpoint from its first packet.
        public bool HasData() => udp != null && udp.Available > 0;

        float lastPeerRecv;
        string lastStranger;

        public byte[] Receive()
        {
            if (udp == null || udp.Available == 0) return null;
            IPEndPoint from = null;
            try
            {
                var data = udp.Receive(ref from);
                RecvPackets++; RecvBytes += data.Length;
                float now = Time.realtimeSinceStartup;
                if (IsHost && peer == null) { peer = from; Plugin.Info("Client connected: " + from); }
                else if (peer != null && !from.Equals(peer))
                {
                    // someone else is sending to our port. Accept a NEW address only when the current friend has been silent for 5 s
                    // (his connection was re-created); otherwise ignore the datagram.
                    if (IsHost && now - lastPeerRecv > 5f) { Plugin.Info($"[net] peer address changed {peer} -> {from} (old one silent for {now - lastPeerRecv:F0}s)"); peer = from; }
                    else
                    {
                        if (lastStranger != from.ToString()) { lastStranger = from.ToString(); Plugin.Info($"[net] ignoring datagrams from {from} (not my friend {peer})"); }
                        return null;
                    }
                }
                lastPeerRecv = now;
                return data;
            }
            catch (Exception e) { Plugin.Info("recv: " + e.Message); return null; }
        }

        public void Close()
        {
            if (udp != null) Plugin.Info($"[net] closed. sent={SentPackets}pk recv={RecvPackets}pk");
            udp?.Close();
            udp = null; peer = null; delayed.Clear();
            pending.Clear(); seen.Clear(); seenOrder.Clear(); nextSeq = 1; peerEpoch = 0;
            myEpoch = (uint)new System.Random().Next(1, int.MaxValue);
            RelSent = RelResent = RelAcked = RelDup = RelDropped = 0;
            SentPackets = SentBytes = RecvPackets = RecvBytes = 0;
        }
    }
}
