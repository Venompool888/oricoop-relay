using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OriCoop.Relay
{
    // Raw UDP checks keep the server contract independent of the game client's transport.
    internal static class RelaySeatTest
    {
        private static readonly Dictionary<Socket, List<byte[]>> pending = new Dictionary<Socket, List<byte[]>>();

        private static Socket Client()
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            socket.Blocking = false;
            return socket;
        }

        private static byte[] Receive(RelayServer server, Socket socket, byte op)
        {
            List<byte[]> queued;
            if (!pending.TryGetValue(socket, out queued))
            {
                queued = new List<byte[]>();
                pending.Add(socket, queued);
            }
            for (int i = 0; i < queued.Count; i++)
                if (op == 0 || queued[i][2] == op)
                {
                    byte[] found = queued[i];
                    queued.RemoveAt(i);
                    return found;
                }
            DateTime end = DateTime.UtcNow.AddSeconds(1);
            do
            {
                server.Poll();
                while (socket.Poll(0, SelectMode.SelectRead))
                {
                    byte[] packet = new byte[4096];
                    int count = socket.Receive(packet);
                    if (count < 3) continue;
                    Array.Resize(ref packet, count);
                    if (op == 0 || packet[2] == op) return packet;
                    queued.Add(packet);
                }
                Thread.Sleep(1);
            } while (DateTime.UtcNow < end);
            throw new Exception("missing op " + op);
        }

        private static byte[] CodePacket(byte op, string code)
        {
            byte[] packet = RelayWire.JoinRoom(code);
            packet[2] = op;
            return packet;
        }

        private static byte[] Identity(byte[] packet, ulong id)
        {
            int offset = packet.Length;
            Array.Resize(ref packet, offset + 12);
            packet[offset] = 0x49;
            packet[offset + 1] = 1;
            for (int i = 0; i < 8; i++) packet[offset + 2 + i] = (byte)(id >> (8 * i));
            packet[offset + 10] = 1;
            packet[offset + 11] = (byte)'X';
            return packet;
        }

        private static byte[] Relay(string code, byte target, byte[] payload)
        {
            byte[] packet = new byte[12 + payload.Length];
            RelayWire.WriteMagicOp(packet, RelayProtocol.Relay);
            RelayWire.WriteCode(packet, 3, code);
            packet[9] = target;
            RelayWire.WriteU16(packet, 10, (ushort)payload.Length);
            Buffer.BlockCopy(payload, 0, packet, 12, payload.Length);
            return packet;
        }

        private static bool Endpoint(byte[] packet, EndPoint endpoint)
        {
            byte[] expected = new byte[6];
            RelayWire.WriteEndpoint(expected, 0, endpoint);
            if (packet.Length != 16) return false;
            for (int i = 0; i < 6; i++) if (packet[10 + i] != expected[i]) return false;
            return true;
        }

        private static bool Payload(byte[] packet, byte source, byte[] payload)
        {
            if (packet.Length != 6 + payload.Length || packet[3] != source ||
                RelayWire.ReadU16(packet, 4) != payload.Length) return false;
            for (int i = 0; i < payload.Length; i++) if (packet[6 + i] != payload[i]) return false;
            return true;
        }

        private static bool Quiet(RelayServer server, params Socket[] sockets)
        {
            for (int i = 0; i < 30; i++)
            {
                server.Poll();
                foreach (Socket socket in sockets)
                    if ((pending.ContainsKey(socket) && pending[socket].Count > 0) || socket.Poll(0, SelectMode.SelectRead)) return false;
                Thread.Sleep(1);
            }
            return true;
        }

        internal static void Run(Action<string, bool, string> check)
        {
            pending.Clear();
            using (RelayServer server = new RelayServer(0))
            using (Socket host = Client())
            using (Socket g2 = Client())
            using (Socket g3 = Client())
            using (Socket g4 = Client())
            using (Socket extra = Client())
            {
                List<string> lines = new List<string>();
                server.Players = new PlayerLog(lines.Add, null, 30, null);
                EndPoint address = new IPEndPoint(IPAddress.Loopback, server.Port);
                host.SendTo(Identity(RelayWire.HostRoom(42), 101), address);
                string code = RelayWire.ReadCode(Receive(server, host, RelayProtocol.RoomCreated), 7);
                Socket[] guests = { g2, g3, g4 };
                bool seats = true;
                for (int i = 0; i < guests.Length; i++)
                {
                    guests[i].SendTo(Identity(RelayWire.JoinRoom(code), (ulong)(102 + i)), address);
                    byte[] joined = Receive(server, guests[i], RelayProtocol.Joined);
                    byte[] peer = Receive(server, host, RelayProtocol.PeerJoined);
                    seats &= joined.Length == 16 && joined[9] == i + 2 && Endpoint(joined, host.LocalEndPoint) &&
                        peer.Length == 16 && peer[9] == i + 2 && Endpoint(peer, guests[i].LocalEndPoint);
                }
                extra.SendTo(RelayWire.JoinRoom(code), address);
                byte[] full = Receive(server, extra, RelayProtocol.JoinFailed);
                seats &= Encoding.UTF8.GetString(full, 4, full[3]) == "room full";
                check("n four seats", seats, "seats 2/3/4, endpoints per seat, fifth player refused");

                bool punched = true;
                for (int i = 0; i < guests.Length; i++)
                {
                    byte[] punch = new byte[11];
                    punch[0] = 2;
                    RelayWire.WriteCode(punch, 1, code);
                    RelayWire.WriteU32(punch, 7, (uint)(i + 2));
                    guests[i].SendTo(punch, host.LocalEndPoint);
                    byte[] received = Receive(server, host, 0);
                    punched &= received.Length == 11 && received[0] == 2 && RelayWire.ReadU32(received, 7) == i + 2;
                    received[0] = 3;
                    host.SendTo(received, guests[i].LocalEndPoint);
                    byte[] ack = Receive(server, guests[i], 0);
                    punched &= ack.Length == 11 && ack[0] == 3 && RelayWire.ReadU32(ack, 7) == i + 2;
                }
                check("u direct per seat", punched, "each advertised endpoint exchanges a direct punch/ack");

                // Retry retains its seat and identity, with both rendezvous replies repeated.
                g3.SendTo(RelayWire.JoinRoom(code), address);
                byte[] retry = Receive(server, g3, RelayProtocol.Joined);
                byte[] retriedPeer = Receive(server, host, RelayProtocol.PeerJoined);
                bool retried = retry.Length == 16 && retry[9] == 3 && retriedPeer.Length == 16 && retriedPeer[9] == 3;
                byte[] payload = { 7, 0, 255, 42 };
                host.SendTo(Relay(code, 3, payload), address);
                bool routed = Payload(Receive(server, g3, RelayProtocol.Relayed), 1, payload);
                routed &= Quiet(server, g2, g4, host);
                // A guest cannot address another guest; invalid host targets are dropped.
                g2.SendTo(Relay(code, 4, payload), address);
                host.SendTo(Relay(code, 1, payload), address);
                host.SendTo(Relay(code, 5, payload), address);
                routed &= Quiet(server, host, g2, g3, g4);
                check("o target routing", routed && retried, "host -> only seat 3; retry keeps seat; invalid routes dropped");

                g4.SendTo(Relay(code, 1, payload), address);
                bool sourced = Payload(Receive(server, host, RelayProtocol.Relayed), 4, payload);
                sourced &= Quiet(server, g2, g3, g4);
                check("p source routing", sourced, "seat 4 -> only host, source seat 4");

                g3.SendTo(CodePacket(RelayProtocol.LeaveRoom, code), address);
                byte[] left = Receive(server, host, RelayProtocol.PeerLeft);
                extra.SendTo(Identity(RelayWire.JoinRoom(code), 105), address);
                byte[] reused = Receive(server, extra, RelayProtocol.Joined);
                Receive(server, host, RelayProtocol.PeerJoined);
                // A delayed duplicate leave from the retired endpoint cannot evict its replacement.
                g3.SendTo(CodePacket(RelayProtocol.LeaveRoom, code), address);
                host.SendTo(Relay(code, 3, payload), address);
                bool reuse = left.Length == 10 && left[9] == 3 && reused.Length == 16 && reused[9] == 3 &&
                    Payload(Receive(server, extra, RelayProtocol.Relayed), 1, payload);
                reuse &= Quiet(server, host, g2, g3, g4);
                check("q seat reuse", reuse, "leave seat 3, reuse lowest free seat, stale leave ignored");

                // Timeout only seat 2, keeping every other seat and the host alive.
                server.GuestTimeoutSeconds = 0.12;
                DateTime until = DateTime.UtcNow.AddMilliseconds(230);
                do
                {
                    host.SendTo(RelayWire.KeepAlive(code), address);
                    g4.SendTo(RelayWire.KeepAlive(code), address);
                    extra.SendTo(RelayWire.KeepAlive(code), address);
                    Receive(server, host, RelayProtocol.KeepAliveAck);
                    Receive(server, g4, RelayProtocol.KeepAliveAck);
                    Receive(server, extra, RelayProtocol.KeepAliveAck);
                    Thread.Sleep(10);
                } while (DateTime.UtcNow < until);
                left = Receive(server, host, RelayProtocol.PeerLeft);
                g3.SendTo(Identity(RelayWire.JoinRoom(code), 106), address);
                reused = Receive(server, g3, RelayProtocol.Joined);
                Receive(server, host, RelayProtocol.PeerJoined);
                bool timeout = left.Length == 10 && left[9] == 2 && reused.Length == 16 && reused[9] == 2;
                server.GuestTimeoutSeconds = 30;
                // Preserve silent-guest replacement when all three slots are occupied.
                server.GuestReplaceSeconds = 0.04;
                Thread.Sleep(60);
                g4.SendTo(RelayWire.KeepAlive(code), address);
                extra.SendTo(RelayWire.KeepAlive(code), address);
                Receive(server, g4, RelayProtocol.KeepAliveAck);
                Receive(server, extra, RelayProtocol.KeepAliveAck);
                g2.SendTo(Identity(RelayWire.JoinRoom(code), 107), address);
                reused = Receive(server, g2, RelayProtocol.Joined);
                left = Receive(server, host, RelayProtocol.PeerLeft);
                Receive(server, host, RelayProtocol.PeerJoined);
                timeout &= reused.Length == 16 && reused[9] == 2 && left.Length == 10 && left[9] == 2;
                check("t seat expiration", timeout, "individual timeout/reuse and full-room silent replacement");

                // Recreating the host room must log all remaining identities exactly once.
                host.SendTo(RelayWire.HostRoom(43), address);
                Receive(server, host, RelayProtocol.RoomCreated);
                bool logged = lines.Count == 15;
                for (int seat = 1; seat <= 4; seat++)
                {
                    ulong id = seat == 1 ? 101UL : seat == 2 ? 107UL : seat == 3 ? 105UL : 104UL;
                    string role = seat == 1 ? "host" : "guest";
                    logged &= lines.Exists(delegate(string line) { return line.StartsWith("PLAYER JOIN ") &&
                        line.Contains("role=" + role + " ") && line.Contains("steamid=" + id + " ") && line.Contains("seat=" + seat); });
                    logged &= lines.Exists(delegate(string line) { return line.StartsWith("PLAYER LEAVE ") &&
                        line.Contains("steamid=" + id + " ") && line.Contains("seat=" + seat); });
                }
                logged &= lines.FindAll(delegate(string line) { return line.StartsWith("PLAYER JOIN ") && line.Contains("steamid=103 "); }).Count == 1;
                check("r per-seat player log", logged, "join/leave identities per seat, retries not logged twice, room closure logs all seats");

                byte[][] legacy = { RelayWire.HostRoom(44), RelayWire.JoinRoom(code) };
                bool refused = true;
                foreach (byte[] request in legacy)
                {
                    // Real v1 header, not a truncated v2 request. Errors retain v1 magic so old clients can read them.
                    RelayWire.WriteU16(request, 0, 0x524F);
                    extra.SendTo(request, address);
                    byte[] failure = Receive(server, extra, RelayProtocol.JoinFailed);
                    refused &= RelayWire.ReadU16(failure, 0) == 0x524F &&
                        Encoding.UTF8.GetString(failure, 4, failure[3]) == "relay protocol v2 required";
                }
                check("s v1 refused", refused && server.RoomCount == 1, "v1 HostRoom/JoinRoom receive explicit error and cannot mutate rooms");
            }
        }
    }
}
