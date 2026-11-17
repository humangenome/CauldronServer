# CauldronServer

The server program, `CauldronServer.exe`. It answers RCON, server-list queries (A2S) and the admin API for one Witchspire server. With `GameInstallRoot` set, it also starts Witchspire with no window (`-nullrhi`) and starts it again if it stops. With `GameInstallRoot` empty, a hosting panel starts and stops the game, and CauldronServer only answers RCON, server lists and the admin API.

The `Mods` settings go inside the `Cauldron` section, not at the top level.
