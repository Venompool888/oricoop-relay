# AGENTS.md: deploying OriCoop Relay (for AI agents)

You are an AI agent asked to deploy this relay server for a human. Follow this file in order. It assumes a fresh Linux
VPS (Debian / Ubuntu / RHEL-family / Alpine) that you reach over SSH, but section 9 covers Docker-only hosts and
Windows.

What you are deploying: a single-process UDP server (`server/RelayServer.cs`, .NET 8) that pairs two OriCoop game
clients by a 6-character room code, helps them hole-punch, and relays packets if punching fails. It listens on **one
UDP port, default 47020**. It needs no database, no TLS, no domain, no TCP port. It writes a player log (SteamID, IP,
Steam name), so it handles personal data; see section 7.

Relay protocol **v2** (up to 4 players per room), matching OriCoop release **v0.2.1-2026.11.18** (the same tag exists in the client
repo and in oricoop-server). For old 2-player clients (OriCoop 0.2.0, ad8f4bc) deploy tag `v1-oricoop-0.2.0` instead; v1 and v2 do not talk to each other. Check the human's client version first.

---

## 0. Ground rules

- **Ask before you act** on anything in the "needs confirmation" list (section 1). Read-only checks are fine.
- **Never** print, log, commit or echo secrets (Steam Web API key, SSH keys, cloud credentials). If the human gives
  you a Steam key, write it only into the root-only env file on the server (section 5.5) and do not repeat it back.
- **Never** commit the human's real server address, host name or SSH alias into this repo or any public place. Use
  placeholders (`203.0.113.10`, `relay.example.com`) in anything you write back to the repo.
- Do not disable the OS firewall to "make it work". Open exactly the one UDP port.
- Do not touch other services on the host. If port 47020 is taken, ask for another port.
- Prefer reversible steps: build into a new folder, then switch (section 8).

## 1. Ask the human first

Ask these in one message, with the defaults, before changing anything:

1. **Server access**: host / IP and SSH user, or "run it on this machine". Do you have sudo?
2. **Port**: UDP 47020 unless they want another (any free port 1024-65535).
3. **Install method**: systemd + .NET 8 (default, smallest), or Docker (if Docker is already used on that host).
4. **Domain** (optional): a DNS name players will type, e.g. `relay.example.com`. Only an A record to the server's
   public IPv4 is needed; no certificate. Players can also just use the IP.
5. **Steam name lookups**: off, public profile XML (default, no key), or Steam Web API with a key the human sets.
   If they want the key, ask them to put it in the env file themselves, or to paste it only for that step.
6. **Player log retention**: 30 days (default) or another number; also whether player lines may go to journald
   (`ORICOOP_PLAYER_LOG_STDOUT=0` keeps them only in the day files).
7. **Cloud provider** (GCP, AWS, Azure, Alibaba Cloud, Tencent Cloud, Oracle, Hetzner, DigitalOcean, Vultr, other)
   and whether you may change its firewall / security group, or the human will.

**Needs explicit confirmation from the human before you do it:**

- Installing packages (`apt`/`dnf`/`apk`, .NET, Docker) or running remote install scripts.
- Creating system users, writing to `/etc`, `/opt`, enabling services.
- Changing the OS firewall or the cloud security group / firewall rules (these affect the whole server).
- Restarting the service once players may be using it (it drops current rooms).
- Rolling back, deleting old builds or logs.
- Anything that costs money (resizing the VM, static IP, load balancer).

## 2. Inspect the host (read-only)

```bash
uname -m                      # x86_64 or aarch64 (both supported by .NET 8); armv7l: use Docker or skip
cat /etc/os-release           # distro and version
nproc; free -m; df -h /       # need ~1 vCPU, 512 MB RAM, ~500 MB disk (SDK) / ~100 MB (runtime only)
command -v dotnet && dotnet --list-sdks && dotnet --list-runtimes
command -v docker && docker --version
systemctl --version | head -1
sudo ss -ulpn | grep -w 47020 || echo "port 47020 free"
curl -s -4 https://ifconfig.me || curl -s -4 https://api.ipify.org   # public IPv4 (players need IPv4)
sudo ufw status 2>/dev/null; sudo firewall-cmd --state 2>/dev/null; sudo nft list ruleset 2>/dev/null | head -50
```

Notes:
- The server is **IPv4 only**. An IPv6-only VPS will not work; ask for an IPv4 address.
- If the public IP differs from `ip addr` output, the VM is behind cloud NAT (normal on GCP / AWS / Azure / Alibaba /
  Tencent). That is fine; you still must open the port in the cloud console (section 6).

## 3. Get the code

```bash
sudo mkdir -p /opt/oricoop-relay && sudo chown "$USER" /opt/oricoop-relay
git clone https://github.com/Venompool888/oricoop-relay /opt/oricoop-relay/src
cd /opt/oricoop-relay/src && git log -1 --oneline
```

No git? `sudo apt-get install -y git` (confirm first), or download the repo archive and unpack it to the same path.

## 4. Install .NET 8 (SDK to build; runtime to run)

You need the **.NET 8 SDK** once to build. Pick one:

**4a. Distro packages** (Ubuntu 22.04+/24.04, Debian 12 via Microsoft repo, Fedora, RHEL 8/9):

```bash
# Ubuntu 22.04 / 24.04 (in the Ubuntu archive):
sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0
# Debian 12: add Microsoft's repo first
wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O /tmp/ms.deb
sudo dpkg -i /tmp/ms.deb && sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0
# Fedora / RHEL 9:
sudo dnf install -y dotnet-sdk-8.0
```

`dotnet` is then `/usr/bin/dotnet`, which is what `deploy/oricoop-relay.service` uses.

**4b. dotnet-install.sh** (any distro, no root for the install itself, also aarch64):

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 8.0 --install-dir /opt/oricoop-relay/dotnet
/opt/oricoop-relay/dotnet/dotnet --list-sdks
```

Then use `/opt/oricoop-relay/dotnet/dotnet` everywhere below **and** change `ExecStart=` in the unit to
`/opt/oricoop-relay/dotnet/dotnet /opt/oricoop-relay/app/RelayServer.dll 47020`. `libicu` is **not** required (the
project sets `InvariantGlobalization`), but `ca-certificates` is needed for Steam name lookups over HTTPS. If
`dotnet` fails to start on a minimal image, install the .NET dependencies listed at
https://learn.microsoft.com/dotnet/core/install/linux (usually `libstdc++`, `zlib`, `libssl`).

## 5. Build and install (systemd)

### 5.1 Build into a new folder

```bash
cd /opt/oricoop-relay/src
dotnet publish server/RelayServer.csproj -c Release -o /opt/oricoop-relay/app-new --nologo
```

A `SYSLIB0014` "WebRequest is obsolete" warning is expected and harmless. Any **error** = stop and report it.

### 5.2 Quick foreground check (optional, before systemd)

```bash
timeout 5 dotnet /opt/oricoop-relay/app-new/RelayServer.dll 47020; echo "exit $?"
```

Expected first lines, then `exit 124` (killed by timeout = it was running):
```
OriCoop relay listening on UDP port 47020
player log: stdout only, keep 30 days, names profile xml. SteamIDs are self-reported by clients and can be forged.
```
`relay failed: Address already in use` = the port is taken; pick another port and ask the human.

### 5.3 Put the build in place

```bash
cd /opt/oricoop-relay
[ -d app ] && mv app app-old       # first install: no app yet
mv app-new app
sudo chown -R root:root /opt/oricoop-relay/app
```

### 5.4 systemd unit

```bash
sudo cp /opt/oricoop-relay/src/deploy/oricoop-relay.service /etc/systemd/system/oricoop-relay.service
# Edit if needed: dotnet path (4b), port (default 47020), MemoryMax.
sudo systemctl daemon-reload
sudo systemctl enable --now oricoop-relay
```

The unit uses `DynamicUser=yes` (a throwaway system user, so you do not need `useradd`), `LogsDirectory=oricoop-relay`
(creates `/var/log/oricoop-relay`, writable by that user, passed to the server as `LOGS_DIRECTORY`) and
`EnvironmentFile=-/etc/oricoop-relay.env` (optional).

If the human prefers a fixed user instead of `DynamicUser`:
```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin oricoop-relay
```
then replace `DynamicUser=yes` with `User=oricoop-relay` and `Group=oricoop-relay`.

To change settings without editing the unit, use a drop-in:
```bash
sudo systemctl edit oricoop-relay       # opens an override file; or write it directly:
sudo mkdir -p /etc/systemd/system/oricoop-relay.service.d
printf '[Service]\nLogsDirectory=oricoop-relay\nEnvironmentFile=-/etc/oricoop-relay.env\n' | \
  sudo tee /etc/systemd/system/oricoop-relay.service.d/player-log.conf
sudo systemctl daemon-reload && sudo systemctl restart oricoop-relay
```

### 5.5 Env file (optional settings and the Steam key)

```bash
sudo install -m 600 -o root -g root /opt/oricoop-relay/src/deploy/oricoop-relay.env.example /etc/oricoop-relay.env
sudo nano /etc/oricoop-relay.env     # the human uncomments what they want; the KEY is typed by the human
sudo stat -c '%a %U' /etc/oricoop-relay.env    # must print: 600 root
sudo systemctl restart oricoop-relay
```

systemd reads this file as root before dropping privileges, so `chmod 600` root-only is correct. Never `cat` the file
back into the conversation once a key is in it.

## 6. Open the UDP port

Both layers. Missing either one is the number-one failure.

### 6.1 OS firewall (only if one is active, see section 2)

```bash
# ufw (Ubuntu/Debian):
sudo ufw allow 47020/udp comment 'OriCoop relay'
# firewalld (RHEL/Fedora/CentOS/Alma/Rocky):
sudo firewall-cmd --permanent --add-port=47020/udp && sudo firewall-cmd --reload
# plain iptables (persist with iptables-persistent / netfilter-persistent):
sudo iptables -I INPUT -p udp --dport 47020 -j ACCEPT
```

Make sure SSH stays allowed before enabling ufw (`sudo ufw allow OpenSSH`). Never run `ufw enable` without that.

### 6.2 Cloud security group / VPC firewall

Allow **inbound UDP 47020 from 0.0.0.0/0** (players come from anywhere). Outbound is open by default on all of these.
If you have no CLI access or permission, give the human the console steps instead.

**Google Cloud (GCP)**
```bash
gcloud compute firewall-rules create oricoop-relay --network=default --direction=INGRESS \
  --action=ALLOW --rules=udp:47020 --source-ranges=0.0.0.0/0 --target-tags=oricoop-relay
gcloud compute instances add-tags <INSTANCE> --zone=<ZONE> --tags=oricoop-relay
```
Console: VPC network → Firewall → Create rule (ingress, udp:47020, 0.0.0.0/0, target tag), then add the tag to the VM.

**AWS (EC2)**
```bash
aws ec2 authorize-security-group-ingress --group-id <sg-xxxxxxxx> \
  --ip-permissions 'IpProtocol=udp,FromPort=47020,ToPort=47020,IpRanges=[{CidrIp=0.0.0.0/0,Description="OriCoop relay"}]'
```
Console: EC2 → Instances → Security tab → security group → Edit inbound rules → Custom UDP, 47020, 0.0.0.0/0.
Lightsail: Networking tab → IPv4 Firewall → Add rule → Custom, UDP, 47020.

**Azure**
```bash
az network nsg rule create -g <RESOURCE_GROUP> --nsg-name <NSG_NAME> -n oricoop-relay --priority 1010 \
  --direction Inbound --access Allow --protocol Udp --destination-port-ranges 47020 --source-address-prefixes '*'
```
Console: VM → Networking → Add inbound port rule → UDP, 47020.

**Alibaba Cloud (阿里云 ECS)**
```bash
aliyun ecs AuthorizeSecurityGroup --RegionId <REGION> --SecurityGroupId <sg-xxxx> \
  --IpProtocol udp --PortRange 47020/47020 --SourceCidrIp 0.0.0.0/0 --Description "OriCoop relay"
```
控制台：ECS → 实例 → 安全组 → 配置规则 → 入方向 → 手动添加：协议 UDP，端口 47020/47020，授权对象 0.0.0.0/0。
轻量应用服务器：实例 → 防火墙 → 添加规则 → UDP 47020。

**Tencent Cloud (腾讯云 CVM / Lighthouse)**
控制台（CVM）：安全组 → 修改规则 → 入站规则 → 添加：来源 0.0.0.0/0，协议端口 `UDP:47020`，策略 允许。
轻量应用服务器 Lighthouse：实例 → 防火墙 → 添加规则 → 协议 UDP，端口 47020。
CLI (`tccli`): `tccli vpc CreateSecurityGroupPolicies --SecurityGroupId <sg-xxxx> --SecurityGroupPolicySet '{"Ingress":[{"Protocol":"UDP","Port":"47020","CidrBlock":"0.0.0.0/0","Action":"ACCEPT","PolicyDescription":"OriCoop relay"}]}'`

**Oracle Cloud**: VCN → Security Lists (or NSG) → Add Ingress Rule → UDP, dest port 47020, source 0.0.0.0/0. Oracle
images also ship strict iptables rules: add `sudo iptables -I INPUT -p udp --dport 47020 -j ACCEPT` and persist it
(`sudo netfilter-persistent save`).

**Hetzner / DigitalOcean / Vultr / Linode**: only if a cloud firewall is attached to the server: add inbound UDP
47020 from anywhere in its Firewall page. Otherwise the OS firewall is the only layer.

## 7. Verify

On the server:
```bash
systemctl is-active oricoop-relay                          # active
journalctl -u oricoop-relay -n 20 --no-pager               # see expected lines below
sudo ss -ulpn | grep -w 47020                              # dotnet listening on 0.0.0.0:47020
```
Expected log lines:
```
OriCoop relay listening on UDP port 47020
player log: stdout + /var/log/oricoop-relay/players-YYYY-MM-DD.log, keep 30 days, names profile xml. SteamIDs are self-reported by clients and can be forged.
```

From **outside** the server (the human's PC, or your own machine; testing from the server itself does not prove the
cloud firewall is open):
```bash
python3 tools/probe.py <PUBLIC_IP_OR_DOMAIN> 47020
# OK relay 203.0.113.10:47020 answered, test room K7QX2M
```
The probe creates an empty test room; the log then shows `room K7QX2M created ...` and a `PLAYER JOIN ...
steamid=unknown` line, and the room closes by itself after ~30 s. If the probe fails from outside but works on the
server (`python3 tools/probe.py 127.0.0.1 47020`), the problem is a firewall layer (section 6).

Then tell the human what players should enter: `<PUBLIC_IP_OR_DOMAIN>:47020`, or the link
`oricoop://<PUBLIC_IP_OR_DOMAIN>:47020` for the OriCoop-Relays.html relay manager page. Final check is a real session:
one player hosts via "Relay server" and gets a room code, the other joins with it.

Player log, privacy:
- Day files: `/var/log/oricoop-relay/players-YYYY-MM-DD.log` (UTC days), deleted after `ORICOOP_PLAYER_LOG_DAYS`
  (default 30). Read: `sudo tail -f /var/log/oricoop-relay/players-$(date -u +%F).log`.
- journald keeps its own copy of stdout under its own limits. For strict retention set
  `ORICOOP_PLAYER_LOG_STDOUT=0`, or set `MaxRetentionSec=30day` in `/etc/systemd/journald.conf` (confirm first; it
  affects all services).
- Remind the human: if the relay is public, they should tell players that SteamID, IP and Steam name are logged.

## 8. Upgrade and roll back

Upgrade (build first, switch only if the build succeeded; restarting drops current rooms, so confirm timing):
```bash
cd /opt/oricoop-relay/src && git fetch && git log --oneline HEAD..origin/main   # show the human what changes
git pull --ff-only
dotnet publish server/RelayServer.csproj -c Release -o /opt/oricoop-relay/app-new --nologo
cd /opt/oricoop-relay && sudo rm -rf app-old && sudo mv app app-old && sudo mv app-new app
sudo systemctl restart oricoop-relay && journalctl -u oricoop-relay -n 5 --no-pager
```
If the unit file changed in the repo: `diff /opt/oricoop-relay/src/deploy/oricoop-relay.service
/etc/systemd/system/oricoop-relay.service`, merge by hand (keep the human's port and dotnet path), `daemon-reload`.

Roll back:
```bash
cd /opt/oricoop-relay && sudo rm -rf app && sudo mv app-old app && sudo systemctl restart oricoop-relay
```

Protocol upgrades (v1 → v2): the mod and the relay must match. Before upgrading across a protocol version, ask the
human whether all players have updated the mod; a v2 relay will not serve v1 clients. Running two relays on two ports
(old and new) during the switch is fine.

## 9. Other hosts

### Docker (any OS with Docker, or Docker-only hosts)
```bash
cd /opt/oricoop-relay/src
docker build -t oricoop-relay server
docker run -d --name oricoop-relay --restart unless-stopped -p 47020:47020/udp \
  -v oricoop-relay-logs:/logs -e ORICOOP_PLAYER_LOG_DIR=/logs \
  --env-file /etc/oricoop-relay.env oricoop-relay          # drop --env-file if you have no env file
docker logs -f oricoop-relay
```
Upgrade: `git pull`, `docker build -t oricoop-relay server`, `docker rm -f oricoop-relay`, run again (the volume keeps
the logs). Roll back: tag the old image first (`docker tag oricoop-relay oricoop-relay:prev`) and run that tag.
Remember `-p 47020:47020/udp` (the `/udp` matters; without it Docker publishes TCP only). Docker bypasses ufw for
published ports on many setups, but the cloud security group still applies.

### Windows (a home PC or Windows VPS)
```powershell
.\build-relay.ps1                          # builds server\RelayServer.exe and runs the tests (needs .NET Framework 4.x, built in)
New-NetFirewallRule -DisplayName "OriCoop relay" -Direction Inbound -Protocol UDP -LocalPort 47020 -Action Allow   # admin, confirm first
.\server\RelayServer.exe 47020
```
For a home PC the router must forward UDP 47020 to that PC, and the ISP must give a public IPv4 (no CGNAT). For
auto-start, use Task Scheduler ("At startup", run whether user is logged on or not) or NSSM; ask the human which.
The player log on Windows: set `ORICOOP_PLAYER_LOG_DIR` to a folder, otherwise it goes to the console only.

## 10. Common mistakes

| Symptom | Cause / fix |
|---|---|
| Probe works on the server, fails from outside | Cloud security group or OS firewall not open for **UDP** 47020 (section 6). |
| Opened TCP 47020 | The relay is UDP only. Open UDP. |
| Docker: probe fails | Published without `/udp`, e.g. `-p 47020:47020`. Use `-p 47020:47020/udp`. |
| `relay failed: Address already in use` | Another process uses the port (`ss -ulpn`). Pick another port, change `ExecStart` and the firewall. |
| `dotnet: not found` / unit fails with 203/EXEC | `ExecStart` dotnet path wrong. `command -v dotnet`, or use `/opt/oricoop-relay/dotnet/dotnet` (4b). |
| Built with runtime only | `dotnet publish` needs the **SDK**. Install `dotnet-sdk-8.0`. |
| Players get "no such room" | Room expired (host left / 30 s silence) or a typo. Codes use `A-Z` without `I O` and digits `2-9`. |
| Room created but joining fails on one network | That player's UDP is blocked (some school / office networks). Nothing to fix on the relay. |
| Names are `-` in the log | No outgoing HTTPS (egress firewall, `IPAddressDeny=` in a drop-in, missing `ca-certificates`) or lookups off. Forwarding still works. |
| `player log: cannot use folder ...` | `LogsDirectory=` missing or the folder is not writable by the service user. |
| Server works, mod cannot connect at all | IPv6-only address, or the human typed a URL like `http://...`. Use `host:port` or `oricoop://host:port`. |
| Steam key visible in `systemctl show` / repo | Keys belong only in `/etc/oricoop-relay.env` (600, root), never in `Environment=` lines or the repo. |

## 11. What to report back to the human

- Public address players should use (`host:port` and the `oricoop://` link), port, install method, paths.
- Firewall changes made (OS + cloud), and anything the human still has to do in the console.
- Probe result from outside, and the service status line.
- Where the player log is, retention, whether a Steam key is configured (yes/no only, never the key).
- How to upgrade / roll back (section 8 commands, with their paths filled in).
