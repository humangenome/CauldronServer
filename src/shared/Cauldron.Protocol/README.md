# Cauldron.Protocol

Wire protocol shared by CauldronServer, the Cauldron app and the native plugin:
the frame codec and MessagePack message records.

The wire format is a length-prefixed frame codec carrying MessagePack records;
payloads track the Witchspire surface. The generated protocol code is gitignored
(see `.gitignore` -> `src/shared/Cauldron.Protocol/Generated/`).
