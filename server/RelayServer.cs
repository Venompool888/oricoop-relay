// Relay protocol v2 (incompatible with v1). Integers are little endian; offsets are zero-based.
// Header H = u16 magic 0x534F (bytes 4F 53, bumped from v1 4F 52), u8 op. No extra version byte.
// Packet / op          Fields after H                                      Bytes
// HostRoom / 1         nonce:u32, optional identity                         7 + identity
// JoinRoom / 2         code:ASCII[6], optional identity                     9 + identity
// RoomCreated / 3      nonce:u32, code:ASCII[6]                             13
// Joined / 4           code:ASCII[6], seat:u8, host IPv4[4], port:u16         16
// PeerJoined / 5       code:ASCII[6], seat:u8, guest IPv4[4], port:u16        16
// JoinFailed / 6       utf8Length:u8, UTF-8 reason                           4 + length
// Relay / 7            code:ASCII[6], target:u8, size:u16, payload[size]      12 + size
// Relayed / 8          source:u8, size:u16, payload[size]                    6 + size
// KeepAlive / 9        code:ASCII[6]                                        9
// PeerLeft / 10        code:ASCII[6], departed seat:u8                       10
// KeepAliveAck / 13    (empty)                                              3
// LeaveRoom / 14       code:ASCII[6]                                        9
// Host is seat 1; guests use 2..4. Guests MUST target 1; host MUST target 2..4 (no broadcast).
// Joined seat is the guest's seat; PeerJoined/PeerLeft seat identifies that guest on the host.
// Relayed always has a source byte: 2..4 to host, 1 to guest. Endpoint starts at offset 10.
// Identity stays 'I', u8 version 1, u64 SteamID, u8 name length <=32, UTF-8 name (offset 7/9).
// v1 requests get JoinFailed with v1 magic and reason "relay protocol v2 required"; no room mutation.
// Direct punch/data remain peer-to-peer; the server supplies the host/guest endpoint for EACH seat.

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
        internal const byte Version = 2;
        internal const ushort Magic = 0x534F;
        internal const ushort V1Magic = 0x524F;
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
        internal const string CodeAlphabet = "23456789";   // T-046: six-digit codes from 2-9 only, so older clients (alphabet A-Z + 2-9) can still type and accept them
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

        internal static byte[] Relay(string code, byte[] payload, int offset, int length, byte targetSeat = 1)
        {
            byte[] packet = new byte[12 + length];
            WriteMagicOp(packet, RelayProtocol.Relay);
            WriteCode(packet, 3, code);
            packet[9] = targetSeat;
            WriteU16(packet, 10, (ushort)length);
            Buffer.BlockCopy(payload, offset, packet, 12, length);
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
            internal readonly EndPoint[] Guest = new EndPoint[3];
            internal uint HostNonce;
            internal double HostSeen;
            internal readonly double[] GuestSeen = new double[3];
            // Admin log only (PlayerLog); never affects routing.
            internal PlayerIdentity HostId = PlayerIdentity.Unknown;
            internal readonly PlayerIdentity[] GuestId = new PlayerIdentity[3];
            internal DateTime HostJoinedUtc;
            internal readonly DateTime[] GuestJoinedUtc = new DateTime[3];
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
            ushort magic = RelayWire.ReadU16(buffer, 0);
            if (magic != RelayProtocol.Magic && magic != RelayProtocol.V1Magic) return;
            if (!AllowPacket(from)) return;
            if (magic == RelayProtocol.V1Magic)
            {
                SendJoinFailed(from, "relay protocol v2 required", RelayProtocol.V1Magic);
                return;
            }
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
            if (Players != null) Players.Join(room.Code, "host", 1, room.HostId, from);
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
            int slot = GuestSlot(room, key);
            if (slot >= 0)
            {
                // Lost reply: repeat both rendezvous replies without changing identity or seat.
                room.GuestSeen[slot] = Now;
                SendJoined(from, room.Code, (byte)(slot + 2), room.Host);
                SendPeerJoined(room.Host, room.Code, (byte)(slot + 2), from);
                return;
            }
            for (int i = 0; i < room.Guest.Length; i++)
                if (room.Guest[i] == null) { slot = i; break; }
            if (slot < 0)
            {
                // Preserve crash recovery when the room is full, replacing the lowest silent seat.
                for (int i = 0; i < room.Guest.Length; i++)
                    if (Now - room.GuestSeen[i] >= GuestReplaceSeconds) { slot = i; break; }
                if (slot < 0)
                {
                    SendJoinFailed(from, "room full");
                    return;
                }
                GuestLeft(room, slot, "silent guest replaced");
            }
            byte seat = (byte)(slot + 2);
            room.Guest[slot] = from;
            room.GuestSeen[slot] = Now;
            room.GuestId[slot] = RelayIdentityWire.Parse(buffer, 9, length);
            room.GuestJoinedUtc[slot] = DateTime.UtcNow;
            SendJoined(from, room.Code, seat, room.Host);
            SendPeerJoined(room.Host, room.Code, seat, from);
            Log("room " + room.Code + " joined guest=" + EndPointText(from) + " seat=" + seat);
            if (Players != null) Players.Join(room.Code, "guest", seat, room.GuestId[slot], from);
        }

        private static int GuestSlot(Room room, string key)
        {
            for (int i = 0; i < room.Guest.Length; i++)
                if (room.Guest[i] != null && EndPointKey(room.Guest[i]) == key) return i;
            return -1;
        }

        private void HandleRelay(int length, EndPoint from)
        {
            if (length < 12) return;
            string code = RelayWire.ReadCode(buffer, 3);
            byte targetSeat = buffer[9];
            int size = RelayWire.ReadU16(buffer, 10);
            if (size > MaxPayload || length != 12 + size) return;
            Room room;
            if (!roomsByCode.TryGetValue(code, out room)) return;
            string key = EndPointKey(from);
            EndPoint target;
            byte sourceSeat;
            if (EndPointKey(room.Host) == key)
            {
                if (targetSeat < 2 || targetSeat > 4) return;
                room.HostSeen = Now;
                sourceSeat = 1;
                target = room.Guest[targetSeat - 2];
            }
            else
            {
                int slot = GuestSlot(room, key);
                if (slot < 0 || targetSeat != 1) return;
                room.GuestSeen[slot] = Now;
                sourceSeat = (byte)(slot + 2);
                target = room.Host;
            }
            if (target == null) return;
            byte[] packet = new byte[6 + size];
            RelayWire.WriteMagicOp(packet, RelayProtocol.Relayed);
            packet[3] = sourceSeat;
            RelayWire.WriteU16(packet, 4, (ushort)size);
            Buffer.BlockCopy(buffer, 12, packet, 6, size);
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
            else
            {
                int slot = GuestSlot(room, key);
                if (slot < 0) return;
                room.GuestSeen[slot] = Now;
            }
            byte[] ack = new byte[3];
            RelayWire.WriteMagicOp(ack, RelayProtocol.KeepAliveAck);
            Send(from, ack);
        }

        private void HandleLeaveRoom(int length, EndPoint from)
        {
            if (length != 9) return;
            Room room;
            if (!roomsByCode.TryGetValue(RelayWire.ReadCode(buffer, 3), out room)) return;
            // A delayed leave cannot evict a replacement at a different endpoint.
            int slot = GuestSlot(room, EndPointKey(from));
            if (slot < 0) return;
            GuestLeft(room, slot, "left");
        }

        private void Expire(double now)
        {
            List<Room> expired = new List<Room>();
            foreach (Room room in roomsByCode.Values)
            {
                if (now - room.HostSeen >= RoomTimeoutSeconds) expired.Add(room);
                else
                    for (int i = 0; i < room.Guest.Length; i++)
                        if (room.Guest[i] != null && now - room.GuestSeen[i] >= GuestTimeoutSeconds)
                            GuestLeft(room, i, "timed out");
            }
            foreach (Room room in expired) RemoveRoom(room, "host timed out");
        }

        private void GuestLeft(Room room, int slot, string why)
        {
            if (room.Guest[slot] == null) return;
            byte seat = (byte)(slot + 2);
            if (Players != null) Players.Leave(room.Code, "guest", seat, room.GuestId[slot],
                room.Guest[slot], room.GuestJoinedUtc[slot], why);
            room.Guest[slot] = null;
            room.GuestId[slot] = PlayerIdentity.Unknown;
            room.GuestJoinedUtc[slot] = default(DateTime);
            room.GuestSeen[slot] = 0;
            SendPeerLeft(room.Host, room.Code, seat);
            Log("room " + room.Code + " guest seat=" + seat + " " + why);
        }

        private void RemoveRoom(Room room, string why)
        {
            for (int i = 0; i < room.Guest.Length; i++) GuestLeft(room, i, "room closed (" + why + ")");
            if (Players != null) Players.Leave(room.Code, "host", 1, room.HostId, room.Host, room.HostJoinedUtc, why);
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

        private void SendJoined(EndPoint to, string code, byte seat, EndPoint host)
        {
            byte[] packet = new byte[16];
            RelayWire.WriteMagicOp(packet, RelayProtocol.Joined);
            RelayWire.WriteCode(packet, 3, code);
            packet[9] = seat;
            RelayWire.WriteEndpoint(packet, 10, host);
            Send(to, packet);
        }

        private void SendPeerJoined(EndPoint to, string code, byte seat, EndPoint guest)
        {
            byte[] packet = new byte[16];
            RelayWire.WriteMagicOp(packet, RelayProtocol.PeerJoined);
            RelayWire.WriteCode(packet, 3, code);
            packet[9] = seat;
            RelayWire.WriteEndpoint(packet, 10, guest);
            Send(to, packet);
        }

        private void SendJoinFailed(EndPoint to, string reason, ushort magic = RelayProtocol.Magic)
        {
            byte[] text = Encoding.UTF8.GetBytes(reason);
            byte[] packet = new byte[4 + text.Length];
            RelayWire.WriteMagicOp(packet, RelayProtocol.JoinFailed);
            RelayWire.WriteU16(packet, 0, magic);
            packet[3] = (byte)text.Length;
            Buffer.BlockCopy(text, 0, packet, 4, text.Length);
            Send(to, packet);
        }

        private void SendPeerLeft(EndPoint to, string code, byte seat)
        {
            byte[] packet = new byte[10];
            RelayWire.WriteMagicOp(packet, RelayProtocol.PeerLeft);
            RelayWire.WriteCode(packet, 3, code);
            packet[9] = seat;
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
