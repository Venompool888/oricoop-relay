using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

using OriCoop.Net;

namespace OriCoop.Relay
{
    // Test-only v2 transport for the original two-peer reliability/direct/reconnect checks.
    // Kept here so server tests do not depend on the separately maintained game relay client.
    // One guest at a time; raw RelaySeatTest exercises the multi-seat wire contract.
    public sealed class RelayTestTransport : IDatagramTransport
    {
        private const ushort Magic = 0x534F;
        private const byte OpHostRoom = 1;
        private const byte OpJoinRoom = 2;
        private const byte OpRoomCreated = 3;
        private const byte OpJoined = 4;
        private const byte OpPeerJoined = 5;
        private const byte OpJoinFailed = 6;
        private const byte OpRelay = 7;
        private const byte OpRelayed = 8;
        private const byte OpKeepAlive = 9;
        private const byte OpPeerLeft = 10;
        private const byte OpKeepAliveAck = 13;
        private const byte OpLeaveRoom = 14;
        private const byte MarkerData = 1;
        private const byte MarkerPunch = 2;
        private const byte MarkerPunchAck = 3;
        private const int MaxRelayPayload = 1300;
        private const double RetrySeconds = 0.5;
        private const double KeepAliveSeconds = 5.0;
        private const double PunchIntervalSeconds = 0.1;
        private const double PunchSeconds = 2.0;
        private const double DirectTimeoutSeconds = 3.0;
        private const string CodeAlphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";   // T-046: digit codes; letter codes from older servers still accepted

        private static readonly EndPoint VirtualRemote = new IPEndPoint(IPAddress.Parse("10.255.255.1"), 1);
        private static readonly Random NonceSource = new Random();

        // Optional identity appended to HostRoom/JoinRoom for the relay operator's join log (SteamID64 and a
        // display name). The mod sets these (RelayList.Init); null/0/"" sends "Steam not running". Read once
        // per transport on the creating (main) thread. Older relays ignore the trailing bytes.
        public static Func<ulong> LocalSteamIdSource;
        public static Func<string> LocalNameSource;
        private const byte IdentityTag = 0x49;
        private const byte IdentityVersion = 1;
        private const int MaxIdentityNameBytes = 32;
        private readonly byte[] identity;

        private readonly UdpTransport udp;
        private readonly EndPoint server;
        private readonly bool hosting;
        private readonly uint nonce;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly byte[] receiveBuffer = new byte[65536];
        private readonly Queue<byte[]> incoming = new Queue<byte[]>();
        private string roomCode;
        private string error;
        private EndPoint peer;
        private byte peerSeat = 2;
        private bool ready;
        private bool direct;
        private bool forceRelay;
        private bool directBlocked;
        private bool receivedPeerPayload;
        private bool updating;
        private bool disposed;
        private double lastHostRoom;
        private double lastJoinRoom;
        private double lastKeepAlive;
        private double punchStart;
        private double lastPunch;
        private double lastDirectReceive;
        private double lastServerReceive;

        private RelayTestTransport(EndPoint server, bool hosting, string code)
        {
            if (server == null) throw new ArgumentNullException("server");
            this.server = server;
            this.hosting = hosting;
            this.roomCode = code;
            this.nonce = NewNonce();
            this.identity = BuildIdentity();
            this.udp = new UdpTransport(0);
            this.lastHostRoom = -1000000;
            this.lastJoinRoom = -1000000;
            this.lastKeepAlive = -1000000;
            this.punchStart = -1000000;
            this.lastPunch = -1000000;
            this.lastDirectReceive = -1000000;
            this.lastServerReceive = -1000000;
            Update();
        }

        public static RelayTestTransport Host(EndPoint server)
        {
            return new RelayTestTransport(server, true, null);
        }

        public static RelayTestTransport Join(EndPoint server, string code)
        {
            if (code == null) throw new ArgumentNullException("code");
            code = code.Trim().ToUpperInvariant();
            if (code.Length != 6) throw new ArgumentException("room code must be 6 characters", "code");
            return new RelayTestTransport(server, false, code);
        }

        public string RoomCode { get { return roomCode; } }
        public bool Ready { get { return ready; } }
        public bool Direct { get { return direct; } }
        public string Error { get { return error; } }
        public EndPoint RemoteEndPoint { get { return VirtualRemote; } }
        public int LocalPort { get { return udp.LocalPort; } }
        public event Action<string> Log;

        private void LogState(string message)
        {
            Action<string> log = Log;
            if (log == null) return;
            try { log("local=" + LocalPort + " " + message); }
            catch (Exception) { } // Diagnostics must not interrupt the transport.
        }

        private void LogFirstPayload(string path, int length)
        {
            if (receivedPeerPayload) return;
            receivedPeerPayload = true;
            LogState("first payload via " + path + " peer=" + peer + " bytes=" + length);
        }

        private void SetDirect(bool value, string reason)
        {
            if (direct == value) return;
            direct = value;
            LogState("direct " + (value ? "on" : "off") + " peer=" + peer + " (" + reason + ")");
        }

        // Test-only: never hole punch, always use the relay path.
        public bool ForceRelay
        {
            get { return forceRelay; }
            set
            {
                forceRelay = value;
                if (value) SetDirect(false, "forced relay");
                else if (ready && peer != null) BeginPunch();
            }
        }

        private static uint NewNonce()
        {
            lock (NonceSource) { return (uint)NonceSource.Next(1, Int32.MaxValue); }
        }

        private double Now { get { return clock.Elapsed.TotalSeconds; } }

        public void Update()
        {
            if (disposed || updating) return;
            updating = true;
            try
            {
                Drain();
                double now = Now;
                if (hosting)
                {
                    if (!ready)
                    {
                        if (now - lastHostRoom >= RetrySeconds) { SendHostRoom(); lastHostRoom = now; }
                    }
                    else if (now - lastKeepAlive >= KeepAliveSeconds) { SendKeepAlive(); lastKeepAlive = now; }
                }
                else
                {
                    if (!ready)
                    {
                        if (now - lastJoinRoom >= RetrySeconds) { SendJoinRoom(); lastJoinRoom = now; }
                    }
                    else if (now - lastKeepAlive >= KeepAliveSeconds) { SendKeepAlive(); lastKeepAlive = now; }
                }
                TryPunch(now);
                CheckDirectTimeout(now);
            }
            finally
            {
                updating = false;
            }
        }

        public void SendTo(byte[] data, int offset, int length, EndPoint to)
        {
            if (disposed) return;
            if (data == null) throw new ArgumentNullException("data");
            if (offset < 0 || length < 0 || offset > data.Length - length) throw new ArgumentOutOfRangeException("length");
            Update();
            if (!ready || roomCode == null) return;
            if (direct && !forceRelay && peer != null)
            {
                byte[] raw = new byte[1 + length];
                raw[0] = MarkerData;
                Buffer.BlockCopy(data, offset, raw, 1, length);
                SendRaw(raw, peer);
                return;
            }
            if (length > MaxRelayPayload) return;
            byte[] packet = new byte[12 + length];
            WriteMagicOp(packet, OpRelay);
            WriteCode(packet, 3, roomCode);
            packet[9] = hosting ? peerSeat : (byte)1;
            WriteU16(packet, 10, (ushort)length);
            Buffer.BlockCopy(data, offset, packet, 12, length);
            SendRaw(packet, server);
        }

        public int Receive(byte[] buffer, out EndPoint from)
        {
            from = VirtualRemote;
            if (disposed) return 0;
            if (buffer == null) throw new ArgumentNullException("buffer");
            Update();
            if (incoming.Count == 0) return 0;
            byte[] payload = incoming.Dequeue();
            int length = Math.Min(buffer.Length, payload.Length);
            Buffer.BlockCopy(payload, 0, buffer, 0, length);
            return length;
        }

        public void Dispose()
        {
            if (disposed) return;
            if (!hosting && ready && roomCode != null)
            {
                byte[] packet = new byte[9];
                WriteMagicOp(packet, OpLeaveRoom);
                WriteCode(packet, 3, roomCode);
                // Best effort over UDP; duplicates are harmless at the server.
                for (int i = 0; i < 3; i++) SendRaw(packet, server);
            }
            disposed = true;
            udp.Dispose();
        }

        private void Drain()
        {
            while (true)
            {
                EndPoint from;
                int length;
                try { length = udp.Receive(receiveBuffer, out from); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                if (length <= 0) break;
                Handle(receiveBuffer, length, from);
            }
        }

        private void Handle(byte[] data, int length, EndPoint from)
        {
            if (length >= 2 && ReadU16(data, 0) == Magic)
            {
                HandleServer(data, length);
                return;
            }
            if (!forceRelay && !directBlocked) HandleDirect(data, length, from);
        }

        private void HandleServer(byte[] data, int length)
        {
            if (length < 3) return;
            switch (data[2])
            {
                case OpRoomCreated:
                    if (!hosting || length < 13) return;
                    if (ReadU32(data, 3) != nonce) return;
                    string created = ReadCode(data, 7);
                    if (!IsValidCode(created)) return;
                    roomCode = created;
                    if (!ready) { ready = true; error = null; }
                    lastServerReceive = Now;
                    return;
                case OpJoined:
                    if (hosting || length < 16) return;
                    if (!CodeEquals(ReadCode(data, 3))) return;
                    EndPoint hostEndpoint = ReadEndpoint(data, 10);
                    if (hostEndpoint == null) return;
                    lastServerReceive = Now;
                    if (!ready) { ready = true; error = null; }
                    if (peer == null || !peer.Equals(hostEndpoint))
                    {
                        peer = hostEndpoint;
                        receivedPeerPayload = false;
                        LogState("PeerJoined host=" + peer);
                        BeginPunch();
                    }
                    return;
                case OpPeerJoined:
                    if (!hosting || length < 16) return;
                    if (!CodeEquals(ReadCode(data, 3))) return;
                    peerSeat = data[9];
                    EndPoint guestEndpoint = ReadEndpoint(data, 10);
                    if (guestEndpoint == null) return;
                    lastServerReceive = Now;
                    if (peer == null || !peer.Equals(guestEndpoint))
                    {
                        peer = guestEndpoint;
                        receivedPeerPayload = false;
                        LogState("PeerJoined guest=" + peer);
                        BeginPunch();
                    }
                    return;
                case OpJoinFailed:
                    if (hosting || length < 4 || ready) return;
                    int count = data[3];
                    if (count > length - 4) return;
                    error = Encoding.UTF8.GetString(data, 4, count);
                    lastServerReceive = Now;
                    return;
                case OpRelayed:
                    if (length < 6) return;
                    if (data[3] != (hosting ? peerSeat : (byte)1)) return;
                    int size = ReadU16(data, 4);
                    if (size != length - 6) return;
                    byte[] payload = new byte[size];
                    Buffer.BlockCopy(data, 6, payload, 0, size);
                    incoming.Enqueue(payload);
                    LogFirstPayload("relay", size);
                    lastServerReceive = Now;
                    return;
                case OpKeepAliveAck:
                    lastServerReceive = Now;
                    return;
                case OpPeerLeft:
                    if (!hosting || length != 10 || data[9] != peerSeat) return;
                    if (!CodeEquals(ReadCode(data, 3))) return;
                    if (peer != null) LogState("PeerLeft guest=" + peer);
                    SetDirect(false, "peer left");
                    peer = null;
                    receivedPeerPayload = false;
                    lastServerReceive = Now;
                    return;
            }
        }

        private void HandleDirect(byte[] data, int length, EndPoint from)
        {
            if (length < 1 || !IsPeer(from)) return;
            byte marker = data[0];
            if (marker == MarkerData)
            {
                if (length <= 1) return;
                lastDirectReceive = Now;
                SetDirect(true, "data");
                byte[] payload = new byte[length - 1];
                Buffer.BlockCopy(data, 1, payload, 0, length - 1);
                incoming.Enqueue(payload);
                LogFirstPayload("direct", length - 1);
                return;
            }
            if (marker != MarkerPunch && marker != MarkerPunchAck) return;
            if (length < 11) return;
            if (!CodeEquals(ReadCode(data, 1))) return;
            lastDirectReceive = Now;
            SetDirect(true, "punch");
            if (marker == MarkerPunch) SendPunch(MarkerPunchAck);
        }

        private void BeginPunch()
        {
            if (forceRelay) return;
            SetDirect(false, "punch start");
            if (directBlocked) LogState("blocked off peer=" + peer);
            directBlocked = false;
            LogState("punch start peer=" + peer);
            punchStart = Now;
            lastPunch = -1000000;
            lastDirectReceive = Now;
        }

        private void TryPunch(double now)
        {
            if (forceRelay || directBlocked || direct || !ready || peer == null) return;
            if (punchStart < 0 || now - punchStart > PunchSeconds) return;
            if (now - lastPunch < PunchIntervalSeconds) return;
            lastPunch = now;
            SendPunch(MarkerPunch);
        }

        private void CheckDirectTimeout(double now)
        {
            if (!direct || forceRelay) return;
            if (now - lastDirectReceive < DirectTimeoutSeconds) return;
            // Only abandon the direct path when the relay path is demonstrably alive.
            if (lastServerReceive < 0 || now - lastServerReceive > 7.0) return;
            SetDirect(false, "direct timeout");
            directBlocked = true;
            LogState("blocked on peer=" + peer);
        }

        private void SendHostRoom()
        {
            byte[] packet = new byte[7 + identity.Length];
            WriteMagicOp(packet, OpHostRoom);
            WriteU32(packet, 3, nonce);
            Buffer.BlockCopy(identity, 0, packet, 7, identity.Length);
            SendRaw(packet, server);
        }

        private void SendJoinRoom()
        {
            if (roomCode == null) return;
            byte[] packet = new byte[9 + identity.Length];
            WriteMagicOp(packet, OpJoinRoom);
            WriteCode(packet, 3, roomCode);
            Buffer.BlockCopy(identity, 0, packet, 9, identity.Length);
            SendRaw(packet, server);
        }

        // 'I', version, u64 SteamID64 (LE), u8 name length, UTF-8 name (cut on a character boundary).
        internal static byte[] BuildIdentity()
        {
            ulong steamId = 0;
            string name = null;
            try { if (LocalSteamIdSource != null) steamId = LocalSteamIdSource(); }
            catch (Exception) { steamId = 0; }
            try { if (LocalNameSource != null) name = LocalNameSource(); }
            catch (Exception) { name = null; }
            byte[] nameBytes = new byte[0];
            if (!String.IsNullOrEmpty(name))
            {
                int chars = Math.Min(name.Length, MaxIdentityNameBytes);
                while (chars > 0 && Encoding.UTF8.GetByteCount(name.Substring(0, chars)) > MaxIdentityNameBytes) chars--;
                if (chars > 0 && Char.IsHighSurrogate(name[chars - 1])) chars--;
                nameBytes = Encoding.UTF8.GetBytes(name.Substring(0, chars));
            }
            byte[] block = new byte[11 + nameBytes.Length];
            block[0] = IdentityTag;
            block[1] = IdentityVersion;
            for (int i = 0; i < 8; i++) block[2 + i] = (byte)(steamId >> (8 * i));
            block[10] = (byte)nameBytes.Length;
            Buffer.BlockCopy(nameBytes, 0, block, 11, nameBytes.Length);
            return block;
        }

        private void SendKeepAlive()
        {
            if (roomCode == null) return;
            byte[] packet = new byte[9];
            WriteMagicOp(packet, OpKeepAlive);
            WriteCode(packet, 3, roomCode);
            SendRaw(packet, server);
        }

        private void SendPunch(byte marker)
        {
            if (peer == null || roomCode == null) return;
            byte[] packet = new byte[11];
            packet[0] = marker;
            WriteCode(packet, 1, roomCode);
            WriteU32(packet, 7, nonce);
            SendRaw(packet, peer);
        }

        private void SendRaw(byte[] data, EndPoint to)
        {
            try { udp.SendTo(data, 0, data.Length, to); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private bool IsPeer(EndPoint from)
        {
            return peer != null && from != null && peer.Equals(from);
        }

        private bool CodeEquals(string code)
        {
            return roomCode != null && String.Equals(code, roomCode, StringComparison.Ordinal);
        }

        private static bool IsValidCode(string code)
        {
            if (code == null || code.Length != 6) return false;
            for (int i = 0; i < code.Length; i++)
                if (CodeAlphabet.IndexOf(code[i]) < 0) return false;
            return true;
        }

        private static ushort ReadU16(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static uint ReadU32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) |
                (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        private static void WriteU16(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteU32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
            data[offset + 2] = (byte)((value >> 16) & 0xFF);
            data[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteMagicOp(byte[] data, byte op)
        {
            data[0] = (byte)(Magic & 0xFF);
            data[1] = (byte)(Magic >> 8);
            data[2] = op;
        }

        private static void WriteCode(byte[] data, int offset, string code)
        {
            Encoding.ASCII.GetBytes(code, 0, 6, data, offset);
        }

        private static string ReadCode(byte[] data, int offset)
        {
            return Encoding.ASCII.GetString(data, offset, 6);
        }

        private static EndPoint ReadEndpoint(byte[] data, int offset)
        {
            int port = ReadU16(data, offset + 4);
            if (port == 0) return null;
            byte[] address = new byte[] { data[offset], data[offset + 1], data[offset + 2], data[offset + 3] };
            return new IPEndPoint(new IPAddress(address), port);
        }
    }
}
