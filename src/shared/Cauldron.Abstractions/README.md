# Cauldron.Abstractions

Shared service interfaces (`ICauldronService` and friends) implemented by
CauldronServer and consumed by the tools/launcher.

PORT FROM BEACON: copy `Beacon.Abstractions` (the `IBeaconService` interfaces)
and rename to `Cauldron.Abstractions`. These are interface-only and port
verbatim.
