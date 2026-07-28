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

Players join by the server's **address** (`ip:port`). CauldronServer brings the
Witchspire host up as an ordinary Unreal listen server on a real UDP port, so a
join is plain UDP traffic straight to the host. Four ports matter:

- **Gameplay** — UDP, the port players connect to. Mandatory.
- **Query (A2S)** — UDP, for status checks and server lists.
- **RCON** — TCP, for remote console.
- **Admin HTTP API** — TCP, for the Cauldron launcher's world and snapshot tools.

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

Tagged releases (`vX.Y.Z`) build and test this source, then publish the release
page. The release's `Cauldron-Server-Windows-x64-<tag>.zip` is the complete host
package and is built and attached by the Cauldron build, not here: this repo
carries the supervisor source only, so anything it could zip on its own would be
a bare publish output with no `CauldronServer\` folder and no host mod. A
release whose host package never arrives fails the `verify-bundle` job.

## Self-hosting

Download `Cauldron-Server-Windows-x64-<tag>.zip` from the
[latest release](https://github.com/HumanGenome/CauldronServer/releases/latest)
and extract it on the Windows host. It unpacks as `CauldronServer\` (the
supervisor and the `angelscript-mods\cauldron_host` host mod), `engine-ini\`,
`steam_appid.txt` and `host-instance.ps1`. Run `CauldronServer\CauldronServer.exe`.

**Use v0.1.13 or later.** Releases up to v0.1.12 published the supervisor build
on its own — the files unpack flat, there is no `CauldronServer\` folder and no
host mod, and a server started from them never opens a world anyone can join.
Those pages are annotated and their archives were left as they are.

1. Install the Witchspire dedicated server files via SteamCMD (app `2679100`)
   into the game folder configured in `appsettings.json`. CauldronServer launches
   the game from there.
2. Forward and allow the gameplay UDP port, the query UDP port, the RCON TCP
   port, and the admin HTTP TCP port. The gameplay port is mandatory — without
   it players cannot reach the host.
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

Hosting: [SurvivalServers.com](https://www.survivalservers.com/services/game_servers/witchspire/?utm_source=github&utm_medium=readme&utm_campaign=cauldronserver) runs Cauldron for you.

Self-hosting is fully supported from this source.

## License

[MIT](LICENSE) © HumanGenome
