# Relay player log (operators)

The relay server writes one line when a player creates or joins a room and one when they leave.

```
PLAYER JOIN 2026-11-16T03:21:07Z room=K7QX2M role=guest steamid=76561198000000001 ip=203.0.113.7:51234 name="Example" client_name="Example" profile=https://steamcommunity.com/profiles/76561198000000001 seat=2
PLAYER LEAVE 2026-11-16T03:58:40Z room=K7QX2M role=guest steamid=76561198000000001 ip=203.0.113.7:51234 name="Example" client_name="Example" profile=https://steamcommunity.com/profiles/76561198000000001 duration=00:37:33 reason="left" seat=2
```

- Time is UTC. `role` is `host` or `guest`; `seat` is 1 for the host and 2-4 for guests (relay protocol v2). `ip` is the UDP source address the relay saw (after NAT).
- `steamid`: the SteamID64 the client sent. `0` means Steam was not running on that PC; `unknown` means a client
  that sends no optional identity. **The client reports its SteamID itself, so it can be forged.** Treat it as a hint, not proof.
- `name`: the Steam name the server looked up for that ID (`-` if the lookup failed or is off). `client_name`: the name
  the client sent (its Steam name, or `ORICOOP_PLAYER_NAME`). Names are cut to 64 characters; quotes and control
  characters become `?`.
- `profile`: `https://steamcommunity.com/profiles/<SteamID64>`.
- LEAVE `reason`: `left`, `timed out`, `silent guest replaced`, `host timed out`, `host requested a new room`,
  `room closed (...)`.

## Wire format

Clients append an identity block to `HostRoom` (after byte 7) and `JoinRoom` (after byte 9):
`'I'` (0x49), version 1, SteamID64 (8 bytes, little endian), name length (1 byte, at most 32), UTF-8 name.
Relay protocol v2 uses magic `0x534F` (wire bytes `4F 53`); the identity version stays 1.
V2 clients may omit identity and are logged as `unknown`. V1 packets are refused with
`relay protocol v2 required`. The complete v2 packet table is at the top of `server/RelayServer.cs`.

## Steam name lookup

Lookups run on worker threads and never block forwarding. Results are cached 24 h (failures 10 min), at most 8
lookups at a time. Order:
1. Steam Web API `ISteamUser/GetPlayerSummaries/v2` if a key is set.
2. `https://steamcommunity.com/profiles/<id>?xml=1` (`<steamID>`).
3. Otherwise only the ID and the profile link are logged.

## Settings (environment)

| Variable | Default | Meaning |
|---|---|---|
| `ORICOOP_PLAYER_LOG_DIR` | systemd `LOGS_DIRECTORY`, else none | Folder for `players-YYYY-MM-DD.log` (one file per UTC day). None = stdout only. |
| `ORICOOP_PLAYER_LOG_DAYS` | 30 | Day files older than this are deleted (at start and at each day change). |
| `ORICOOP_PLAYER_LOG_STDOUT` | 1 | `0` = player lines only in the files, not in stdout/journald. |
| `STEAM_WEB_API_KEY` | none | Steam Web API key. Never put it in the repo; it is never logged. |
| `ORICOOP_STEAM_API_KEY_FILE` | none | Or read the key from this file. |
| `ORICOOP_PLAYER_NAME_LOOKUP` | 1 | `0` = no Steam name lookups (no outgoing HTTP). |

## Retention and privacy

- Logs stay on the relay server only. Default retention is **30 days** for the day files.
- journald keeps stdout under its own limits. Either set `ORICOOP_PLAYER_LOG_STDOUT=0`, or limit journald (for example
  `MaxRetentionSec=30day` in `/etc/systemd/journald.conf`).
- The OriCoop mod tells players about this in its README. If you run a public relay, tell your players too.

## systemd example

Full template: [`deploy/oricoop-relay.service`](../deploy/oricoop-relay.service). The two lines that matter here
(systemd does not allow comments at the end of a line, so they are above):

```ini
[Service]
ExecStart=<dotnet> <app-dir>/RelayServer.dll 47020
DynamicUser=yes
# -> /var/log/oricoop-relay, passed to the server as LOGS_DIRECTORY
LogsDirectory=oricoop-relay
# root-only (chmod 600): STEAM_WEB_API_KEY=..., ORICOOP_PLAYER_LOG_STDOUT=0
EnvironmentFile=-/etc/oricoop-relay.env
```

Read the log: `tail -f /var/log/oricoop-relay/players-$(date -u +%F).log` or `journalctl -u oricoop-relay | grep PLAYER`.
