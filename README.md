# CauldronServer

<p align="center">
  <a href="#build"><img src="https://img.shields.io/badge/Platform-Windows_10%2F11%2FServer-blue.svg" alt="Platform"></a>
  <a href="https://store.steampowered.com/app/2679100/"><img src="https://img.shields.io/badge/Game-Witchspire-darkgreen.svg" alt="Game"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-8.0-512BD4.svg" alt=".NET 8"></a>
  <a href="https://github.com/HumanGenome/Cauldron"><img src="https://img.shields.io/badge/Player_App-Cauldron-444.svg" alt="Player App"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-lightgrey.svg" alt="License"></a>
</p>

Witchspire has no dedicated server. [Cauldron](https://github.com/HumanGenome/Cauldron) adds one: a headless Witchspire host opens the world on a public IP and port, and players join it by address through the Cauldron app. CauldronServer is the supervisor that runs beside that host. It answers Source RCON and A2S queries, serves the admin API the Cauldron app uses for world backups, takes world snapshots, and can launch and restart the game.

This repository is the supervisor's source. The player app and its installer are published from [HumanGenome/Cauldron](https://github.com/HumanGenome/Cauldron).

## What it does

### 🛠 RCON
Source RCON on TCP `GameplayPort + 3` (default 17780). Commands: `help`, `status`, `players`, `ping`, `save snapshot`, `save list`. RCON is off until `RconPassword` is set in `appsettings.json`.

### 📡 Server query
Source A2S on UDP `GameplayPort + 2` (default 17779): the server name, the map (`L_StarterIsland`) and the live player count, which the host mod publishes from the game's own player list.

### 🔁 World snapshots
`save snapshot` (RCON) or the admin API zips everything under the host's `Saved\SaveGames`. A restore stops the game, takes a snapshot of the current world first, swaps the chosen one in and starts the game again. The Cauldron app's World window drives the same calls.

### 🔐 Admin API
HTTP on TCP `GameplayPort + 4` (default 17781). `/api/v1/health` is public so a panel or the app can poll it; everything else needs a signed request whose key is derived from `RconPassword`, and the API is off while that password is empty.

### ♻️ Game supervision
With `GameInstallRoot` set, the supervisor launches Witchspire headless (`-nullrhi`) and relaunches it with backoff when it exits.

## Ports

| Port | Protocol | Use |
|---|---|---|
| `GameplayPort` (default 17777) | UDP | players join here; open it to the internet |
| `+1` | local only | supervisor IPC |
| `+2` | UDP | A2S query |
| `+3` | TCP | RCON |
| `+4` | TCP | admin API |

## Self-hosting

**Self-hosting is not turnkey yet.** The package does not copy the host mod (`CauldronServer\angelscript-mods\cauldron_host`) into your Witchspire install's `Hercules\Script\Mods` folder, does not set the host mod's port, and does not write the per-server Engine.ini with your gameplay port. Without those steps a server started from the package opens no world players can join.

What you need either way: a Windows machine, and Witchspire's game files on it from your own Steam copy (CauldronServer does not ship the game). Use the host package from v0.1.14 or later (`Cauldron-Server-Windows-x64-v<version>.zip` on the [latest release](https://github.com/HumanGenome/CauldronServer/releases/latest)); earlier releases published the supervisor build on its own, without the host mod.

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet restore CauldronServer.sln
dotnet build CauldronServer.sln -c Release
dotnet test  CauldronServer.sln -c Release
```

Publish a self-contained Windows build:

```bash
dotnet publish src/server/CauldronServer/CauldronServer.csproj \
  -c Release -r win-x64 --self-contained true
```

A tagged release (`vX.Y.Z`) builds and tests this source and publishes the release page. The host package attached to it (the supervisor, the `cauldron_host` mod, the Engine.ini templates and the launch helper) is assembled by the Cauldron build, because the host mod is not part of this repository. A release whose host package does not arrive fails the `verify-bundle` job. Changes are listed in [CHANGELOG.md](CHANGELOG.md).

## Layout

```
src/shared/Cauldron.Protocol       wire types shared with the player app
src/shared/Cauldron.Abstractions   shared interfaces
src/server/Cauldron.SourceQuery    A2S responder
src/server/Cauldron.Rcon           Source RCON server
src/server/Cauldron.Persistence    SQLite store (snapshots, bans, schedule, audit)
src/server/CauldronServer          the supervisor (entry point)
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security issues go through [private reporting](https://github.com/HumanGenome/CauldronServer/security/advisories/new), never a public issue.

## Community note

Cauldron is an independent community project. It is not affiliated with, endorsed by, or supported by Envar Games.

## License

MIT, see [LICENSE](LICENSE).

---
Hosting: [SurvivalServers.com](https://www.survivalservers.com/services/game_servers/witchspire/?utm_source=github&utm_medium=readme&utm_campaign=cauldronserver) runs Cauldron for you.
