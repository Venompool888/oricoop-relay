using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using OriCoop.Net;

namespace OriCoop.Relay
{
    public sealed class RelayTest
    {
        private const int TotalChecks = 13;
        private static int checks;
        private static int passed;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static RelayServer mainServer;
        private static RelayServer shortServer;

        private static double NowMs { get { return Clock.Elapsed.TotalMilliseconds; } }

        public static int Main(string[] args)
        {
            try
            {
                mainServer = new RelayServer(0);
                RunA();
                RunB();
                RunC();
                RunD();
                RunE();
                RunReconnect(false);
                RunReconnect(true);
                RunGameReconnect(false);
                RunGameReconnect(true);
                RunIdentityWire();
                RunNameParsing();
                RunPlayerLog();
                RunLogRetention();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL harness: " + ex.GetType().Name + ": " + ex.Message);
                checks++;
            }
            finally
            {
                if (mainServer != null) mainServer.Dispose();
                if (shortServer != null) shortServer.Dispose();
            }
            bool ok = checks == TotalChecks && passed == TotalChecks;
            Console.WriteLine("RESULT " + (ok ? "PASS " : "FAIL ") + passed + "/" + TotalChecks);
            return ok ? 0 : 1;
        }

        private static void Check(string name, bool ok, string detail)
        {
            checks++;
            if (ok) passed++;
            Console.WriteLine("{0} {1}: {2}", ok ? "PASS" : "FAIL", name, detail);
        }

        private static EndPoint ServerAddress(RelayServer server)
        {
            return new IPEndPoint(IPAddress.Loopback, server.Port);
        }

        private static void Tick()
        {
            if (mainServer != null) mainServer.Poll();
            if (shortServer != null) shortServer.Poll();
        }

        private static void Pump(int milliseconds, params RelayTransport[] transports)
        {
            double end = NowMs + milliseconds;
            do
            {
                Tick();
                for (int i = 0; i < transports.Length; i++)
                    if (transports[i] != null) transports[i].Update();
                Thread.Sleep(1);
            } while (NowMs < end);
        }

        private static bool Wait(int milliseconds, Func<bool> done, params RelayTransport[] transports)
        {
            double end = NowMs + milliseconds;
            while (!done() && NowMs < end) Pump(1, transports);
            return done();
        }

        private static void PumpPeers(int milliseconds, NetPeer first, NetPeer second)
        {
            double end = NowMs + milliseconds;
            do
            {
                Tick();
                if (first != null) first.Poll();
                if (second != null) second.Poll();
                Thread.Sleep(1);
            } while (NowMs < end);
        }

        private static bool WaitPeers(int milliseconds, Func<bool> done, NetPeer first, NetPeer second)
        {
            double end = NowMs + milliseconds;
            while (!done() && NowMs < end) PumpPeers(1, first, second);
            return done();
        }

        private static void Dispose(RelayTransport transport)
        {
            if (transport != null) transport.Dispose();
        }

        private static byte[] Payload(int id, int size)
        {
            byte[] bytes = new byte[size];
            uint value = (uint)id + 12345U;
            for (int i = 0; i < bytes.Length; i++)
            {
                value = value * 1664525U + 1013904223U;
                bytes[i] = (byte)(value >> 24);
            }
            return bytes;
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static int MessageSize(int index)
        {
            return 1 + (index * 17) % 2000;
        }

        private static bool InAlphabet(string code)
        {
            if (code == null || code.Length != 6) return false;
            for (int i = 0; i < code.Length; i++)
                if (RelayProtocol.CodeAlphabet.IndexOf(code[i]) < 0) return false;
            return true;
        }

        private static string DifferentCode(string code)
        {
            char first = code[0] == 'A' ? 'B' : 'A';
            return first + code.Substring(1);
        }

        private static void RunIdentityWire()
        {
            List<string> bad = new List<string>();
            try
            {
                RelayTransport.LocalSteamIdSource = delegate { return 76561198000000042UL; };
                // 3-byte UTF-8 characters plus an emoji (surrogate pair) right at the 32-byte cut.
                string longName = "Ori " + new string('测', 9) + "😀abcdefghijklmnop";
                RelayTransport.LocalNameSource = delegate { return longName; };
                byte[] block = RelayTransport.BuildIdentity();
                PlayerIdentity id = RelayIdentityWire.Parse(block, 0, block.Length);
                int nameBytes = System.Text.Encoding.UTF8.GetByteCount(id.ClientName);
                if (!id.Known || id.SteamId != 76561198000000042UL) bad.Add("roundtrip id " + id.SteamId);
                if (nameBytes > 32 || nameBytes < 26 || !longName.StartsWith(id.ClientName, StringComparison.Ordinal) ||
                    id.ClientName.IndexOf('�') >= 0)
                    bad.Add("name cut '" + id.ClientName + "' " + nameBytes + "B");

                // Embedded in a JoinRoom packet at offset 9, with trailing future fields.
                byte[] packet = new byte[9 + block.Length + 4];
                Buffer.BlockCopy(block, 0, packet, 9, block.Length);
                if (RelayIdentityWire.Parse(packet, 9, packet.Length).SteamId != 76561198000000042UL) bad.Add("offset/trailing");
                if (RelayIdentityWire.Parse(packet, 9, 9 + block.Length - 1).Known) bad.Add("truncated name accepted");
                if (RelayIdentityWire.Parse(new byte[9], 9, 9).Known) bad.Add("old client (no block) known");
                block[0] = 0x58;
                if (RelayIdentityWire.Parse(block, 0, block.Length).Known) bad.Add("wrong tag accepted");

                RelayTransport.LocalSteamIdSource = delegate { throw new InvalidOperationException("steam off"); };
                RelayTransport.LocalNameSource = null;
                byte[] off = RelayTransport.BuildIdentity();
                PlayerIdentity offId = RelayIdentityWire.Parse(off, 0, off.Length);
                if (!offId.Known || offId.SteamId != 0 || offId.ClientName != "") bad.Add("steam off -> " + offId.SteamId);
            }
            catch (Exception ex) { bad.Add(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                RelayTransport.LocalSteamIdSource = null;
                RelayTransport.LocalNameSource = null;
            }
            Check("j identity wire", bad.Count == 0, bad.Count == 0 ? "roundtrip, utf-8 cut, old/truncated/forged blocks" : String.Join("; ", bad.ToArray()));
        }

        private static void RunNameParsing()
        {
            List<string> bad = new List<string>();
            string json = "{\"response\":{\"players\":[{\"steamid\":\"1\",\"personaname\":\"A\\\"b\\\\c \\u00e9\\/\"}]}}";
            Expect(bad, "json", "A\"b\\c é/",SteamNameResolver.ParseSummariesJson(json));
            Expect(bad, "json empty", null, SteamNameResolver.ParseSummariesJson("{\"response\":{\"players\":[]}}"));
            Expect(bad, "json null", null, SteamNameResolver.ParseSummariesJson(null));
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><profile><steamID64>1</steamID64>" +
                "<steamID><![CDATA[Ann & <Bob>]]></steamID><onlineState>offline</onlineState></profile>";
            Expect(bad, "xml", "Ann & <Bob>", SteamNameResolver.ParseProfileXml(xml));
            Expect(bad, "xml error", null, SteamNameResolver.ParseProfileXml(
                "<?xml version=\"1.0\"?><response><error><![CDATA[The specified profile could not be found.]]></error></response>"));
            Expect(bad, "xml dtd", null, SteamNameResolver.ParseProfileXml(
                "<?xml version=\"1.0\"?><!DOCTYPE p [<!ENTITY x \"boom\">]><profile><steamID>&x;</steamID></profile>"));
            Expect(bad, "html", null, SteamNameResolver.ParseProfileXml("<html><body>rate limited</body></html>"));

            PlayerIdentity id = new PlayerIdentity();
            id.Known = true; id.SteamId = 76561198000000001UL; id.ClientName = "Cli\"ent\nX";
            DateTime t = new DateTime(2026, 11, 16, 3, 21, 7, DateTimeKind.Utc);
            EndPoint ep = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51234);
            Expect(bad, "join line",
                "PLAYER JOIN 2026-11-16T03:21:07Z room=K7QX2M role=guest steamid=76561198000000001 ip=203.0.113.7:51234 " +
                "name=\"Ex ?ample?\" client_name=\"Cli?ent?X\" profile=https://steamcommunity.com/profiles/76561198000000001",
                PlayerLog.FormatJoin(t, "K7QX2M", "guest", id, ep, "Ex \"ample\t"));
            Expect(bad, "leave line",
                "PLAYER LEAVE 2026-11-16T03:21:07Z room=K7QX2M role=host steamid=unknown ip=203.0.113.7:51234 " +
                "name=- client_name=- profile=- duration=26:03:04 reason=\"host timed out\"",
                PlayerLog.FormatLeave(t, "K7QX2M", "host", PlayerIdentity.Unknown, ep, null,
                    new TimeSpan(1, 2, 3, 4), "host timed out"));
            PlayerIdentity off = new PlayerIdentity(); off.Known = true;
            string offLine = PlayerLog.FormatJoin(t, "K7QX2M", "host", off, ep, null);
            if (offLine.IndexOf("steamid=0 ") < 0 || offLine.IndexOf("profile=-") < 0) bad.Add("steam off line: " + offLine);
            if (PlayerLog.Quote(new string('x', 100)).Length != 64 + 2 + 3) bad.Add("quote length");
            Check("k name parsing", bad.Count == 0, bad.Count == 0 ? "json, xml, error/dtd/html, line format" : String.Join("; ", bad.ToArray()));
        }

        private static void Expect(List<string> bad, string what, string expected, string actual)
        {
            if (expected != actual) bad.Add(what + ": expected <" + expected + "> got <" + actual + ">");
        }

        private static void RunPlayerLog()
        {
            List<string> lines = new List<string>();
            List<string> urls = new List<string>();
            List<string> bad = new List<string>();
            RelayTransport host = null, guest = null;
            Socket old = null;
            try
            {
                SteamNameResolver resolver = new SteamNameResolver("TESTKEY123");
                resolver.Queue = delegate(WaitCallback work) { work(null); };
                resolver.Fetch = delegate(string url)
                {
                    lock (urls) urls.Add(url);
                    if (url.IndexOf("api.steampowered.com") >= 0)
                        return url.IndexOf("76561198000000001") >= 0
                            ? "{\"response\":{\"players\":[{\"personaname\":\"HostSteam\"}]}}"
                            : "{\"response\":{\"players\":[]}}";
                    return "<profile><steamID><![CDATA[GuestXml]]></steamID></profile>";
                };
                mainServer.Players = new PlayerLog(delegate(string line) { lock (lines) lines.Add(line); }, null, 30, resolver);
                EndPoint address = ServerAddress(mainServer);

                RelayTransport.LocalSteamIdSource = delegate { return 76561198000000001UL; };
                RelayTransport.LocalNameSource = delegate { return "HostClient"; };
                host = RelayTransport.Host(address);
                if (!Wait(3000, delegate { return host.Ready; }, host)) bad.Add("host not ready");
                RelayTransport.LocalSteamIdSource = delegate { return 76561198000000002UL; };
                RelayTransport.LocalNameSource = delegate { return "GuestClient"; };
                guest = RelayTransport.Join(address, host.RoomCode);
                if (!Wait(3000, delegate { return guest.Ready; }, host, guest)) bad.Add("guest not ready");
                Pump(200, host, guest);
                int guestPort = guest.LocalPort;
                guest.Dispose();
                guest = null;
                Pump(200, host);

                // An old client: a plain 9-byte JoinRoom with no identity block.
                old = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                old.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                old.SendTo(RelayWire.JoinRoom(host.RoomCode), address);
                Pump(300, host);

                string[] snapshot;
                lock (lines) snapshot = lines.ToArray();
                string all = String.Join("\n", snapshot);
                string code = host.RoomCode;
                Want(bad, all, "PLAYER JOIN ", " room=" + code + " role=host steamid=76561198000000001 ip=127.0.0.1:" + host.LocalPort +
                    " name=\"HostSteam\" client_name=\"HostClient\" profile=https://steamcommunity.com/profiles/76561198000000001");
                Want(bad, all, "PLAYER JOIN ", " role=guest steamid=76561198000000002 ip=127.0.0.1:" + guestPort +
                    " name=\"GuestXml\" client_name=\"GuestClient\" profile=https://steamcommunity.com/profiles/76561198000000002");
                Want(bad, all, "PLAYER LEAVE ", " role=guest steamid=76561198000000002 ip=127.0.0.1:" + guestPort +
                    " name=\"GuestXml\" client_name=\"GuestClient\" profile=https://steamcommunity.com/profiles/76561198000000002 duration=00:00:0");
                Want(bad, all, "PLAYER LEAVE ", "reason=\"left\"");
                Want(bad, all, "PLAYER JOIN ", " role=guest steamid=unknown ip=127.0.0.1:" + ((IPEndPoint)old.LocalEndPoint).Port +
                    " name=- client_name=- profile=-");
                if (all.IndexOf("TESTKEY123") >= 0) bad.Add("api key leaked into log");
                int guestApi = 0, guestXml = 0;
                lock (urls)
                    foreach (string u in urls)
                    {
                        if (u.IndexOf("76561198000000002") < 0) continue;
                        if (u.IndexOf("api.steampowered.com") >= 0) guestApi++;
                        else if (u.IndexOf("?xml=1") >= 0) guestXml++;
                    }
                if (guestApi != 1 || guestXml != 1) bad.Add("guest lookups api=" + guestApi + " xml=" + guestXml + " (cache must stop repeats)");
                if (bad.Count > 0) bad.Add("log:\n" + all);
            }
            catch (Exception ex) { bad.Add(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                RelayTransport.LocalSteamIdSource = null;
                RelayTransport.LocalNameSource = null;
                if (old != null) old.Close();
                Dispose(guest); Dispose(host);
                mainServer.Players = null;
            }
            Check("l player log", bad.Count == 0, bad.Count == 0 ? "host/guest join with id+ip+name+profile, leave+duration, old client unknown, key not logged" : String.Join("; ", bad.ToArray()));
        }

        private static void Want(List<string> bad, string all, string prefix, string fragment)
        {
            foreach (string line in all.Split('\n'))
                if (line.StartsWith(prefix) && line.IndexOf(fragment) >= 0) return;
            bad.Add("missing " + prefix.Trim() + " line with: " + fragment);
        }

        private static void RunLogRetention()
        {
            List<string> bad = new List<string>();
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oricoop-relay-logtest-" + Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                DateTime now = DateTime.UtcNow;
                string oldFile = System.IO.Path.Combine(dir, PlayerLog.FileNameFor(now.AddDays(-31)));
                string keepFile = System.IO.Path.Combine(dir, PlayerLog.FileNameFor(now.AddDays(-30)));
                string other = System.IO.Path.Combine(dir, "players-notes.log");
                System.IO.File.WriteAllText(oldFile, "old\n");
                System.IO.File.WriteAllText(keepFile, "keep\n");
                System.IO.File.WriteAllText(other, "other\n");
                List<string> console = new List<string>();
                PlayerLog log = new PlayerLog(delegate(string line) { console.Add(line); }, dir, 30, null);
                if (System.IO.File.Exists(oldFile)) bad.Add("31-day-old file kept");
                if (!System.IO.File.Exists(keepFile)) bad.Add("30-day-old file deleted");
                if (!System.IO.File.Exists(other)) bad.Add("unrelated file deleted");
                PlayerIdentity id = new PlayerIdentity(); id.Known = true; id.SteamId = 76561198000000009UL;
                log.Join("ABCDEF", "host", id, new IPEndPoint(IPAddress.Parse("198.51.100.4"), 4000));
                string today = System.IO.Path.Combine(dir, PlayerLog.FileNameFor(DateTime.UtcNow));
                string text = System.IO.File.Exists(today) ? System.IO.File.ReadAllText(today) : "";
                if (text.IndexOf("steamid=76561198000000009 ip=198.51.100.4:4000") < 0) bad.Add("today's file: '" + text + "'");
                if (console.Count != 1) bad.Add("console lines " + console.Count);
                // The day rolls over: the next write prunes with the new date.
                log.UtcNow = delegate { return DateTime.UtcNow.AddDays(31); };
                log.Join("ABCDEF", "guest", id, null);
                if (System.IO.File.Exists(keepFile)) bad.Add("rollover did not prune");
                if (!System.IO.File.Exists(System.IO.Path.Combine(dir, PlayerLog.FileNameFor(DateTime.UtcNow.AddDays(31))))) bad.Add("rollover file missing");
            }
            catch (Exception ex) { bad.Add(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch (Exception) { }
            }
            Check("m log retention", bad.Count == 0, bad.Count == 0 ? "daily files, 30-day prune at start and on day change" : String.Join("; ", bad.ToArray()));
        }

        private static void RunA()
        {
            bool ok = false;
            string detail = "";
            RelayTransport host = null, guest = null, second = null, wrong = null;
            try
            {
                EndPoint address = ServerAddress(mainServer);
                host = RelayTransport.Host(address);
                bool created = Wait(3000, delegate { return host.Ready; }, host);
                string code = host.RoomCode;
                bool shaped = InAlphabet(code);
                if (created && code != null)
                {
                    guest = RelayTransport.Join(address, code);
                    bool joined = Wait(3000, delegate { return guest.Ready; }, host, guest);
                    second = RelayTransport.Join(address, code);
                    bool full = Wait(2000, delegate { return second.Error != null; }, host, guest, second);
                    wrong = RelayTransport.Join(address, DifferentCode(code));
                    bool missing = Wait(2000, delegate { return wrong.Error != null; }, host, guest, second, wrong);
                    ok = shaped && joined && full && missing &&
                        second.Error == "room full" && wrong.Error == "no such room";
                    detail = String.Format("code={0} shaped={1} joined={2} second='{3}' wrong='{4}'",
                        code, shaped, joined, second.Error, wrong.Error);
                }
                else detail = "host did not receive a 6-char room code";
            }
            finally
            {
                Dispose(wrong); Dispose(second); Dispose(guest); Dispose(host);
            }
            Check("a rooms", ok, detail);
        }

        private static void RunB()
        {
            bool ok = false;
            string detail = "";
            RelayTransport hostTransport = null, guestTransport = null;
            NetPeer host = null, guest = null;
            int expected = 300;
            try
            {
                EndPoint address = ServerAddress(mainServer);
                hostTransport = RelayTransport.Host(address);
                hostTransport.ForceRelay = true;
                bool hostReady = Wait(3000, delegate { return hostTransport.Ready; }, hostTransport);
                if (!hostReady || hostTransport.RoomCode == null)
                {
                    detail = "host did not receive a room code";
                    Check("b relay reliable", false, detail);
                    return;
                }
                guestTransport = RelayTransport.Join(address, hostTransport.RoomCode);
                guestTransport.ForceRelay = true;
                bool ready = Wait(3000, delegate { return hostTransport.Ready && guestTransport.Ready; }, hostTransport, guestTransport);

                host = new NetPeer(hostTransport);
                guest = new NetPeer(guestTransport);
                host.Sim.LossPercent = 5f;
                guest.Sim.LossPercent = 5f;
                host.Listen();
                guest.Connect(guestTransport.RemoteEndPoint);
                bool connected = WaitPeers(10000, delegate
                {
                    return host.State == PeerState.Connected && guest.State == PeerState.Connected;
                }, host, guest);

                for (int i = 0; i < expected; i++)
                {
                    byte[] message = Payload(i, MessageSize(i));
                    host.SendReliable(message, 0, message.Length);
                }
                List<byte[]> got = new List<byte[]>();
                bool valid = true;
                double start = NowMs;
                bool delivered = WaitPeers(25000, delegate
                {
                    byte[] data; bool reliable;
                    while (guest.TryReceive(out data, out reliable))
                    {
                        if (!reliable) valid = false;
                        got.Add(data);
                    }
                    return got.Count >= expected;
                }, host, guest);
                WaitPeers(5000, delegate { return host.Stats.ReliablePending == 0; }, host, guest);
                if (got.Count != expected) valid = false;
                for (int i = 0; i < Math.Min(got.Count, expected); i++)
                    if (!Equal(got[i], Payload(i, MessageSize(i)))) valid = false;
                ok = hostReady && ready && connected && delivered && valid &&
                    got.Count == expected && host.Stats.ReliablePending == 0;
                detail = String.Format("connected={0} received={1}/{2} exact={3} pending={4} loss={5:F1}% elapsed={6:F0}ms",
                    connected, got.Count, expected, valid, host.Stats.ReliablePending, host.Stats.LossPercent, NowMs - start);
            }
            finally
            {
                Dispose(hostTransport); Dispose(guestTransport);
            }
            Check("b relay reliable", ok, detail);
        }

        private static void RunC()
        {
            bool ok = false;
            string detail = "";
            RelayTransport hostTransport = null, guestTransport = null;
            NetPeer host = null, guest = null;
            int expected = 300;
            try
            {
                EndPoint address = ServerAddress(mainServer);
                hostTransport = RelayTransport.Host(address);
                bool hostReady = Wait(3000, delegate { return hostTransport.Ready; }, hostTransport);
                if (!hostReady || hostTransport.RoomCode == null)
                {
                    detail = "host did not receive a room code";
                    Check("c direct punch", false, detail);
                    return;
                }
                guestTransport = RelayTransport.Join(address, hostTransport.RoomCode);
                bool ready = Wait(3000, delegate { return hostTransport.Ready && guestTransport.Ready; }, hostTransport, guestTransport);
                double punchStart = NowMs;
                bool direct = Wait(2500, delegate { return hostTransport.Direct && guestTransport.Direct; }, hostTransport, guestTransport);
                double directMs = NowMs - punchStart;
                bool withinTwo = direct && directMs <= 2000.0;

                host = new NetPeer(hostTransport);
                guest = new NetPeer(guestTransport);
                host.Listen();
                guest.Connect(guestTransport.RemoteEndPoint);
                bool connected = WaitPeers(10000, delegate
                {
                    return host.State == PeerState.Connected && guest.State == PeerState.Connected;
                }, host, guest);

                for (int i = 0; i < expected; i++)
                {
                    byte[] message = Payload(i, MessageSize(i));
                    guest.SendReliable(message, 0, message.Length);
                }
                List<byte[]> got = new List<byte[]>();
                bool valid = true;
                double start = NowMs;
                bool delivered = WaitPeers(20000, delegate
                {
                    byte[] data; bool reliable;
                    while (host.TryReceive(out data, out reliable))
                    {
                        if (!reliable) valid = false;
                        got.Add(data);
                    }
                    return got.Count >= expected;
                }, host, guest);
                if (got.Count != expected) valid = false;
                for (int i = 0; i < Math.Min(got.Count, expected); i++)
                    if (!Equal(got[i], Payload(i, MessageSize(i)))) valid = false;
                bool directAfter = hostTransport.Direct && guestTransport.Direct;
                ok = hostReady && ready && withinTwo && connected && delivered && valid &&
                    got.Count == expected && directAfter;
                detail = String.Format("direct={0}/{1} in {2:F0}ms connected={3} received={4}/{5} exact={6} directAfter={7} elapsed={8:F0}ms",
                    hostTransport.Direct, guestTransport.Direct, directMs, connected, got.Count, expected, valid, directAfter, NowMs - start);
            }
            finally
            {
                Dispose(hostTransport); Dispose(guestTransport);
            }
            Check("c direct punch", ok, detail);
        }

        private static void RunD()
        {
            bool ok = false;
            string detail = "";
            RelayTransport host = null, late = null;
            try
            {
                shortServer = new RelayServer(0);
                shortServer.RoomTimeoutSeconds = 2.0;
                EndPoint address = ServerAddress(shortServer);
                host = RelayTransport.Host(address);
                bool created = Wait(3000, delegate { return host.Ready; }, host);
                string code = host.RoomCode;
                double start = NowMs;
                // Deliberately stop touching the host transport: its room must expire.
                while (NowMs - start < 2600) { shortServer.Poll(); Thread.Sleep(20); }
                bool removed = shortServer.RoomCount == 0;
                bool failed = false;
                if (code != null)
                {
                    late = RelayTransport.Join(address, code);
                    failed = Wait(2000, delegate { return late.Error != null; }, late);
                }
                bool lateOk = late != null && !late.Ready && late.Error == "no such room";
                ok = created && removed && failed && lateOk;
                detail = String.Format("code={0} removed={1} rooms={2} lateError='{3}'",
                    code, removed, shortServer.RoomCount, late == null ? "?" : late.Error);
            }
            finally
            {
                Dispose(late);
                Dispose(host);
                if (shortServer != null) { shortServer.Dispose(); shortServer = null; }
            }
            Check("d room timeout", ok, detail);
        }

        private static void RunE()
        {
            bool ok = false;
            string detail = "";
            UdpTransport hostSocket = null, guestSocket = null;
            int forwarded = 0, sent = 0;
            try
            {
                EndPoint address = ServerAddress(mainServer);
                hostSocket = new UdpTransport(0);
                guestSocket = new UdpTransport(0);
                byte[] buffer = new byte[4096];
                EndPoint from;
                byte[] hostRoom = RelayWire.HostRoom(0x12345678u);
                hostSocket.SendTo(hostRoom, 0, hostRoom.Length, address);
                string code = null;
                double end = NowMs + 2000;
                while (code == null && NowMs < end)
                {
                    mainServer.Poll();
                    int length;
                    while ((length = hostSocket.Receive(buffer, out from)) > 0)
                    {
                        if (length >= 13 && RelayWire.ReadU16(buffer, 0) == RelayProtocol.Magic &&
                            buffer[2] == RelayProtocol.RoomCreated)
                            code = RelayWire.ReadCode(buffer, 7);
                    }
                    if (code == null) Thread.Sleep(1);
                }
                bool created = code != null;
                bool joined = false;
                if (created)
                {
                    byte[] join = RelayWire.JoinRoom(code);
                    guestSocket.SendTo(join, 0, join.Length, address);
                    end = NowMs + 2000;
                    while (!joined && NowMs < end)
                    {
                        mainServer.Poll();
                        int length;
                        while ((length = hostSocket.Receive(buffer, out from)) > 0)
                            if (length >= 15 && RelayWire.ReadU16(buffer, 0) == RelayProtocol.Magic &&
                                buffer[2] == RelayProtocol.PeerJoined)
                                joined = true;
                        if (!joined) Thread.Sleep(1);
                    }
                    byte[] payload = new byte[100];
                    for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + 7);
                    byte[] relay = RelayWire.Relay(code, payload, 0, payload.Length);
                    for (int i = 0; i < 1000; i++)
                    {
                        try { guestSocket.SendTo(relay, 0, relay.Length, address); sent++; }
                        catch (SocketException) { break; }
                    }
                    end = NowMs + 800;
                    while (NowMs < end)
                    {
                        mainServer.Poll();
                        int length;
                        while ((length = hostSocket.Receive(buffer, out from)) > 0)
                            if (length >= 5 && RelayWire.ReadU16(buffer, 0) == RelayProtocol.Magic &&
                                buffer[2] == RelayProtocol.Relayed)
                                forwarded++;
                        Thread.Sleep(0);
                    }
                }
                ok = created && joined && sent == 1000 && forwarded > 0 && forwarded <= 250;
                detail = String.Format("created={0} joined={1} sent={2} forwarded={3}", created, joined, sent, forwarded);
            }
            finally
            {
                if (hostSocket != null) hostSocket.Dispose();
                if (guestSocket != null) guestSocket.Dispose();
            }
            Check("e rate limit", ok, detail);
        }

        private static void RunGameReconnect(bool forceRelay)
        {
            string name = forceRelay ? "i game timing forced relay" : "h game timing punched";
            RelayTransport hostTransport = null, guestTransport = null;
            try
            {
                EndPoint address = ServerAddress(mainServer);
                hostTransport = RelayTransport.Host(address);
                hostTransport.ForceRelay = forceRelay;
                NetPeer host = new NetPeer(hostTransport);
                host.Listen();
                if (!Wait(3000, delegate { return hostTransport.Ready; }, hostTransport))
                {
                    Check(name, false, "host room not ready");
                    return;
                }
                string code = hostTransport.RoomCode;
                PumpPeers(3000, host, null);
                guestTransport = RelayTransport.Join(address, code);
                guestTransport.ForceRelay = forceRelay;
                NetPeer guest = new NetPeer(guestTransport);
                bool ready = Wait(3000, delegate { return guestTransport.Ready; }, hostTransport, guestTransport);
                bool path = forceRelay || Wait(2500, delegate
                {
                    return hostTransport.Direct && guestTransport.Direct;
                }, hostTransport, guestTransport);
                if (ready) guest.Connect(guestTransport.RemoteEndPoint);
                bool connected = ready && WaitPeers(10000, delegate
                {
                    return host.State == PeerState.Connected && guest.State == PeerState.Connected;
                }, host, guest);
                int initialHost = 0, initialGuest = 0;
                if (!path || !connected || !ExchangeReliable(host, guest, 10, out initialHost, out initialGuest))
                {
                    Check(name, false, "initial path/messages failed");
                    return;
                }

                bool hostTimedOut = false, guestTimedOut = false, relisten = false;
                double retryAt = Double.MaxValue, firstRejoin = -1;
                int rejoins = 0, rejected = 0;
                host.Disconnected += delegate(string reason)
                {
                    hostTimedOut = reason == "timed out";
                    relisten = true;
                };
                Action<string> guestDisconnected = delegate(string reason)
                {
                    if (reason == "timed out") guestTimedOut = true;
                    if (reason == "full") rejected++;
                    retryAt = NowMs + 3000;
                };
                guest.Disconnected += guestDisconnected;
                NetPeer lossPeer = host;
                lossPeer.Sim.LossPercent = 100f;
                double start = NowMs;
                bool pendingConnect = false;
                while (NowMs - start < 25000 && (firstRejoin < 0 || NowMs - firstRejoin < 10000))
                {
                    Tick();
                    if (NowMs - start >= 7000) lossPeer.Sim.LossPercent = 0f;
                    if (relisten)
                    {
                        relisten = false;
                        host = new NetPeer(hostTransport);
                        host.Listen();
                    }
                    hostTransport.Update();
                    if (guestTransport != null)
                    {
                        // NetPlay continues pumping the disconnected transport during
                        // its retry delay, then Close/Open disposes and joins in one frame.
                        guestTransport.Update();
                        if (!String.IsNullOrEmpty(guestTransport.Error))
                        {
                            guestTransport.Dispose(); guestTransport = null;
                            guest = null; pendingConnect = false;
                            retryAt = NowMs + 5000;
                        }
                    }
                    if (NowMs >= retryAt && (guest == null || guest.State == PeerState.Disconnected))
                    {
                        Dispose(guestTransport);
                        guestTransport = RelayTransport.Join(address, code);
                        guestTransport.ForceRelay = forceRelay;
                        guest = new NetPeer(guestTransport);
                        guest.Disconnected += guestDisconnected;
                        pendingConnect = true;
                        retryAt = Double.MaxValue;
                        if (firstRejoin < 0) firstRejoin = NowMs;
                        rejoins++;
                    }
                    if (pendingConnect && guestTransport != null && guestTransport.Ready)
                    {
                        pendingConnect = false;
                        guest.Connect(guestTransport.RemoteEndPoint);
                    }
                    host.Poll();
                    if (guest != null && guestTransport.Ready) guest.Poll();
                    if (hostTimedOut && guestTimedOut && host.State == PeerState.Connected &&
                        guest != null && guest.State == PeerState.Connected) break;
                    Thread.Sleep(1);
                }
                double reconnectMs = firstRejoin < 0 ? Double.MaxValue : NowMs - firstRejoin;
                bool reconnected = hostTimedOut && guestTimedOut && host.State == PeerState.Connected &&
                    guest != null && guest.State == PeerState.Connected;
                int atHost = 0, atGuest = 0;
                bool delivered = reconnected && ExchangeReliable(host, guest, 100, out atHost, out atGuest);
                bool finalPath = guestTransport != null && (forceRelay ?
                    !hostTransport.Direct && !guestTransport.Direct : hostTransport.Direct && guestTransport.Direct);
                Check(name, reconnected && reconnectMs < 10000 && delivered && finalPath && hostTransport.RoomCode == code,
                    String.Format("timeouts={0}/{1} rejoins={2} fullRejections={3} reconnected={4} in {5:F0}ms " +
                        "received={6}/100,{7}/100 direct={8}/{9}", hostTimedOut, guestTimedOut, rejoins,
                        rejected, reconnected, reconnectMs, atHost, atGuest, hostTransport.Direct,
                        guestTransport != null && guestTransport.Direct));
            }
            finally
            {
                Dispose(guestTransport); Dispose(hostTransport);
            }
        }

        private static bool ExchangeReliable(NetPeer host, NetPeer guest, int expected,
            out int atHost, out int atGuest)
        {
            for (int i = 0; i < expected; i++)
            {
                byte[] toHost = Payload(1000 + i, MessageSize(i));
                byte[] toGuest = Payload(2000 + i, MessageSize(i));
                guest.SendReliable(toHost, 0, toHost.Length);
                host.SendReliable(toGuest, 0, toGuest.Length);
            }
            int hostCount = 0, guestCount = 0;
            bool valid = true;
            bool delivered = WaitPeers(10000, delegate
            {
                byte[] data; bool reliable;
                while (host.TryReceive(out data, out reliable))
                {
                    if (!reliable || !Equal(data, Payload(1000 + hostCount, MessageSize(hostCount)))) valid = false;
                    hostCount++;
                }
                while (guest.TryReceive(out data, out reliable))
                {
                    if (!reliable || !Equal(data, Payload(2000 + guestCount, MessageSize(guestCount)))) valid = false;
                    guestCount++;
                }
                return hostCount >= expected && guestCount >= expected &&
                    host.Stats.ReliablePending == 0 && guest.Stats.ReliablePending == 0;
            }, host, guest);
            atHost = hostCount; atGuest = guestCount;
            return delivered && valid && hostCount == expected && guestCount == expected;
        }

        private static void RunReconnect(bool forceRelay)
        {
            string name = forceRelay ? "g reconnect forced relay" : "f reconnect punched";
            RelayTransport hostTransport = null, guestTransport = null;
            UdpTransport oldGuestSocket = null;
            bool ok = false;
            string detail = "initial connection failed";
            try
            {
                EndPoint address = ServerAddress(mainServer);
                hostTransport = RelayTransport.Host(address);
                hostTransport.ForceRelay = forceRelay;
                NetPeer host = new NetPeer(hostTransport);
                host.Listen();
                if (!Wait(3000, delegate { return hostTransport.Ready; }, hostTransport))
                {
                    Check(name, false, "host room not ready");
                    return;
                }
                string code = hostTransport.RoomCode;
                // Match the game's delayed first join, with transport keepalives running.
                PumpPeers(3000, host, null);
                guestTransport = RelayTransport.Join(address, code);
                guestTransport.ForceRelay = forceRelay;
                NetPeer guest = new NetPeer(guestTransport);
                bool ready = Wait(3000, delegate { return guestTransport.Ready; }, hostTransport, guestTransport);
                bool path = forceRelay || Wait(2500, delegate
                {
                    return hostTransport.Direct && guestTransport.Direct;
                }, hostTransport, guestTransport);
                if (ready) guest.Connect(guestTransport.RemoteEndPoint);
                bool connected = ready && WaitPeers(10000, delegate
                {
                    return host.State == PeerState.Connected && guest.State == PeerState.Connected;
                }, host, guest);
                int initialHost = 0, initialGuest = 0;
                bool exchanged = connected && ExchangeReliable(host, guest, 10, out initialHost, out initialGuest);
                if (!path || !exchanged)
                {
                    Check(name, false, "initial path/messages failed");
                    return;
                }

                // Drop NetPeer packets only. Both relay transports keep updating.
                host.Sim.LossPercent = 100f;
                PumpPeers(7000, host, guest);
                host.Sim.LossPercent = 0f;
                bool timedOut = WaitPeers(6000, delegate
                {
                    return host.State == PeerState.Disconnected && guest.State == PeerState.Disconnected;
                }, host, guest);
                string reasons = host.DisconnectReason + "/" + guest.DisconnectReason;

                // NetPlay reuses the host's room/transport with a fresh listening peer,
                // but disposes the guest transport and joins again after three seconds.
                host = new NetPeer(hostTransport);
                host.Listen();
                int oldGuestPort = guestTransport.LocalPort;
                guestTransport.Dispose();
                guestTransport = null;
                // Reserve the retired endpoint so the replacement is guaranteed to
                // have a different port, and exercise delayed duplicate leaves below.
                oldGuestSocket = new UdpTransport(oldGuestPort);
                PumpPeers(3000, host, null);
                double rejoinStart = NowMs;
                guestTransport = RelayTransport.Join(address, code);
                guestTransport.ForceRelay = forceRelay;
                guest = new NetPeer(guestTransport);
                bool connectSent = false;
                while (NowMs - rejoinStart < 10000)
                {
                    Tick();
                    hostTransport.Update(); guestTransport.Update();
                    if (!connectSent && guestTransport.Ready)
                    {
                        guest.Connect(guestTransport.RemoteEndPoint);
                        connectSent = true;
                    }
                    host.Poll(); guest.Poll();
                    if (host.State == PeerState.Connected && guest.State == PeerState.Connected) break;
                    Thread.Sleep(1);
                }
                double reconnectMs = NowMs - rejoinStart;
                bool reconnected = host.State == PeerState.Connected && guest.State == PeerState.Connected;
                byte[] staleLeave = new byte[9];
                RelayWire.WriteMagicOp(staleLeave, RelayProtocol.LeaveRoom);
                RelayWire.WriteCode(staleLeave, 3, code);
                oldGuestSocket.SendTo(staleLeave, 0, staleLeave.Length, address);
                int atHost = 0, atGuest = 0;
                bool delivered = reconnected && ExchangeReliable(host, guest, 100, out atHost, out atGuest);
                bool finalPath = forceRelay ? !hostTransport.Direct && !guestTransport.Direct :
                    hostTransport.Direct && guestTransport.Direct;
                ok = timedOut && reasons == "timed out/timed out" && reconnected && reconnectMs < 10000 &&
                    delivered && finalPath && hostTransport.RoomCode == code && oldGuestPort != guestTransport.LocalPort;
                detail = String.Format("timeouts={0} ({1}) reconnected={2} in {3:F0}ms received={4}/100,{5}/100 " +
                    "direct={6}/{7} ports={8}->{9} guestError='{10}' states={11}/{12}",
                    timedOut, reasons, reconnected, reconnectMs, atHost, atGuest,
                    hostTransport.Direct, guestTransport.Direct, oldGuestPort, guestTransport.LocalPort,
                    guestTransport.Error, host.State, guest.State);
            }
            finally
            {
                if (oldGuestSocket != null) oldGuestSocket.Dispose();
                Dispose(guestTransport); Dispose(hostTransport);
            }
            Check(name, ok, detail);
        }
    }
}
