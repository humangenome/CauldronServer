# Cauldron.Protocol

Wire protocol shared by CauldronServer, the launcher, and the native plugin:
the frame codec and MessagePack message records.

PORT FROM BEACON: copy `Beacon.Protocol` (FrameCodec + Protocol records) and
rename the namespace to `Cauldron.Protocol`. The wire format ports verbatim;
adjust message payloads only where the Witchspire surface differs from
Subnautica 2. The generated protocol code is gitignored (see `.gitignore` ->
`src/shared/Cauldron.Protocol/Generated/`).
