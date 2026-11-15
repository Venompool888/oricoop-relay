using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace OriCoop.Relay
{
    // Who a relay client says it is. Clients append an optional identity block to HostRoom/JoinRoom;
    // older clients send none (Known = false). The SteamID is SELF-REPORTED and can be forged.
    public sealed class PlayerIdentity
    {
        public bool Known;
        public ulong SteamId;
        public string ClientName = "";

        public static readonly PlayerIdentity Unknown = new PlayerIdentity();
    }

    // Identity block: 'I', version, u64 SteamID64 (LE), u8 name length, UTF-8 name (<= MaxNameBytes).
    // Later versions may append fields after the name; version-1 readers ignore them.
    internal static class RelayIdentityWire
    {
        internal const byte Tag = 0x49;
        internal const byte Version = 1;
        internal const int HeaderSize = 11;
        internal const int MaxNameBytes = 32;

        internal static PlayerIdentity Parse(byte[] data, int offset, int length)
        {
            if (data == null || length - offset < HeaderSize || data[offset] != Tag || data[offset + 1] < 1)
                return PlayerIdentity.Unknown;
            ulong id = 0;
            for (int i = 7; i >= 0; i--) id = (id << 8) | data[offset + 2 + i];
            int nameLength = data[offset + 10];
            if (nameLength > MaxNameBytes || nameLength > length - offset - HeaderSize) return PlayerIdentity.Unknown;
            PlayerIdentity identity = new PlayerIdentity();
            identity.Known = true;
            identity.SteamId = id;
            identity.ClientName = nameLength == 0 ? "" : Encoding.UTF8.GetString(data, offset + HeaderSize, nameLength);
            return identity;
        }
    }

    // Looks up Steam persona names off the relay thread, with a cache. The Web API key (if any) comes from
    // the server's environment and is never logged.
    public sealed class SteamNameResolver
    {
        public const string WebApiUrl = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={0}&steamids={1}";
        public const string ProfileXmlUrl = "https://steamcommunity.com/profiles/{0}?xml=1";

        private sealed class Entry
        {
            internal string Name;
            internal DateTime Expires;
        }

        private readonly object gate = new object();
        private readonly Dictionary<ulong, Entry> cache = new Dictionary<ulong, Entry>();
        private readonly Dictionary<ulong, List<Action<string>>> waiting = new Dictionary<ulong, List<Action<string>>>();
        private readonly string apiKey;

        public TimeSpan CacheTime = TimeSpan.FromHours(24);
        public TimeSpan FailureCacheTime = TimeSpan.FromMinutes(10);
        public int MaxLookups = 8;
        public int MaxCacheEntries = 20000;
        // url -> body, or null on failure. Replaced in tests. Runs on a worker thread.
        public Func<string, string> Fetch = HttpGet;
        // Runs a lookup off the relay thread. Replaced in tests to run inline.
        public Action<WaitCallback> Queue = delegate(WaitCallback work) { ThreadPool.QueueUserWorkItem(work); };

        public SteamNameResolver(string apiKey)
        {
            this.apiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey.Trim();
        }

        public bool HasApiKey { get { return apiKey != null; } }

        public bool TryGetCached(ulong id, out string name)
        {
            name = null;
            lock (gate)
            {
                Entry entry;
                if (!cache.TryGetValue(id, out entry) || entry.Expires <= DateTime.UtcNow) return false;
                name = entry.Name;
                return true;
            }
        }

        // done(name or null) runs on a worker thread. Returns false (and never calls done) when the
        // lookup queue is full; the caller logs without a name then.
        public bool Resolve(ulong id, Action<string> done)
        {
            lock (gate)
            {
                List<Action<string>> list;
                if (waiting.TryGetValue(id, out list))
                {
                    if (list.Count >= 16) return false;
                    list.Add(done);
                    return true;
                }
                if (waiting.Count >= MaxLookups) return false;
                list = new List<Action<string>>();
                list.Add(done);
                waiting.Add(id, list);
            }
            Queue(delegate { Lookup(id); });
            return true;
        }

        private void Lookup(ulong id)
        {
            string name = null;
            try { name = FetchName(id); }
            catch (Exception) { name = null; }
            List<Action<string>> list;
            lock (gate)
            {
                if (cache.Count >= MaxCacheEntries) cache.Clear();
                Entry entry = new Entry();
                entry.Name = name;
                entry.Expires = DateTime.UtcNow + (name == null ? FailureCacheTime : CacheTime);
                cache[id] = entry;
                if (!waiting.TryGetValue(id, out list)) list = new List<Action<string>>();
                waiting.Remove(id);
            }
            foreach (Action<string> done in list)
            {
                try { done(name); }
                catch (Exception) { }
            }
        }

        private string FetchName(ulong id)
        {
            string text = id.ToString(CultureInfo.InvariantCulture);
            if (apiKey != null)
            {
                string body = Fetch(string.Format(WebApiUrl, Uri.EscapeDataString(apiKey), text));
                string name = ParseSummariesJson(body);
                if (name != null) return name;
            }
            return ParseProfileXml(Fetch(string.Format(ProfileXmlUrl, text)));
        }

        private static readonly Regex PersonaName = new Regex("\"personaname\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");

        // GetPlayerSummaries v2 response -> personaname, or null.
        public static string ParseSummariesJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Match match = PersonaName.Match(json);
            if (!match.Success) return null;
            string name = JsonUnescape(match.Groups[1].Value);
            return string.IsNullOrEmpty(name) ? null : name;
        }

        private static string JsonUnescape(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char e = s[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        int code;
                        if (i + 4 < s.Length &&
                            Int32.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        else sb.Append('u');
                        break;
                    default: sb.Append(e); break; // \" \\ \/
                }
            }
            return sb.ToString();
        }

        // steamcommunity.com/profiles/<id>?xml=1 -> <steamID> text, or null (also for <response><error>).
        public static string ParseProfileXml(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings();
                settings.DtdProcessing = DtdProcessing.Prohibit;
                settings.XmlResolver = null;
                using (XmlReader reader = XmlReader.Create(new StringReader(xml), settings))
                {
                    if (!reader.ReadToFollowing("profile")) return null;
                    if (!reader.ReadToDescendant("steamID")) return null;
                    string name = reader.ReadElementContentAsString();
                    return string.IsNullOrEmpty(name) ? null : name;
                }
            }
            catch (Exception) { return null; }
        }

        private static string HttpGet(string url)
        {
            try
            {
                // .NET Framework builds default to old TLS; Steam needs TLS 1.2.
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.UserAgent = "OriCoop-Relay";
                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                {
                    MemoryStream copy = new MemoryStream();
                    byte[] chunk = new byte[8192];
                    int read;
                    while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        copy.Write(chunk, 0, read);
                        if (copy.Length > 512 * 1024) return null;
                    }
                    return Encoding.UTF8.GetString(copy.ToArray());
                }
            }
            catch (Exception) { return null; }
        }
    }

    // One line per player join/leave, to stdout (journald) and to a daily file players-YYYY-MM-DD.log.
    // Files older than RetentionDays are deleted. Thread-safe: name lookups finish on worker threads.
    public sealed class PlayerLog
    {
        public const string ProfileUrlPrefix = "https://steamcommunity.com/profiles/";
        public const int DefaultRetentionDays = 30;

        private readonly object gate = new object();
        private readonly Action<string> console;
        private readonly string directory;
        private readonly int retentionDays;
        private readonly SteamNameResolver resolver;
        private string currentDay;

        // Replaced in tests.
        public Func<DateTime> UtcNow = delegate { return DateTime.UtcNow; };

        public PlayerLog(Action<string> console, string directory, int retentionDays, SteamNameResolver resolver)
        {
            this.console = console;
            this.retentionDays = retentionDays <= 0 ? DefaultRetentionDays : retentionDays;
            this.resolver = resolver;
            this.directory = null;
            if (string.IsNullOrEmpty(directory)) return;
            // A bad log folder must never stop the relay: fall back to stdout only.
            try { Directory.CreateDirectory(directory); this.directory = directory; }
            catch (Exception ex)
            {
                try { console("player log: cannot use folder " + directory + " (" + ex.Message + "); stdout only"); }
                catch (Exception) { }
                return;
            }
            Prune(DateTime.UtcNow);
        }

        public string LogDirectory { get { return directory; } }
        public int RetentionDays { get { return retentionDays; } }

        // From the environment (systemd EnvironmentFile): ORICOOP_PLAYER_LOG_DIR (else systemd's
        // LOGS_DIRECTORY, else stdout only), ORICOOP_PLAYER_LOG_DAYS (30), STEAM_WEB_API_KEY or
        // ORICOOP_STEAM_API_KEY_FILE, ORICOOP_PLAYER_NAME_LOOKUP=0 to turn name lookups off.
        public static PlayerLog FromEnvironment(Action<string> console)
        {
            string dir = Environment.GetEnvironmentVariable("ORICOOP_PLAYER_LOG_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                dir = Environment.GetEnvironmentVariable("LOGS_DIRECTORY");
                // systemd joins several LogsDirectory= paths with ':'.
                if (!string.IsNullOrEmpty(dir) && dir.StartsWith("/")) dir = dir.Split(':')[0];
            }
            int days;
            if (!Int32.TryParse(Environment.GetEnvironmentVariable("ORICOOP_PLAYER_LOG_DAYS"), out days)) days = DefaultRetentionDays;
            SteamNameResolver resolver = null;
            if (Environment.GetEnvironmentVariable("ORICOOP_PLAYER_NAME_LOOKUP") != "0")
            {
                string key = Environment.GetEnvironmentVariable("STEAM_WEB_API_KEY");
                string keyFile = Environment.GetEnvironmentVariable("ORICOOP_STEAM_API_KEY_FILE");
                if (string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(keyFile))
                {
                    try { key = File.ReadAllText(keyFile).Trim(); }
                    catch (Exception) { console("player log: cannot read ORICOOP_STEAM_API_KEY_FILE; using public profile lookup"); }
                }
                resolver = new SteamNameResolver(key);
            }
            // ORICOOP_PLAYER_LOG_STDOUT=0: player lines only in the files (journald keeps its own, often longer, history).
            bool toStdout = Environment.GetEnvironmentVariable("ORICOOP_PLAYER_LOG_STDOUT") != "0" || string.IsNullOrEmpty(dir);
            Action<string> sink = toStdout ? console : delegate(string line) { if (!line.StartsWith("PLAYER ")) console(line); };
            PlayerLog log = new PlayerLog(sink, dir, days, resolver);
            console("player log: " + (log.directory == null ? "stdout only" :
                (toStdout ? "stdout + " : "") + log.directory + "/players-YYYY-MM-DD.log") +
                ", keep " + log.retentionDays + " days, names " +
                (resolver == null ? "off" : resolver.HasApiKey ? "Steam Web API + profile xml" : "profile xml") +
                ". SteamIDs are self-reported by clients and can be forged.");
            return log;
        }

        public void Join(string room, string role, PlayerIdentity id, EndPoint from)
        {
            if (id == null) id = PlayerIdentity.Unknown;
            DateTime at = UtcNow();
            string cached;
            if (!id.Known || id.SteamId == 0 || resolver == null)
            {
                Write(FormatJoin(at, room, role, id, from, null));
                return;
            }
            if (resolver.TryGetCached(id.SteamId, out cached))
            {
                Write(FormatJoin(at, room, role, id, from, cached));
                return;
            }
            PlayerIdentity copy = id;
            bool queued = resolver.Resolve(id.SteamId, delegate(string name) { Write(FormatJoin(at, room, role, copy, from, name)); });
            if (!queued) Write(FormatJoin(at, room, role, id, from, null));
        }

        public void Leave(string room, string role, PlayerIdentity id, EndPoint from, DateTime joinedUtc, string reason)
        {
            if (id == null) id = PlayerIdentity.Unknown;
            DateTime at = UtcNow();
            string name = null;
            if (resolver != null && id.Known && id.SteamId != 0) resolver.TryGetCached(id.SteamId, out name);
            Write(FormatLeave(at, room, role, id, from, name, at - joinedUtc, reason));
        }

        public static string ProfileUrl(ulong steamId)
        {
            return ProfileUrlPrefix + steamId.ToString(CultureInfo.InvariantCulture);
        }

        public static string FormatJoin(DateTime utc, string room, string role, PlayerIdentity id, EndPoint from, string steamName)
        {
            StringBuilder sb = Begin("JOIN", utc, room, role, id, from, steamName);
            return sb.ToString();
        }

        public static string FormatLeave(DateTime utc, string room, string role, PlayerIdentity id, EndPoint from,
            string steamName, TimeSpan duration, string reason)
        {
            StringBuilder sb = Begin("LEAVE", utc, room, role, id, from, steamName);
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            sb.Append(" duration=").Append(((int)duration.TotalHours).ToString("00", CultureInfo.InvariantCulture))
                .Append(':').Append(duration.Minutes.ToString("00", CultureInfo.InvariantCulture))
                .Append(':').Append(duration.Seconds.ToString("00", CultureInfo.InvariantCulture));
            sb.Append(" reason=").Append(Quote(reason));
            return sb.ToString();
        }

        private static StringBuilder Begin(string what, DateTime utc, string room, string role, PlayerIdentity id,
            EndPoint from, string steamName)
        {
            if (id == null) id = PlayerIdentity.Unknown;
            StringBuilder sb = new StringBuilder(256);
            sb.Append("PLAYER ").Append(what).Append(' ')
                .Append(utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            sb.Append(" room=").Append(room ?? "-");
            sb.Append(" role=").Append(role ?? "-");
            sb.Append(" steamid=");
            if (!id.Known) sb.Append("unknown");
            else sb.Append(id.SteamId.ToString(CultureInfo.InvariantCulture));
            sb.Append(" ip=").Append(EndPointText(from));
            sb.Append(" name=").Append(steamName == null ? "-" : Quote(steamName));
            sb.Append(" client_name=").Append(!id.Known || string.IsNullOrEmpty(id.ClientName) ? "-" : Quote(id.ClientName));
            sb.Append(" profile=").Append(id.Known && id.SteamId != 0 ? ProfileUrl(id.SteamId) : "-");
            return sb;
        }

        private static string EndPointText(EndPoint endpoint)
        {
            IPEndPoint ip = endpoint as IPEndPoint;
            if (ip == null) return endpoint == null ? "-" : endpoint.ToString();
            return ip.Address.ToString() + ":" + ip.Port.ToString(CultureInfo.InvariantCulture);
        }

        // Names are user-controlled: one line, no quotes or control characters, bounded length.
        public static string Quote(string text)
        {
            if (text == null) text = "";
            StringBuilder sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            int count = 0;
            foreach (char c in text)
            {
                if (count >= 64) { sb.Append("..."); break; }
                if (char.IsControl(c) || c == '"' || c == '\\' || c == (char)0x2028 || c == (char)0x2029) sb.Append('?');
                else sb.Append(c);
                count++;
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string FileNameFor(DateTime utc)
        {
            return "players-" + utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log";
        }

        private void Write(string line)
        {
            lock (gate)
            {
                try { console(line); }
                catch (Exception) { }
                if (directory == null) return;
                DateTime now = UtcNow();
                string day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                try
                {
                    if (day != currentDay)
                    {
                        currentDay = day;
                        Prune(now);
                    }
                    File.AppendAllText(Path.Combine(directory, FileNameFor(now)), line + "\n", new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    try { console("player log: write failed: " + ex.Message); }
                    catch (Exception) { }
                }
            }
        }

        // Deletes players-YYYY-MM-DD.log files whose day is more than RetentionDays before today.
        public int Prune(DateTime nowUtc)
        {
            if (directory == null) return 0;
            int removed = 0;
            DateTime cutoff = nowUtc.Date.AddDays(-retentionDays);
            string[] files;
            try { files = Directory.GetFiles(directory, "players-*.log"); }
            catch (Exception) { return 0; }
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                DateTime day;
                if (name.Length != 18 || !DateTime.TryParseExact(name.Substring(8), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out day)) continue;
                if (day.Date >= cutoff) continue;
                try { File.Delete(file); removed++; }
                catch (Exception) { }
            }
            return removed;
        }
    }
}
