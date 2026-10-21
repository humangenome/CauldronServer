# CauldronServer

<p align="center">
  <a href="#build"><img src="https://img.shields.io/badge/Platform-Windows_10%2F11%2FServer-blue.svg" alt="Platform"></a>
  <a href="https://store.steampowered.com/app/2679100/"><img src="https://img.shields.io/badge/Game-Witchspire-darkgreen.svg" alt="Game"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-8.0-512BD4.svg" alt=".NET 8"></a>
  <a href="https://github.com/HumanGenome/Cauldron"><img src="https://img.shields.io/badge/Player_App-Cauldron-444.svg" alt="Player App"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-lightgrey.svg" alt="License"></a>
</p>

Witchspire has no dedicated server. [Cauldron](https://github.com/HumanGenome/Cauldron) adds one: a headless Witchspire host opens the world on a public IP and port, and players join it by address through the Cauldron app. CauldronServer is the supervisor that runs beside that host. It starts the game with the host mod in place, answers Source RCON and A2S queries, serves the admin API the Cauldron app uses for world backups, and takes world snapshots.

This repository is the supervisor's source. The player app and its installer are published from [HumanGenome/Cauldron](https://github.com/HumanGenome/Cauldron).

## What it does

### 🛠 RCON
Source RCON on TCP `GameplayPort + 3` (default 17780). Commands: `help`, `status`, `players`, `ping`, `save snapshot`, `save list`. RCON is off until `RconPassword` is set in `appsettings.json`.

### 📡 Server query
Source A2S on UDP `GameplayPort + 2` (default 17779): the server name, the map (`L_StarterIsland`) and the live player count, which the host mod publishes from the game's own player list.

### 🔁 World snapshots
`save snapshot` (RCON) or the admin API zips the world and player saves under the host's `Saved\SaveGames`, without the `Backups` copies the game keeps inside each world folder. A restore stops the game, takes a snapshot of the current world first, swaps the chosen one in and starts the game again. The Cauldron app's World window drives the same calls.

### 🔐 Admin API
HTTP on TCP `GameplayPort + 4` (default 17781). `/api/v1/health` is public so a panel or the app can poll it; everything else needs a signed request whose key is derived from `RconPassword`, and the API is off while that password is empty.

### ♻️ Game supervision
With `GameInstallRoot` set, the supervisor prepares the game before every start (the host mod with your port in `Hercules\Script\Mods\Cauldron`, the game's network settings for that port, `steam_appid.txt`), launches Witchspire headless (`-nullrhi`) and relaunches it with backoff when it exits. Closing the server stops the game with it.

## Ports

| Port | Protocol | Use |
|---|---|---|
| `GameplayPort` (default 17777) | UDP | players join here; open it to the internet |
| `+1` | local only | supervisor IPC |
| `+2` | UDP | A2S query |
| `+3` | TCP | RCON |
| `+4` | TCP | admin API |

## Self-hosting

You need a 64-bit Windows 10, 11 or Windows Server machine with about 10 GB of free disk (the game itself is about 7 GB), and Witchspire's game files from your own Steam copy (CauldronServer does not ship the game). In our test on Windows Server 2022 an empty server used about a quarter of a CPU core and 2 GB of memory; players add to that.

1. Download `Cauldron-Server-Windows-x64-v<version>.zip` from the [latest release](https://github.com/HumanGenome/CauldronServer/releases/latest) and extract it to `C:\Cauldron`. You get `C:\Cauldron\CauldronServer\CauldronServer.exe` and `C:\Cauldron\engine-ini\`.
2. Put a copy of Witchspire in `C:\Cauldron\game`, separate from your Steam library. Download it with [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD) and a Steam account that owns the game:
   ```
   steamcmd +force_install_dir C:\Cauldron\game +login <your Steam name> +app_update 2679100 validate +quit
   ```
   or copy the folder Steam opens from Witchspire > Manage > Browse local files. The server will not use a game folder inside a Steam library: the host mod it adds would turn that copy into a server the next time you play.
3. Optional: in `C:\Cauldron\CauldronServer\appsettings.json` set `ServerName`, and set `RconPassword` to turn on RCON and the admin API (the app's Console and World window). For other ports, change `GameplayPort` and the four ports after it.
4. Open the ports in Windows Firewall, in an administrator PowerShell:
   ```
   New-NetFirewallRule -DisplayName "Cauldron game" -Direction Inbound -Protocol UDP -LocalPort 17777 -Action Allow
   New-NetFirewallRule -DisplayName "Cauldron query" -Direction Inbound -Protocol UDP -LocalPort 17779 -Action Allow
   New-NetFirewallRule -DisplayName "Cauldron admin" -Direction Inbound -Protocol TCP -LocalPort 17780-17781 -Action Allow
   ```
   Behind a home router, also forward UDP 17777 and 17779 to this machine (and TCP 17780-17781 if you set an admin password).
5. Run `C:\Cauldron\CauldronServer\CauldronServer.exe`. Before every start it puts the host mod into the game with your port, writes the game's network settings and starts Witchspire with no window. The world was open about 15 seconds after the start in our test. The game's log is `C:\Cauldron\userdir\ws-ue.log`; the world is open when it shows `CAULDRON_HOST: ... listen=true`.
6. Players install the [Cauldron app](https://github.com/HumanGenome/Cauldron), add `<your public IP>:17777` and click Connect.

Close the server's window to stop it; the game stops with it. The world is saved in `C:\Cauldron\userdir\Saved\SaveGames\savegame_0` and loads again at the next start. When Witchspire updates, update `C:\Cauldron\game` too: players' games update by themselves, and a server on an older version turns them away. There is no join password; anyone with the address can join.

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
