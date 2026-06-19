# CauldronServer

[![Platform](https://img.shields.io/badge/Platform-Windows_10%2F11-blue.svg)](#build)
[![Game](https://img.shields.io/badge/Game-Witchspire-darkgreen.svg)](https://store.steampowered.com/app/2679100/)
[![Runtime](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Client](https://img.shields.io/badge/Client_App-Cauldron-brightgreen.svg)](https://github.com/HumanGenome/Cauldron)

**CauldronServer** is the dedicated-server supervisor behind **Cauldron**, the
hosting stack that gives [Witchspire](https://store.steampowered.com/app/2679100/)
reliable, panel-manageable multiplayer servers. It wraps the Witchspire dedicated
server with the operational plumbing a real host needs: process supervision,
crash recovery, server query, RCON, persistence, and a local admin API the
Cauldron launcher drives.

This repository is the **server source**. The player-facing launcher and the
packaged installer are distributed from [HumanGenome/Cauldron](https://github.com/HumanGenome/Cauldron).

## What it does

- **Process supervisor + watchdog** — launches the Witchspire dedicated server,
  watches its heartbeat, and recovers it on crash or hang.
- **Source Query (A2S)** — answers A2S so the server is visible to the hosting
  panel's status checks.
- **Source RCON** — standard Source RCON for remote console and admin commands.
- **Persistence** — SQLite-backed bans, scheduled tasks, and an audit log.
- **Local admin API** — a loopback-only control plane the Cauldron launcher uses
  to start/stop, configure, and query the server.

## How players join

Witchspire is not a direct-IP product. Players join through the game's own
session transport using a short **join code** surfaced by the host. There is no
gameplay UDP port to forward — only the operational ports below need to be open:

- **Query (A2S)** — UDP, for status checks.
- **RCON** — TCP, for remote console.
- **Admin HTTP API** — TCP, loopback-only by default for the launcher.

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet restore CauldronServer.sln
dotnet build CauldronServer.sln -c Release
dotnet test  CauldronServer.sln -c Release
```

Publish a self-contained Windows build (what releases ship):

```bash
dotnet publish src/server/CauldronServer/CauldronServer.csproj \
  -c Release -r win-x64 --self-contained true
```

Tagged releases (`vX.Y.Z`) build, test, publish, and attach
`Cauldron-Server-Windows-x64-<tag>.zip` automatically via GitHub Actions.

## Self-hosting

1. Install the Witchspire dedicated server files via SteamCMD (app `2679100`)
   into the game folder configured in `appsettings.json`. CauldronServer launches
   the game from there.
2. Open the query UDP port, the RCON TCP port, and the admin HTTP TCP port.
   Joins ride the game session transport, so there is no gameplay UDP port to
   forward.
3. Run CauldronServer; it supervises the game process, answers A2S/RCON, serves
   the admin API, and keeps save snapshots.

## Layout

```
src/shared/Cauldron.Protocol       wire types shared with the launcher
src/shared/Cauldron.Abstractions   shared interfaces
src/server/Cauldron.SourceQuery    A2S responder
src/server/Cauldron.Rcon           Source RCON server
src/server/Cauldron.Persistence    SQLite store (bans/schedule/audit)
src/server/CauldronServer          the supervisor host (entry point)
```

## Official hosting

CauldronServer is officially supported by
[SurvivalServers.com](https://www.survivalservers.com/services/game_servers/witchspire/?utm_source=github&utm_medium=release&utm_campaign=cauldronserver) —
managed Witchspire hosting with Cauldron pre-installed and kept on the latest
pinned release. Self-hosting is fully supported from this source.

## License

[MIT](LICENSE) © HumanGenome
