# Contributing to CauldronServer

This repo holds the dedicated server's source. Issues with the Cauldron app go on
https://github.com/HumanGenome/Cauldron.

## Reporting bugs

Open an issue using the **Bug report** template. Include:

- Cauldron version (the release tag, such as `v0.1.26`, and which download: `CauldronSetup-<version>.exe` or `Cauldron-Server-Windows-x64-v<version>.zip`)
- Witchspire build ID and your client's exe SHA256 if known
- Steps to reproduce
- Server log excerpt (`logs/cauldron-*.log`, beside `CauldronServer.exe`) and the game's `ws-ue.log`
- Whether anyone else can reproduce on a clean server

If your issue is about a hosted server you rent (panel, billing, support), contact your host directly. Cauldron's GitHub issues are for the open-source server, app and mods themselves.

## Feature requests

Open an issue using the **Feature request** template. Describe the use case, not the implementation. If you're proposing a change to how the app and server talk, link the file in CauldronServer's `src/shared/Cauldron.Protocol` you'd change.

## Pull requests

Project doesn't accept code PRs during pre-alpha. Once we hit alpha:

- Branch from `main`, name `feat/<short-slug>` or `fix/<short-slug>`
- Keep commits short and focused: one change per commit
- Match existing code style (`.editorconfig` comes with the alpha tag)
- For new dependencies, justify the addition in the PR description and pin the version in `Directory.Packages.props`
- Run the test suite locally before opening the PR

## Code of conduct

Be civil. Be technical. Don't post game-piracy or anti-cheat-evasion material in issues or PRs.
