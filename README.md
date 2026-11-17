# CauldronServer

<p align="center">
  <a href="#build"><img src="https://img.shields.io/badge/Platform-Windows_10%2F11%2FServer-blue.svg" alt="Platform"></a>
  <a href="https://store.steampowered.com/app/2679100/"><img src="https://img.shields.io/badge/Game-Witchspire-darkgreen.svg" alt="Game"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-512BD4.svg" alt=".NET 10"></a>
  <a href="https://github.com/HumanGenome/Cauldron"><img src="https://img.shields.io/badge/Player_App-Cauldron-444.svg" alt="Player App"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-lightgrey.svg" alt="License"></a>
</p>

Witchspire has no dedicated server. [Cauldron](https://github.com/HumanGenome/Cauldron) adds one, and players join it by IP with the Cauldron app. CauldronServer runs your Witchspire server. It starts the game, restarts it if it crashes, takes world backups, and adds RCON and server-list support.

This repo holds the server's source. The Cauldron app and its installer are on [HumanGenome/Cauldron](https://github.com/HumanGenome/Cauldron).

## What it does

### 🛠 RCON
Type admin commands from any Source RCON tool or the Cauldron app's Console tab: `help`, `status`, `players`, `ping`, `save snapshot`, `save list`. Set an RCON password to turn it on.

### 📡 Shows up in server lists
Server lists, monitoring tools and Discord bots can read the server's name, map and live player count.

### 🔁 World backups
`save snapshot` or the Cauldron app's World window backs up the world and every player's character. A restore backs up the current world first, then puts the chosen one in and restarts the game. Backups leave out the extra copies Witchspire keeps in each world's `Backups` folder.

### 💬 In-game chat
Players press T to talk to everyone on the server. Needs the Cauldron app 0.1.22 or newer. It's on by default. To turn it off, add `"Chat": { "InGameEnabled": false }` inside the `Cauldron` section of `appsettings.json`.

### 🔐 Admin API
The Cauldron app's World window uses it to list, download, restore and upload world backups. `/api/v1/health` is public. Everything else needs a request signed with the RCON password, and the API stays off until that password is set.

### ♻️ Crash restarts
With `GameInstallRoot` set, CauldronServer starts Witchspire with no window and starts it again if it stops. Closing the server stops the game with it.

## Ports

| Port | Protocol | Use |
|---|---|---|
| `GameplayPort` (default 17777) | UDP | players join here; open it to the internet |
| `+1` | this PC only | control port |
| `+2` | UDP | server lists (A2S query) |
| `+3` | TCP | RCON |
| `+4` | TCP | admin API |

## Host your own

You need 64-bit Windows 10, 11 or Windows Server with about 10 GB of free disk (the game is about 7 GB) and a copy of Witchspire from your own Steam account. In our test on Windows Server 2022, an empty server used about a quarter of a CPU core and 2 GB of memory. Players add to that.

1. Download `Cauldron-Server-Windows-x64-v<version>.zip` from the [latest release](https://github.com/HumanGenome/CauldronServer/releases/latest) and extract it to `C:\Cauldron`. You get `C:\Cauldron\CauldronServer\CauldronServer.exe` and `C:\Cauldron\engine-ini\`.
2. Put a copy of Witchspire in `C:\Cauldron\game`, separate from your Steam library. Download it with [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD) and a Steam account that owns the game:
   ```
   steamcmd +force_install_dir C:\Cauldron\game +login <your Steam name> +app_update 2679100 validate +quit
   ```
   or copy the folder Steam opens from Witchspire > Manage > Browse local files. The server won't use a game folder inside a Steam library, because the mod it adds would turn that copy into a server the next time you play.
3. Optional: in `C:\Cauldron\CauldronServer\appsettings.json` set `ServerName`, and set `RconPassword` to turn on RCON and the admin API (the app's Console and World window). For other ports, change `GameplayPort` and the four ports after it.
4. Open the ports in Windows Firewall, in an administrator PowerShell:
   ```
   New-NetFirewallRule -DisplayName "Cauldron game" -Direction Inbound -Protocol UDP -LocalPort 17777 -Action Allow
   New-NetFirewallRule -DisplayName "Cauldron query" -Direction Inbound -Protocol UDP -LocalPort 17779 -Action Allow
   New-NetFirewallRule -DisplayName "Cauldron admin" -Direction Inbound -Protocol TCP -LocalPort 17780-17781 -Action Allow
   ```
   Behind a home router, also forward UDP 17777 and 17779 to this machine (and TCP 17780-17781 if you set an admin password).
5. Run `C:\Cauldron\CauldronServer\CauldronServer.exe`. Before every start it puts the server mod into the game with your port, writes the game's network settings and starts Witchspire with no window. The world was open about 15 seconds after the start in our test. The game's log is `C:\Cauldron\userdir\ws-ue.log`. The world is open when it shows `CAULDRON_HOST: ... listen=true`.
6. Players install the [Cauldron app](https://github.com/HumanGenome/Cauldron), add `<your public IP>:17777` and click Connect.

Close the server's window to stop it. The game stops with it. The world is saved in `C:\Cauldron\userdir\Saved\SaveGames\savegame_0` and loads again at the next start. When Witchspire updates, update `C:\Cauldron\game` too: players' games update by themselves, and a server on an older version turns them away. There's no join password, so anyone with the IP and port can join.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

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

A tagged release (`vX.Y.Z`) builds and tests this source and publishes the release page. The server download attached to it (CauldronServer, the `cauldron_host` mod, the Engine.ini templates and the launch helper) is put together by the Cauldron build, because the server mod isn't in this repo. A release whose server download doesn't arrive fails the `verify-bundle` job. Changes are listed in [CHANGELOG.md](CHANGELOG.md).

## Layout

```
src/shared/Cauldron.Protocol       wire types shared with the player app
src/shared/Cauldron.Abstractions   shared interfaces
src/server/Cauldron.SourceQuery    A2S responder
src/server/Cauldron.Rcon           Source RCON server
src/server/Cauldron.Persistence    SQLite store (players, bans, backups, audit log)
src/server/CauldronServer          the server (entry point)
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security issues go through [private reporting](https://github.com/HumanGenome/CauldronServer/security/advisories/new), never a public issue.

Bugs: [open an issue](https://github.com/HumanGenome/CauldronServer/issues).

## Community note

Cauldron is an independent community project. It is not affiliated with, endorsed by, or supported by Envar Games.

## License

MIT, see [LICENSE](LICENSE).

---
[SurvivalServers.com](https://www.survivalservers.com/services/game_servers/witchspire/?utm_source=github&utm_medium=readme&utm_campaign=cauldronserver) runs Witchspire servers with CauldronServer installed.
