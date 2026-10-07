# Changelog

## 1.4.57

- Store Rested, Poison, potion effects, and player-activated buffs in one versioned snapshot.
- Automatically migrate legacy Rested and Poison fields and the `1.4.56` snapshot format when a character loads.
- Write only the unified format after migration while preserving support for older character saves.
- Restore cloned effects without destroying their shared `ObjectDB` definitions, preserving both restored and environment-applied effects.
- Print clear console notices when Ctrl+C starts, repeats, and finishes the coordinated character-save phase.
- Defer Valheim's native `OnApplicationQuit` teardown until final character saves finish, so networking and builders shut down only once.
- After persisting final profiles, notify clients, wait briefly for acknowledgement, and send Valheim's native `Disconnect` RPC while networking is still active; clients accept it without a redundant profile save.
- Normalize the client connection state after a confirmed server shutdown so Valheim does not present the expected native disconnect as a connection error.

## 1.4.56

- Preserve finite-duration potion effects and player-activated buffs across logout and reconnect.
- Preserve remaining Protection Staff shield absorption and gradual potion progress without replaying one-shot healing.
- Exclude environmental, equipment-derived, encumbrance, and death-related effects that Valheim recalculates.
- Consume saved buff snapshots once and discard them on death or when malformed.

## 1.4.55

- Exempt players lying in a bed from AFK disconnection.

## 1.4.54

- Discard obsolete emergency backups only after an explicit server conflict notice and successful loading of the matching authoritative profile.
- Preserve emergency backups when signatures, server data, or recovery validation fail.

## 1.4.53

- Mark final logout and quit profiles with dedicated RPC events and omit their save acknowledgements.
- Normal saves and server-requested shutdown saves retain acknowledgements.

## 1.4.52

- Skip profile-save acknowledgements when the client connection has already closed.

## 1.4.51

- Players seated on or standing inside a boat are exempt from AFK kicking even when the boat does not move.

## 1.4.50

- AFK detection now samples world-position and mouse movement once per second.
- Players are no longer considered AFK merely because their boat is moving while they are seated.

## 1.4.49

- Added optional Message-level diagnostics for character transfer sends and receives.
- Diagnostics are disabled by default and never include character contents.

## 1.4.48

- Rebranded the BepInEx plugin identifier, visible name, assembly, and package DLL to `FuriousServerCharacters`.
- Declares the original Server Characters plugin as incompatible so both implementations cannot load together.

## 1.4.47

- Thunderstore repackaging release with the same tested 1.4.46 feature set.

## 1.4.46

- Initial Thunderstore package for the Valheim 1.0-compatible release.
- Server-authoritative character persistence with full-profile recovery snapshots.
- Inventory change synchronization is limited to one update every two seconds after the first immediate update.
- Includes unexpected-disconnect recovery, safe character backups, Rested persistence, and controlled server shutdown saves.
