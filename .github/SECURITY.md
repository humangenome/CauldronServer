# Security Policy

## Reporting a vulnerability

If you have found a security issue in Cauldron (the server, the launcher, the host mods, or the admin API), please **do not** open a public GitHub issue.

Report it privately through GitHub's security advisory form:

- Launcher and hub: https://github.com/HumanGenome/Cauldron/security/advisories/new
- Server: https://github.com/HumanGenome/CauldronServer/security/advisories/new

Include:

- A description of the vulnerability
- Steps to reproduce
- Affected component (server / launcher / host mod / admin API)
- Cauldron version (the release tag, e.g. `v0.1.12`)
- Whether the issue is currently being exploited

Reports are acknowledged within 72 hours and triaged within 7 days.

## Scope

In scope:

- Remote code execution or unauthenticated takeover of `CauldronServer.exe`
- Authentication bypass on the admin HTTP API or RCON
- Injection through the named-pipe control channel between the launcher and the server
- A connected client being able to write arbitrary files on the host
- Privilege escalation through the host mod or a host launch prep plugin

Out of scope:

- Vulnerabilities in the machine your server runs on — those belong to whoever operates it
- Vulnerabilities in retail Witchspire itself — report those to the game's publisher
- Vulnerabilities in third-party mods running alongside Cauldron
- Cheating and anti-cheat concerns; Cauldron does not provide anti-cheat
