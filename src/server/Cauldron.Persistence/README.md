# Cauldron.Persistence

SQLite-backed persistence (CauldronDb): bans, scheduler, audit, character store,
save-snapshot bookkeeping.

PORT FROM BEACON: copy `Beacon.Persistence` (SQLite/Dapper, the BeaconDb schema)
and rename. Schema ports verbatim; add Witchspire-specific tables only as the
feature set diverges.
