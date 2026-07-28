#!/usr/bin/env python3
"""Verify a Cauldron server host package before it is published.

The host package is the self-hoster artifact. It is NOT a bare `dotnet publish`
output: it carries the supervisor under `CauldronServer/`, the AngelScript host
mod that actually opens the world as a listen server, the Engine.ini templates,
the Steam app id and the host launch helper.

A zip holding only the supervisor publish output is a valid, internally
consistent, correctly checksummed zip -- and a completely broken host. It has no
`CauldronServer\\` folder to run the exe from and no `cauldron_host` mod, so the
process comes up, answers query, and never opens a joinable world.

    python scripts/verify-server-bundle.py <package.zip>

Exit 0 only when the zip is a complete host package. Any other outcome exits 1,
including anything this script cannot positively confirm.

WHY THERE IS NO SIZE FLOOR
--------------------------
Do not add one. On Cauldron a size floor is worse than useless: the CORRECT
v0.1.12 package is 47,096,779 bytes and the BROKEN supervisor-only zip published
under the same asset name is 48,545,247 bytes. The good artifact is 1.4 MB
SMALLER than the bad one, because v0.1.12 dropped an inert `ue4ss/` pack. Any
floor that rejects the bad zip also rejects the good one.

The general lesson, learned the expensive way on the sibling project: guards in
this family must assert LAYOUT, not size and not a checksum. Every bad artifact
was internally consistent and correctly checksummed. It was simply the wrong
thing.
"""

import argparse
import os
import sys
import zipfile

# Every one of these must be present and non-empty. The angelscript-mods entries
# are what makes the difference between a supervisor babysitting a process and a
# host that opens a world players can join.
REQUIRED_ENTRIES = (
    "CauldronServer/CauldronServer.exe",
    "CauldronServer/CauldronServer.dll",
    "CauldronServer/CauldronServer.runtimeconfig.json",
    "CauldronServer/appsettings.json",
    "CauldronServer/angelscript-mods/cauldron_host/CauldronHost.as",
    "CauldronServer/angelscript-mods/cauldron_host/CauldronClient.as",
    "engine-ini/Engine.host.ini",
    "engine-ini/Engine.client.ini",
    "steam_appid.txt",
    "host-instance.ps1",
)

# The host mod is ~35 KB of AngelScript. A stub or a truncated copy is not a mod.
MIN_HOST_MOD_BYTES = 10_000

# The launch helper is ~15 KB of PowerShell.
MIN_HOST_SCRIPT_BYTES = 2_000

# The self-contained supervisor publish output is ~355 files. Well under that
# means the supervisor tree itself is incomplete.
MIN_SUPERVISOR_ENTRIES = 300

# Publish output that ended up at the zip root instead of under CauldronServer/
# is the exact signature of the supervisor-only artifact.
FLAT_ROOT_MARKERS = (
    "cauldronserver.exe",
    "cauldronserver.dll",
    "hostfxr.dll",
    "coreclr.dll",
)


def normalise(name):
    return name.replace("\\", "/").lstrip("./")


def verify(path):
    failures = []

    if not os.path.isfile(path):
        return ["not a file: {}".format(path)]

    try:
        zf = zipfile.ZipFile(path)
    except Exception as exc:  # noqa: BLE001 - fail closed on anything
        return ["cannot open as a zip: {}".format(exc)]

    with zf:
        bad = zf.testzip()
        if bad is not None:
            failures.append("corrupt entry: {}".format(bad))

        sizes = {}
        for info in zf.infolist():
            name = normalise(info.filename)
            if name.endswith("/"):
                continue
            sizes[name.lower()] = info.file_size

        for entry in REQUIRED_ENTRIES:
            key = entry.lower()
            if key not in sizes:
                failures.append("missing required entry: {}".format(entry))
            elif sizes[key] == 0:
                failures.append("required entry is empty: {}".format(entry))

        for marker in FLAT_ROOT_MARKERS:
            if marker in sizes:
                failures.append(
                    "'{}' sits at the zip root -- this is a bare publish output, "
                    "not the host package (the supervisor belongs under "
                    "CauldronServer/)".format(marker)
                )

        host_mod = sizes.get(
            "cauldronserver/angelscript-mods/cauldron_host/cauldronhost.as"
        )
        if host_mod is not None and host_mod < MIN_HOST_MOD_BYTES:
            failures.append(
                "CauldronHost.as is {:,} bytes, below the {:,} byte floor".format(
                    host_mod, MIN_HOST_MOD_BYTES
                )
            )

        host_script = sizes.get("host-instance.ps1")
        if host_script is not None and host_script < MIN_HOST_SCRIPT_BYTES:
            failures.append(
                "host-instance.ps1 is {:,} bytes, below the {:,} byte floor".format(
                    host_script, MIN_HOST_SCRIPT_BYTES
                )
            )

        supervisor = [n for n in sizes if n.startswith("cauldronserver/")]
        if len(supervisor) < MIN_SUPERVISOR_ENTRIES:
            failures.append(
                "only {} files under CauldronServer/ (expected at least {}) -- "
                "the supervisor publish output is incomplete".format(
                    len(supervisor), MIN_SUPERVISOR_ENTRIES
                )
            )

        # The panel-side installer accepts the mod under either directory name,
        # so accept either here too, but require that one of them is real.
        mod_dirs = set()
        prefix = "cauldronserver/angelscript-mods/"
        for name in sizes:
            if name.startswith(prefix):
                rest = name[len(prefix):]
                if "/" in rest:
                    mod_dirs.add(rest.split("/", 1)[0])
        if not mod_dirs & {"cauldron_host", "cauldron"}:
            failures.append(
                "no host mod directory under CauldronServer/angelscript-mods/ "
                "(want cauldron_host/): {}".format(sorted(mod_dirs) or "none")
            )

    return failures


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("zip", help="path to the server host package zip")
    args = parser.parse_args(argv)

    try:
        failures = verify(args.zip)
    except Exception as exc:  # noqa: BLE001 - never pass on an unexpected error
        print("FAIL {}: unexpected error: {}".format(args.zip, exc))
        return 1

    if failures:
        print("FAIL {} is not a complete Cauldron host package:".format(args.zip))
        for line in failures:
            print("  - {}".format(line))
        print(
            "\nDo not publish this artifact. The host package carries the "
            "supervisor under CauldronServer/ alongside the cauldron_host "
            "AngelScript mod, the Engine.ini templates and the host launch "
            "helper. A supervisor-only zip supervises a process that never "
            "opens a joinable world."
        )
        return 1

    print(
        "OK {} is a complete host package ({:,} bytes)".format(
            args.zip, os.path.getsize(args.zip)
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
