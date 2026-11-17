# OriCoop Relay

A small UDP relay server for **OriCoop**, a fan-made online co-op mod for *Ori and the Blind Forest: Definitive
Edition*. Run it on any VPS with a public IPv4 address, give friends its address, and they can play together with a
6-character room code: no port forwarding on the players' side.

这是 **OriCoop** 联机 mod（《Ori and the Blind Forest: Definitive Edition》的同人联机 mod）用的 UDP 中继服务器。
放到任何有公网 IPv4 的 VPS 上运行，把地址告诉朋友，就能用 6 位房间码联机，玩家那边不用开端口。

> Not affiliated with or endorsed by Moon Studios or Microsoft. "Ori" is their trademark; this repo contains no game
> files and the server never sees any. / 与 Moon Studios、Microsoft 无关。本仓库不含任何游戏文件，服务器也不接触游戏文件。

**AI agents: read [AGENTS.md](AGENTS.md) first.** It is a step-by-step deploy guide written for you.
**让 AI 部署：让它先读 [AGENTS.md](AGENTS.md)**，里面是专门写给 AI 的分步部署说明。

The server-style C-server (authority / arbitration, 1-4 players) is at
[oricoop-server](https://github.com/Venompool888/oricoop-server) (in development, not usable by the game yet).
服务器版的 C-server（权威/仲裁服务器）在 [oricoop-server](https://github.com/Venompool888/oricoop-server)（开发中，游戏还不能用）。

| | |
|---|---|
| Relay protocol / 协议版本 | **v2**: up to 4 players per room (host = seat 1, guests = seats 2-4), seat-routed packets, magic `4F 53`, optional identity block v1. v2 does not talk to v1. / 协议 v2：每房最多 4 人（主机座位 1，客人 2–4），按座位转发；与 v1 不互通 |
| Matching mod / 对应 mod | OriCoop release **v0.2.0-2026.11.17** (same tag in the client repo and in oricoop-server). For the older 2-player OriCoop 0.2.0 (ad8f4bc), use tag [`v1-oricoop-0.2.0`](https://github.com/Venompool888/oricoop-relay/tree/v1-oricoop-0.2.0). / 对应 OriCoop 发布版 **v0.2.0-2026.11.17**（客户端仓库和 oricoop-server 用同一个标签）；旧的 2 人版 0.2.0（ad8f4bc）请用标签 `v1-oricoop-0.2.0`。 |
| Default port / 默认端口 | **UDP 47020** (TCP not used / 不用 TCP) |
| Runtime / 运行环境 | .NET 8 (Linux, Docker) or .NET Framework 4.x (Windows `.exe`) |
| Resources / 资源 | 1 vCPU, 512 MB RAM, about 0.5 Mbit/s per playing pair |

---

## English

### What it does

- The host asks the relay for a room and gets a 6-character code; up to 3 friends join with that code.
- The relay first helps the two games connect directly (UDP hole punching). If that fails, it forwards the game
  packets between them. It only sees opaque packets; it stores nothing about the game.
- Rooms close by themselves 30 s after the host goes quiet. At most 1000 rooms, 200 packets/s per address.
- It writes a **player log** (see Privacy below).

### Deploy (pick one)

Full details, firewall / cloud security-group steps and troubleshooting: [AGENTS.md](AGENTS.md).

**A. Docker**

```bash
git clone https://github.com/Venompool888/oricoop-relay oricoop-relay && cd oricoop-relay
docker build -t oricoop-relay server
docker run -d --name oricoop-relay --restart unless-stopped -p 47020:47020/udp \
  -v oricoop-relay-logs:/logs -e ORICOOP_PLAYER_LOG_DIR=/logs oricoop-relay
```

**B. systemd + .NET 8 (no Docker)**

```bash
git clone https://github.com/Venompool888/oricoop-relay oricoop-relay && cd oricoop-relay
dotnet publish server/RelayServer.csproj -c Release -o out --nologo
sudo mkdir -p /opt/oricoop-relay && sudo cp -r out /opt/oricoop-relay/app
sudo cp deploy/oricoop-relay.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now oricoop-relay
```

**C. Windows**

Run `build-relay.ps1` in PowerShell (builds `server\RelayServer.exe` with the .NET Framework compiler that ships with
Windows, then runs the tests). Start `server\RelayServer.exe [port]` and allow UDP 47020 in Windows Firewall (and in
your router, if the PC is behind one).

**Open UDP 47020** in the OS firewall **and** in your cloud provider's security group / firewall rules. This is the
most common reason a relay "does not work".

Check it from any PC with Python 3:

```bash
python3 tools/probe.py <server-ip-or-domain> 47020
```

### Use it in the mod

- In game: online page → **Relay server** → enter the address as `host:port`, for example `203.0.113.10:47020` or
  `relay.example.com:47020`. The game remembers it.
- Or share a link: `oricoop://relay.example.com:47020` (optional room code and display name:
  `oricoop://relay.example.com:47020/ABCD23?name=My+Relay`). Paste it into **OriCoop-Relays.html** in the game folder
  (the relay manager page) to add, choose or remove relays.
- Config file: `[Net] Transport = relay`, `RelayServer = host:port`.

### Privacy and the player log

When a player hosts or joins through the relay, the mod sends its SteamID64 (`0` without Steam) and Steam name. The
relay logs one line per join and per leave: time (UTC), room code, role, SteamID64, IP:port as seen by the server,
Steam name and profile link, and on leave the duration.

- Logs stay on the server. Day files `players-YYYY-MM-DD.log` are deleted after **30 days** by default.
- The SteamID is self-reported by the client and can be forged. Treat it as a hint.
- Name lookups use the public Steam profile XML, or the Steam Web API if you set `STEAM_WEB_API_KEY` in an
  environment file (never in the repo). `ORICOOP_PLAYER_NAME_LOOKUP=0` turns lookups off.
- If you run a public relay, tell your players what you log. Details and all settings:
  [docs/PLAYER-LOG.md](docs/PLAYER-LOG.md).

### Tests

`build-relay.ps1` (Windows) builds the server and runs `tests/RelayTest.cs`: rooms, relaying, hole punching,
timeouts, rate limit, reconnects, identity wire format, player log and retention. It uses the mod's own relay client
code in `tests/client/`.

### Layout

```
server/      RelayServer.cs, RelayPlayerLog.cs, RelayServer.csproj (net8.0), Dockerfile
tests/       RelayTest.cs, client/ (the mod's relay client code, for tests)
deploy/      oricoop-relay.service (systemd template), oricoop-relay.env.example
tools/       probe.py (is the relay answering?)
docs/        PLAYER-LOG.md
```

License: [MIT](LICENSE).

---

## 中文

### 它做什么

- 主机向中继要一个房间，拿到 6 位房间码；最多 3 个朋友输入房间码加入。
- 中继先帮两边的游戏直接连上（UDP 打洞）；连不上就由服务器转发游戏数据。服务器只转发看不懂的数据包，不保存任何游戏内容。
- 主机 30 秒没有动静，房间自动关闭。最多 1000 个房间，每个地址每秒最多 200 个包。
- 会写**玩家日志**（见下面的隐私说明）。

### 部署（三选一）

完整步骤、防火墙和云服务商安全组的设置、常见问题见 [AGENTS.md](AGENTS.md)。

**A. Docker**

```bash
git clone https://github.com/Venompool888/oricoop-relay oricoop-relay && cd oricoop-relay
docker build -t oricoop-relay server
docker run -d --name oricoop-relay --restart unless-stopped -p 47020:47020/udp \
  -v oricoop-relay-logs:/logs -e ORICOOP_PLAYER_LOG_DIR=/logs oricoop-relay
```

**B. systemd + .NET 8（不用 Docker）**

```bash
git clone https://github.com/Venompool888/oricoop-relay oricoop-relay && cd oricoop-relay
dotnet publish server/RelayServer.csproj -c Release -o out --nologo
sudo mkdir -p /opt/oricoop-relay && sudo cp -r out /opt/oricoop-relay/app
sudo cp deploy/oricoop-relay.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now oricoop-relay
```

**C. Windows**

在 PowerShell 里运行 `build-relay.ps1`（用 Windows 自带的 .NET Framework 编译器生成 `server\RelayServer.exe`，并跑测试）。
然后运行 `server\RelayServer.exe [端口]`，在 Windows 防火墙里放行 UDP 47020（电脑在路由器后面的话，路由器也要转发）。

**一定要开放 UDP 47020**：系统防火墙**和**云服务商控制台里的安全组 / 防火墙规则都要开。中继"连不上"大多是这个原因。

在任何装了 Python 3 的电脑上检查：

```bash
python3 tools/probe.py <服务器IP或域名> 47020
```

### 在 mod 里使用

- 游戏里：联机页面 → **服务器中继** → 输入地址，格式 `主机:端口`，例如 `203.0.113.10:47020` 或 `relay.example.com:47020`。之后会记住。
- 也可以发链接：`oricoop://relay.example.com:47020`（可以带房间码和名字：`oricoop://relay.example.com:47020/ABCD23?name=My+Relay`）。
  粘贴到游戏目录里的 **OriCoop-Relays.html**（中继管理页面），就能添加、选择或删除中继服务器。
- 配置文件：`[Net] Transport = relay`，`RelayServer = 主机:端口`。

### 隐私和玩家日志

玩家通过中继开房或加入时，mod 会发送自己的 SteamID64（没开 Steam 就是 `0`）和 Steam 昵称。服务器每次进出房间记一行：
时间（UTC）、房间码、身份（主机/客人）、SteamID64、服务器看到的 IP:端口、Steam 昵称和个人资料链接，离开时还有时长。

- 日志只保存在服务器上。按天的文件 `players-YYYY-MM-DD.log` 默认 **30 天**后自动删除。
- SteamID 是客户端自己报的，可以伪造，只能当参考。
- 昵称通过 Steam 公开资料（XML）查询；在环境变量文件里设置 `STEAM_WEB_API_KEY` 后改用 Steam Web API（key 不要放进仓库）。
  `ORICOOP_PLAYER_NAME_LOOKUP=0` 关闭查询。
- 如果你开的是公开中继，请告诉玩家你记录了什么。日志格式和所有设置见 [docs/PLAYER-LOG.md](docs/PLAYER-LOG.md)。

### 协议版本

当前是**中继协议 v2**：每个房间最多 4 人（主机 + 最多 3 个客人），对应 OriCoop 发布版 **v0.2.0-2026.11.17**。旧的 2 人版客户端（OriCoop 0.2.0，ad8f4bc）只能连
协议 v1 的服务器，请用标签 `v1-oricoop-0.2.0` 部署。以后服务器和客户端仓库始终同步发布、打同一个标签。

许可证：[MIT](LICENSE)。
