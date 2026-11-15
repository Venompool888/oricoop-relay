using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OriCoop.Relay
{
    internal static class RelayProtocol
    {
        internal const ushort Magic = 0x524F;
        internal const byte HostRoom = 1;
        internal const byte JoinRoom = 2;
        internal const byte RoomCreated = 3;
        internal const byte Joined = 4;
        internal const byte PeerJoined = 5;
        internal const byte JoinFailed = 6;
        internal const byte Relay = 7;
        internal const byte Relayed = 8;
        internal const byte KeepAlive = 9;
        internal const byte PeerLeft = 10;
        internal const byte Punch = 11;
        internal const byte PunchAck = 12;
        internal const byte KeepAliveAck = 13;
        internal const byte LeaveRoom = 14;
        internal const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    }

    // Wire helpers are also used by RelayTest, which builds raw server packets.
    internal static class RelayWire
    {
        internal static ushort ReadU16(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        internal static uint ReadU32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) |
                (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        internal static void WriteU16(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)(value >> 8);
        }

        internal static void WriteU32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
            data[offset + 2] = (byte)((value >> 16) & 0xFF);
            data[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        internal static void WriteMagicOp(byte[] data, byte op)
        {
            data[0] = (byte)(RelayProtocol.Magic & 0xFF);
            data[1] = (byte)(RelayProtocol.Magic >> 8);
            data[2] = op;
        }

        internal static void WriteCode(byte[] data, int offset, string code)
        {
            Encoding.ASCII.GetBytes(code, 0, 6, data, offset);
        }

        internal static string ReadCode(byte[] data, int offset)
        {
            return Encoding.ASCII.GetString(data, offset, 6);
        }

        internal static void WriteEndpoint(byte[] data, int offset, EndPoint endpoint)
        {
            IPEndPoint ip = endpoint as IPEndPoint;
            byte[] address = ip == null ? new byte[] { 0, 0, 0, 0 } : ip.Address.GetAddressBytes();
            if (address.Length != 4) address = new byte[] { 0, 0, 0, 0 };
            data[offset] = address[0];
            data[offset + 1] = address[1];
            data[offset + 2] = address[2];
            data[offset + 3] = address[3];
            WriteU16(data, offset + 4, ip == null ? (ushort)0 : (ushort)ip.Port);
        }

        internal static byte[] HostRoom(uint nonce)
        {
            byte[] packet = new byte[7];
            WriteMagicOp(packet, RelayProtocol.HostRoom);
            WriteU32(packet, 3, nonce);
            return packet;
        }

        internal static byte[] JoinRoom(string code)
        {
            byte[] packet = new byte[9];
            WriteMagicOp(packet, RelayProtocol.JoinRoom);
            WriteCode(packet, 3, code);
            return packet;
        }

        internal static byte[] KeepAlive(string code)
        {
            byte[] packet = new byte[9];
            WriteMagicOp(packet, RelayProtocol.KeepAlive);
            WriteCode(packet, 3, code);
            return packet;
        }

        internal static byte[] Relay(string code, byte[] payload, int offset, int length)
        {
            byte[] packet = new byte[11 + length];
            WriteMagicOp(packet, RelayProtocol.Relay);
            WriteCode(packet, 3, code);
            WriteU16(packet, 9, (ushort)length);
            Buffer.BlockCopy(payload, offset, packet, 11, length);
            return packet;
        }
    }

    // Single-threaded UDP rendezvous and relay. Poll() drains and times out.
    public sealed class RelayServer : IDisposable
    {
        public const int DefaultPort = 47020;

        private readonly Socket socket;
        private readonly int port;
        private readonly byte[] buffer = new byte[4096];
        private readonly Dictionary<string, Room> roomsByCode = new Dictionary<string, Room>();
        private readonly Dictionary<string, Room> roomsByHost = new Dictionary<string, Room>();
        private readonly Dictionary<string, RateLimit> rates = new Dictionary<string, RateLimit>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Random random = new Random();
        private double lastRateSweep;
        private bool disposed;

        private sealed class Room
        {
            internal string Code;
            internal EndPoint Host;
            internal EndPoint Guest;
            internal uint HostNonce;
            internal double HostSeen;
            internal double GuestSeen;
            // Admin log only (PlayerLog); never affects routing.
            internal PlayerIdentity HostId = PlayerIdentity.Unknown;
            internal PlayerIdentity GuestId = PlayerIdentity.Unknown;
            internal DateTime HostJoinedUtc;
            internal DateTime GuestJoinedUtc;
        }

        private sealed class RateLimit
        {
            internal double WindowStart;
            internal int Count;
        }

        // Seconds. A room dies after this long without a host packet; a guest slot
        // after this long without a guest packet.
        public double RoomTimeoutSeconds = 30.0;
        public double GuestTimeoutSeconds = 30.0;
        public double GuestReplaceSeconds = 12.0;
        public int MaxRooms = 1000;
        public int MaxPayload = 1300;
        public int MaxPacketsPerSecond = 200;
        // Join/leave lines with SteamID, IP and Steam name; null = off.
        public PlayerLog Players;

        public RelayServer(int port)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ReceiveBufferSize = 4 * 1024 * 1024;
                socket.SendBufferSize = 1024 * 1024;
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                socket.Blocking = false;
                this.port = ((IPEndPoint)socket.LocalEndPoint).Port;
                // Windows otherwise reports ICMP port-unreachable as a receive error.
                try { socket.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); }
                catch (Exception) { }
            }
            catch
            {
                socket.Close();
                throw;
            }
        }

        public int Port { get { return port; } }
        public int RoomCount { get { return roomsByCode.Count; } }

        private double Now { get { return clock.Elapsed.TotalSeconds; } }

        public void Poll()
        {
            if (disposed) return;
            while (true)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int length;
                try { length = socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref from); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { return; }
                if (length <= 0 || from == null) break;
                Handle(length, from);
            }
            double now = Now;
            Expire(now);
            if (now - lastRateSweep >= 5.0)
            {
                lastRateSweep = now;
                SweepRates(now);
            }
        }

        private void Handle(int length, EndPoint from)
        {
            if (length < 3) return;
            if (RelayWire.ReadU16(buffer, 0) != RelayProtocol.Magic) return;
            if (!AllowPacket(from)) return;
            switch (buffer[2])
            {
                case RelayProtocol.HostRoom: HandleHostRoom(length, from); break;
                case RelayProtocol.JoinRoom: HandleJoinRoom(length, from); break;
                case RelayProtocol.Relay: HandleRelay(length, from); break;
                case RelayProtocol.KeepAlive: HandleKeepAlive(length, from); break;
                case RelayProtocol.LeaveRoom: HandleLeaveRoom(length, from); break;
            }
        }

        private bool AllowPacket(EndPoint from)
        {
            if (MaxPacketsPerSecond <= 0) return true;
            string key = EndPointKey(from);
            double now = Now;
            RateLimit limit;
            if (!rates.TryGetValue(key, out limit))
            {
                limit = new RateLimit();
                limit.WindowStart = now;
                rates.Add(key, limit);
            }
            if (now - limit.WindowStart >= 1.0)
            {
                limit.WindowStart = now;
                limit.Count = 0;
            }
            limit.Count++;
            return limit.Count <= MaxPacketsPerSecond;
        }

        private void HandleHostRoom(int length, EndPoint from)
        {
            if (length < 7) return;
            uint nonce = RelayWire.ReadU32(buffer, 3);
            if (nonce == 0) return;
            string key = EndPointKey(from);
            Room existing;
            if (roomsByHost.TryGetValue(key, out existing))
            {
                if (existing.HostNonce == nonce)
                {
                    existing.HostSeen = Now;
                    SendRoomCreated(from, nonce, existing.Code);
                    return;
                }
                RemoveRoom(existing, "host requested a new room");
            }
            if (roomsByCode.Count >= MaxRooms)
            {
                Log("room rejected: server full");
                return;
            }
            Room room = new Room();
            room.Code = NewCode();
            room.Host = from;
            room.HostNonce = nonce;
            room.HostSeen = Now;
            room.HostId = RelayIdentityWire.Parse(buffer, 7, length);
            room.HostJoinedUtc = DateTime.UtcNow;
            roomsByCode.Add(room.Code, room);
            roomsByHost.Add(key, room);
            Log("room " + room.Code + " created host=" + EndPointText(from));
            if (Players != null) Players.Join(room.Code, "host", room.HostId, from);
            SendRoomCreated(from, nonce, room.Code);
        }

        private void HandleJoinRoom(int length, EndPoint from)
        {
            if (length < 9) return;
            string code = RelayWire.ReadCode(buffer, 3);
            Room room;
            if (!roomsByCode.TryGetValue(code, out room))
            {
                SendJoinFailed(from, "no such room");
                return;
            }
            string key = EndPointKey(from);
            if (EndPointKey(room.Host) == key)
            {
                SendJoinFailed(from, "room full");
                return;
            }
            if (room.Guest != null)
            {
                if (EndPointKey(room.Guest) == key)
                {
                    // Lost reply: repeat both rendezvous replies.
                    room.GuestSeen = Now;
                    SendJoined(from, room.Code, room.Host);
                    SendPeerJoined(room.Host, room.Code, from);
                    return;
                }
                // A guest that crashed or lost its network never sends LeaveRoom. Guests keep the server alive
                // every 5 s, so after GuestReplaceSeconds of silence the slot goes to the new joiner (same code).
                if (Now - room.GuestSeen < GuestReplaceSeconds)
                {
                    SendJoinFailed(from, "room full");
                    return;
                }
                Log("room " + room.Code + " silent guest replaced");
                GuestLeft(room, "silent guest replaced");
                SendPeerLeft(room.Host, room.Code);
            }
            room.Guest = from;
            room.GuestSeen = Now;
            room.GuestId = RelayIdentityWire.Parse(buffer, 9, length);
            room.GuestJoinedUtc = DateTime.UtcNow;
            SendJoined(from, room.Code, room.Host);
            SendPeerJoined(room.Host, room.Code, from);
            Log("room " + room.Code + " joined guest=" + EndPointText(from));
            if (Players != null) Players.Join(room.Code, "guest", room.GuestId, from);
        }

        private void HandleRelay(int length, EndPoint from)
        {
            if (length < 11) return;
            string code = RelayWire.ReadCode(buffer, 3);
            int size = RelayWire.ReadU16(buffer, 9);
            if (size > MaxPayload || length != 11 + size) return;
            Room room;
            if (!roomsByCode.TryGetValue(code, out room)) return;
            string key = EndPointKey(from);
            EndPoint target = null;
            if (EndPointKey(room.Host) == key)
            {
                room.HostSeen = Now;
                target = room.Guest;
            }
            else if (room.Guest != null && EndPointKey(room.Guest) == key)
            {
                room.GuestSeen = Now;
                target = room.Host;
            }
            else return;
            if (target == null) return;
            byte[] packet = new byte[5 + size];
            RelayWire.WriteMagicOp(packet, RelayProtocol.Relayed);
            RelayWire.WriteU16(packet, 3, (ushort)size);
            Buffer.BlockCopy(buffer, 11, packet, 5, size);
            Send(target, packet);
        }

        private void HandleKeepAlive(int length, EndPoint from)
        {
            if (length < 9) return;
            string code = RelayWire.ReadCode(buffer, 3);
            Room room;
            if (!roomsByCode.TryGetValue(code, out room)) return;
            string key = EndPointKey(from);
            if (EndPointKey(room.Host) == key) room.HostSeen = Now;
            else if (room.Guest != null && EndPointKey(room.Guest) == key) room.GuestSeen = Now;
            else return;
            byte[] ack = new byte[3];
            RelayWire.WriteMagicOp(ack, RelayProtocol.KeepAliveAck);
            Send(from, ack);
        }

        private void HandleLeaveRoom(int length, EndPoint from)
        {
            if (length != 9) return;
            Room room;
            if (!roomsByCode.TryGetValue(RelayWire.ReadCode(buffer, 3), out room)) return;
            // Only the current guest can free its slot. A delayed leave from an old
            // endpoint must not evict a replacement guest, or close the host's room.
            if (room.Guest == null || EndPointKey(room.Guest) != EndPointKey(from)) return;
            GuestLeft(room, "left");
            room.Guest = null;
            SendPeerLeft(room.Host, room.Code);
            Log("room " + room.Code + " guest left");
        }

        private void Expire(double now)
        {
            List<Room> expired = new List<Room>();
            List<Room> guestGone = new List<Room>();
            foreach (Room room in roomsByCode.Values)
            {
                if (now - room.HostSeen >= RoomTimeoutSeconds) expired.Add(room);
                else if (room.Guest != null && now - room.GuestSeen >= GuestTimeoutSeconds) guestGone.Add(room);
            }
            foreach (Room room in guestGone)
            {
                GuestLeft(room, "timed out");
                room.Guest = null;
                SendPeerLeft(room.Host, room.Code);
                Log("room " + room.Code + " guest timed out");
            }
            foreach (Room room in expired) RemoveRoom(room, "host timed out");
        }

        private void GuestLeft(Room room, string why)
        {
            if (Players == null || room.Guest == null) return;
            Players.Leave(room.Code, "guest", room.GuestId, room.Guest, room.GuestJoinedUtc, why);
        }

        private void RemoveRoom(Room room, string why)
        {
            GuestLeft(room, "room closed (" + why + ")");
            if (Players != null) Players.Leave(room.Code, "host", room.HostId, room.Host, room.HostJoinedUtc, why);
            roomsByCode.Remove(room.Code);
            string key = EndPointKey(room.Host);
            Room mapped;
            if (roomsByHost.TryGetValue(key, out mapped) && Object.ReferenceEquals(mapped, room))
                roomsByHost.Remove(key);
            Log("room " + room.Code + " closed (" + why + ")");
        }

        private string NewCode()
        {
            while (true)
            {
                char[] chars = new char[6];
                for (int i = 0; i < chars.Length; i++)
                    chars[i] = RelayProtocol.CodeAlphabet[random.Next(RelayProtocol.CodeAlphabet.Length)];
                string code = new string(chars);
                if (!roomsByCode.ContainsKey(code)) return code;
            }
        }

        private void SendRoomCreated(EndPoint to, uint nonce, string code)
        {
            byte[] packet = new byte[13];
            RelayWire.WriteMagicOp(packet, RelayProtocol.RoomCreated);
            RelayWire.WriteU32(packet, 3, nonce);
            RelayWire.WriteCode(packet, 7, code);
            Send(to, packet);
        }

        private void SendJoined(EndPoint to, string code, EndPoint host)
        {
            byte[] packet = new byte[15];
            RelayWire.WriteMagicOp(packet, RelayProtocol.Joined);
            RelayWire.WriteCode(packet, 3, code);
            RelayWire.WriteEndpoint(packet, 9, host);
            Send(to, packet);
        }

        private void SendPeerJoined(EndPoint to, string code, EndPoint guest)
        {
            byte[] packet = new byte[15];
            RelayWire.WriteMagicOp(packet, RelayProtocol.PeerJoined);
            RelayWire.WriteCode(packet, 3, code);
            RelayWire.WriteEndpoint(packet, 9, guest);
            Send(to, packet);
        }

        private void SendJoinFailed(EndPoint to, string reason)
        {
            byte[] text = Encoding.UTF8.GetBytes(reason);
            byte[] packet = new byte[4 + text.Length];
            RelayWire.WriteMagicOp(packet, RelayProtocol.JoinFailed);
            packet[3] = (byte)text.Length;
            Buffer.BlockCopy(text, 0, packet, 4, text.Length);
            Send(to, packet);
        }

        private void SendPeerLeft(EndPoint to, string code)
        {
            byte[] packet = new byte[9];
            RelayWire.WriteMagicOp(packet, RelayProtocol.PeerLeft);
            RelayWire.WriteCode(packet, 3, code);
            Send(to, packet);
        }

        private void Send(EndPoint to, byte[] packet)
        {
            try { socket.SendTo(packet, 0, packet.Length, SocketFlags.None, to); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private void SweepRates(double now)
        {
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, RateLimit> pair in rates)
                if (now - pair.Value.WindowStart >= 3.0) dead.Add(pair.Key);
            foreach (string key in dead) rates.Remove(key);
        }

        private static string EndPointKey(EndPoint endpoint)
        {
            IPEndPoint ip = endpoint as IPEndPoint;
            if (ip == null) return endpoint.ToString();
            return ip.Address.ToString() + ":" + ip.Port;
        }

        private static string EndPointText(EndPoint endpoint)
        {
            return endpoint == null ? "?" : endpoint.ToString();
        }

        private static void Log(string message)
        {
            Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + message);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            socket.Close();
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            int port = RelayServer.DefaultPort;
            if (args.Length > 0 && !Int32.TryParse(args[0], out port))
            {
                Console.Error.WriteLine("usage: RelayServer.exe [udp-port]");
                return 2;
            }
            try
            {
                using (RelayServer server = new RelayServer(port))
                {
                    Console.WriteLine("OriCoop relay listening on UDP port " + server.Port);
                    server.Players = PlayerLog.FromEnvironment(delegate(string line) { Console.WriteLine(line); });
                    while (true)
                    {
                        server.Poll();
                        Thread.Sleep(5);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("relay failed: " + ex.Message);
                return 1;
            }
        }
    }
}
