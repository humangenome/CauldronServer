# Changelog

All notable changes to CauldronServer are documented here. This project follows
[Semantic Versioning](https://semver.org/).

## [0.1.19] - 2026-10-21

### Security
- The admin API now checks a signed request's headers before it reads any of the body. A world upload is received only after its signature checks out, and a body of unknown length is capped at 64 KB.
- Public and signed requests have their own request slots (16 public, 6 signed), and one large upload runs at a time, so signed traffic cannot crowd out the health check and the player list.
- `GET /api/v1/chat/recent` needs a signed request.

### Changed
- A world upload over 64 KB must carry `X-Cauldron-Content-Sha256`. Apps before v0.1.19 do not send it and get `428` with "update the Cauldron app to upload worlds to this server". Everything else they do still works.
- RCON `players` lists each player's name and time on the server; `status` says whether the world is running.

### Removed
- `POST /api/v1/chat/player`, a public route nothing called.

## [0.1.18] - 2026-10-16

### Fixed
- Snapshots no longer include the game's own `Backups` folder inside each world folder. Witchspire keeps adding copies of the world there and never removes old ones. On a server running since June that folder held 99,313 files (503 MB), so one snapshot took four minutes and made a 546 MB zip, and every restore and import takes a snapshot first. Snapshots now hold only the world and player saves.
- A restore or import no longer deletes that `Backups` history. It stays where it is, or goes to `Saved\world-backups-kept` when the restored world has no folder for it.
- A restore or import turned automatic snapshots on even with `SnapshotsEnabled` set to `false`. They now come back after a restore only when `SnapshotsEnabled` is `true`.
- The game writing its own backup copies no longer starts an automatic snapshot.

### Security
- MessagePack updated from 3.1.7 to 3.1.10. Versions before 3.1.9 let nested arrays get past buffer length limits ([GHSA-qhrr-8q5h-9q3h](https://github.com/advisories/GHSA-qhrr-8q5h-9q3h)).

## [0.1.17] - 2026-10-15

### Added
- You can host a Witchspire server from this package on your own Windows machine. Before every start the server puts the host mod into the game's `Hercules\Script\Mods\Cauldron` folder with your `GameplayPort`, writes the game's network settings for that port and adds `steam_appid.txt` beside the game. Until now only a hosting panel did those steps, so a server started from the package never opened a world anyone could join. The steps are in the README.
- Closing the server window, or ending CauldronServer any other way, stops the game with it.

### Fixed
- The Cauldron app showed a self-hosted server as offline. The server now keeps the game's process id for its online check.
- Restoring or importing a world through the World window refused every Witchspire 0.2 world, which keeps its files in a folder (`savegame_0\`). Automatic snapshots never ran for the same reason. Both read the folder now, and an automatic snapshot is taken at most every 15 minutes.
- Setting `CauldronAuthPassword` stopped the game every three minutes. Witchspire servers have no join password, so the setting is now ignored with a warning, and server browsers are no longer told one is needed.
- The server read no settings when it was started from another folder.
- The game's own log no longer fills the server window; it stays in `<GameUserDir>\ws-ue.log`.

### Changed
- A player's character is keyed on the first 20 characters of their name, the same as on hosted servers. Names of 20 characters or fewer keep their characters.
- The sample `appsettings.json` names the game, user and save folders (`C:\Cauldron\game`, `C:\Cauldron\userdir`, `C:\Cauldron\saves`).

## [0.1.16] - 2026-10-12

### Changed
- The RCON commands `say`, `announce` and `motd` now answer that nothing was sent. Witchspire has no in-game channel Cauldron can post to, so these used to report success for messages no player could see. They are no longer listed in `help`. The host package is otherwise the same as v0.1.15.

## [0.1.15] - 2026-07-30

### Changed
- Rebuild of the v0.1.14 server package with no functional change. The binaries no longer carry the build machine's directory layout or a debug link back to where they were compiled, and the build now fails if either comes back.

## [0.1.14] - 2026-07-28

### Fixed
- This is the complete host package and the server download to use. Everything up to v0.1.12 published the supervisor build on its own, with no `CauldronServer\` folder and no host mod, so `CauldronServer\CauldronServer.exe` did not exist and a server started from those files supervised Witchspire without ever opening a world anyone could join. v0.1.13 was the first release to carry the real package, but its archive was withdrawn because the host mod inside it still carried internal development notes that should not have been published.
- The sample `appsettings.json` used a default port block belonging to another game, left out the control port so it fell back to an unrelated default, and carried a mods setting that was not wired to anything.

### Changed
- Comments in the host mod and the Engine.ini template were rewritten for readers of this project. Behaviour is unchanged: strip the comments and the code is identical to v0.1.13.
- A release is blocked unless the published package carries the full host layout and none of the project's private material, and both checks run again on the copy downloaded back from this page.

## [0.1.13] - 2026-07-28

### Fixed
- The server download on this page is now the complete host package, and this is the first release you can actually host from. Every release up to v0.1.12 published the supervisor build on its own, with no `CauldronServer\` folder and no host mod, so the `CauldronServer\CauldronServer.exe` in the setup steps did not exist and a server started from those files supervised Witchspire without ever opening a world anyone could join.
- The sample `appsettings.json` used a default port block belonging to another game, left out the control port so it fell back to an unrelated default, and carried a mods setting that was not wired to anything.

### Changed
- A release is blocked unless the published server package carries the full host layout — the supervisor, the host mod, the Engine.ini templates, the Steam app id and the launch helper — and the check runs again on the copy downloaded back from this page.

## [0.1.12] - 2026-07-27

### Added
- Optional host launch prep plugin. A host package can now ship its own prep for the Steam/EOS prerequisites a headless Witchspire host needs; when none is present the server applies the package's `engine-ini/Engine.host.ini` template instead.
- The save-identity script patch is now part of the published server, so a self-hosted server gets per-player character saves on direct-IP joins without a manual step.

### Fixed
- The shipped defaults advertised a map name from a different game, so server browsers and monitors were shown a map that does not exist in Witchspire.
- The live game-process check never matched the real Witchspire process, so a direct-IP host reported itself offline to A2S even while running.

### Changed
- Published server source is now synced from the build repo on every release, so the source on this repo always matches the binary the release ships.

## [0.1.11] - 2026-07-22

### Changed
- Version aligned with the desktop app. No server behavior changes in this release.

## [0.1.10] - 2026-07-05

### Fixed
- The server release package now delivers the Cauldron host mod during install, so a freshly installed host comes up with the mod in place instead of starting without it.

## [0.1.9] - 2026-07-05

### Fixed
- Character identity on direct-IP joins. Two players could previously load into the same character on a hosted server.

## [0.1.8] - 2026-07-04

### Changed
- Version aligned with the desktop app. No server behavior changes in this release.

## [0.1.7] - 2026-06-28

### Changed
- Version aligned with the desktop app. No server behavior changes in this release.

## [0.1.6] - 2026-06-22

### Added
- Live A2S player count: the server now tails the host mod's authoritative roster line and reports the real connected-player list to Source query and the HTTP `/players` endpoint. The empty server reports 0, and the headless host's own slot is excluded from the count.

## [0.1.1] - 2026-06-19

### Fixed
- Stopped the fog-of-war map pass from flooding errors on a headless host.
- Stopped the day/night lighting from dropping players just after they join.

## [0.1.0] - 2026-06-17

### Added
- First public CauldronServer build — host supervisor, Source RCON, A2S query, HTTP admin API, save snapshots, SQLite persistence.
