# Changelog

All notable changes to CauldronServer are documented here. This project follows
[Semantic Versioning](https://semver.org/).

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
