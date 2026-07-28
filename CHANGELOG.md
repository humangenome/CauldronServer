# Changelog

All notable changes to CauldronServer are documented here. This project follows
[Semantic Versioning](https://semver.org/).

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
