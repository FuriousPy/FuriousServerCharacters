# Furious Server Characters

A BepInEx plugin for Valheim that keeps player characters on the server, so character progress does not depend on a client's local character file. This project is based on [Smoothbrain's ServerCharacters project](https://github.com/blaxxun-boop/ServerCharacters).

Current version: **1.4.57**

## What it does

The server is the authority for player-character data. When a player connects, the client receives the profile associated with that player's SteamID and character name. When character data is saved, it is sent to and stored by the server under `characters_local`.

This allows players to:

- Use the same character from different clients.
- Recover the latest state received by the server after an unexpected disconnect.
- Retain inventory, skills, food, position, known data, and other information serialized by Valheim.
- Maintain compressed character backups without modifying world-save data.
- Optionally restrict each SteamID/Xbox ID to one character.

## How it works

### Joining a server

1. The client and server negotiate the mod version.
2. The server finds the profile using the `SteamID_characterName` combination.
3. The server sends the complete profile in compressed form.
4. The client loads that profile in memory and Valheim continues its normal connection flow.

If the server has no profile for a character but the client already has existing local character data, the connection is rejected to prevent accidental overwrites. A genuinely new character is created from the configured template.

### Inventory changes and unexpected disconnect protection

Whenever Valheim reports an inventory change, the plugin marks the player's inventory as pending for synchronization.

- The **first change is sent immediately**.
- After that, no more than **one inventory update every 2 seconds** is sent.
- Further changes during those two seconds are grouped together; only the latest complete inventory state is serialized and sent.
- The complete inventory is sent **without compression** to keep client CPU usage and temporary allocations low.
- The server keeps the most recent received inventory for every connected player **in memory**. It does not write the character file to disk for each inventory update.

If a player disconnects unexpectedly, the server combines that in-memory inventory with the latest complete character snapshot or stored character profile, then writes the combined character data to disk. If that save fails, the server retains the recovery data in memory and retries it later.

This means the protection can recover the last inventory update that reached the server. As with any networked system, a change made immediately before a connection loss may not have reached the server yet.

### Full character snapshots

Every **60 seconds** by default, the client creates a complete in-memory character snapshot and sends it to the server in compressed form. The server keeps this snapshot in memory and does not write it to disk at every interval.

During an unexpected disconnect, the server uses the newest full snapshot when one is available, applies the newest cached inventory, and saves the resulting character file.

### Logout and server shutdown

Normal character saves, logout, quit, and the final save requested during server shutdown take priority over the two-second inventory-update interval.

Logout and quit saves are identified separately from ordinary saves. The server stores the final profile but does not send a `ProfileSaved` acknowledgement for `Logout` or `Quit`, because the client connection is already ending. Ordinary saves and server-requested shutdown saves keep the acknowledgement flow used for synchronization and recovery.

When the server is stopped with Ctrl+C, the plugin asks connected clients for one final character save before allowing Valheim's native shutdown to continue. The console immediately reports that this work is in progress, warns when another shutdown request arrives, and reports when control returns to Valheim. If no players are connected and no disconnected character save is pending, native shutdown continues immediately.

### AFK protection

The optional `AFK Kick Timer` applies to clients only; the dedicated server itself is never kicked. When enabled, the client-side monitor samples activity once per second and logs out inactive players after the configured number of minutes. A value of `0` disables the feature, and the accepted range is `0–30` minutes.

Activity is recorded when the player moves in the world, moves the mouse, or uses an input button. Players seated on or standing inside a boat, including a stationary boat, and players lying in a bed are always treated as active. Administrators are subject to the same AFK behavior as other players.

## Default intervals and values

| Setting | Default | Unit | Purpose |
|---|---:|---|---|
| `Auto save interval` | `30` | minutes | Valheim's world and character autosave interval. |
| `Unexpected disconnect protection interval` | `60` | seconds | Interval between full character snapshots kept in server memory. |
| Inventory update interval | `2` | seconds | Internal maximum interval between inventory sends after the initial immediate update. |
| Character backups retained | `2` | files | Maximum backup files kept per character. |
| Minimum backup interval | `30` | minutes | Prevents frequent or identical backup files. |
| `AFK Kick Timer` | `0` | minutes | Disabled by default; `0` disables AFK kicking. |
| `Diagnostic Logging` | `Off` | toggle | Optional `Message`-level transfer diagnostics; payload contents are never logged. |

## Configuration

The configuration file is created at:

```text
BepInEx/config/furiousservercharacters.cfg
```

### `1 - General`

- `Lock Configuration`: locks synchronized settings so they can only be managed by the server.
- `AFK Kick Timer`: logs out inactive clients after the configured number of minutes. Allowed range: 0–30 minutes; `0` disables it. World-position movement, mouse movement, input buttons, being on or inside a boat, and lying in a bed count as activity. The dedicated server is excluded, but administrators are not exempt.
- `Login Message`: global message shown when a player joins. Leave empty to disable it.
- `Diagnostic Logging`: when enabled, logs character transfer direction, event, peer, revision, size, and compression at `Message` level. Character contents are never logged.

### `2 - Save Files`

- `Hardcore mode`: after death, disconnects the player and deletes the server-side character profile according to hardcore-mode rules.
- `Single Character Mode`: limits each SteamID/Xbox ID to one character. It does not affect administrators.
- `Backup only mode`: stops enforcing the server profile and uses the mod for backups only. Enable it only when its behavior is fully understood.
- `Auto save interval`: changes Valheim's autosave interval. Allowed range: 1–120 minutes.
- `Unexpected disconnect protection interval`: changes the full in-memory snapshot interval. Allowed range: 15–300 seconds.
- `Store poison debuff`: stores and restores Poison when character data is saved and loaded.

### `3 - First Login`

- `First Login Message`: message displayed when a character joins for the first time.
- `Intro`: controls the Valkyrie introduction.

### `4 - Other`

- `Server key`: internal key used to authenticate emergency character backups. Do not share or edit it manually.

## Buffs and special character data

Valheim's native character profile retains its own character data, including skills and the post-death no-skill-drain protection. This plugin also explicitly persists:

- Finite-duration potion effects and player-activated buffs, preserving their remaining duration. This includes `Rested`, `Poison` when `Store poison debuff` is enabled, and stateful effects such as Protection Staff shields with their remaining absorption.

Legacy Rested and Poison fields and the `1.4.56` buff snapshot format are migrated automatically when a character loads. New saves write only the unified, versioned buff snapshot.

Effects recalculated from the world, equipped items, weather, shelter, encumbrance, or death state are not restored.

## Server files

Character profiles are stored in:

```text
<Valheim dedicated server>/characters_local/
```

Character backups are stored in:

```text
<Valheim dedicated server>/characters_local/backups/
```

At most two backups are kept per character. Backups use a temporary file and safe replacement process to avoid leaving partially written ZIP archives.

## World-save safety

The plugin does not expose a TCP interface, does not depend on Discord, and does not run external administrative commands. Character persistence is kept separate from the world save:

- Periodic snapshots are kept in memory.
- Character backups read character files, not world chunks.
- Profile errors are logged and retried without propagating exceptions to the server's main thread.
- Shutdown allows Valheim to run its native world-save process.

As with any persistence plugin, the dedicated server should always run under the same account that has read and write permissions for its world and character folders.

## Requirements

- A Valheim version compatible with the compiled plugin.
- BepInEx for Valheim.
- ServerSync and HarmonyX, provided through the BepInEx mod environment.
- The same mod version installed on both the server and every client.

## Building

The project targets .NET Framework 4.8 and uses the locally installed Valheim assemblies. Build through Visual Studio or MSBuild:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe' `
  ServerCharacters.csproj /t:Build /p:Configuration=Release /p:SkipDeploy=true
```

The resulting DLL is written to `bin/Release/FuriousServerCharacters.dll`. `SkipDeploy=true` prevents the build from copying it into the game installation automatically.

## Credits and license

This project is an adaptation and continuation of the work by **Smoothbrain** / **blaxxun-boop**:

- [Original GitHub repository](https://github.com/blaxxun-boop/ServerCharacters)

Review the original project's license and notices before redistributing modified builds.
