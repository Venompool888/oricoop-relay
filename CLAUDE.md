# CLAUDE.md

Read [AGENTS.md](AGENTS.md) before doing anything in this repo. It is the deploy guide for AI agents: what to ask the
human first, which steps need their confirmation, the install / firewall / verify / upgrade steps, and common mistakes.

Working on the code itself: the server is `server/RelayServer.cs` + `server/RelayPlayerLog.cs` (C# 5 compatible, so
the same files build with .NET Framework `csc` on Windows and with .NET 8 on Linux). Run `build-relay.ps1` on Windows
for the tests (`tests/RelayTest.cs`). Never commit real server addresses, host names or keys; use `203.0.113.10` /
`relay.example.com` in docs.
