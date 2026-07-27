# CauldronServer

The per-instance sidecar — `CauldronServer.exe`. Runs Source RCON, Source A2S
query, and the loopback HTTP admin API for one Witchspire host. It does NOT own
the game-process lifecycle and there is NO display head / menu-drive: in a
managed deploy the host's launch script starts the per-customer exe directly
(CREATE_SUSPENDED), pins CPU affinity before resume, and stops it; `GameInstallRoot`
stays empty. The game hosts headless via `-nullrhi` + the Engine.ini
`LocalMapOptions=?listen` config — no GPU and no virtual display needed.

The host's launch script owns the game-process lifecycle and CauldronServer's
internal supervisor stays idle on an EMPTY `GameInstallRoot`. That script launches
the per-customer exe directly (CREATE_SUSPENDED with the `-nullrhi` no-GPU args),
pins CPU affinity before resume, reaps `CrashReportClient`/`WerFault`, and
restarts. CauldronServer only serves RCON / Source A2S query / the loopback HTTP
admin API for that instance — it does NOT launch, pin, or relaunch the game.

The `Mods` settings nest UNDER the `Cauldron` config section, not at the top
level.
