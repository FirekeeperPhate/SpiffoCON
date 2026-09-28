# SpiffoCON

Remote admin console for Project Zomboid (Build 42) dedicated servers, over RCON.
Windows, .NET 10, WPF.

## Features

- RCON client written for PZ: replies matched by request id (a late reply is never shown
  for the next command), long replies in one or several packets, automatic reconnect
  before the next command after a drop, UTF-8.
- Console with history (Up/Down); a leading `/` is stripped.
- Broadcast editor for `servermsg`: colors (`<RGB:r,g,b>`), multi-line (`<LINE>`),
  live preview, safe quoting (inner `"` become `'`), and a reply check that does not
  report a delivered message as failed.
- Catalog of items and vehicles, **mods included**: search, filter by kind and by mod,
  mod item icons, give items (`additem`) and spawn vehicles (`addvehicle`) for an online
  player.
  - The base game comes from a bundled snapshot (`src/SpiffoCON.Core/Data`), so no game
    install is needed.
  - The server's mods come from `showoptions` (`Mods=`, `WorkshopItems=`), in its load
    order. Mod files are read from, in this order: the server over SFTP (only scripts,
    translations and icons are copied), the local Steam workshop folder, SteamCMD
    (anonymous download of whole mods, asked first, cached in
    `%LOCALAPPDATA%\SpiffoCON`).
  - B42 layout (`common` + the newest `42.x` folder), JSON and legacy translation files,
    vehicle names through `carModelName` / `template!`, mods that change base-game items.
- Players tab: online list (auto refresh), kick and ban (with reason, optional IP ban),
  unban, voice mute, access level, teleport to a player or to coordinates, god mode,
  invisibility, no clip, XP per skill. Command syntax comes from the B42 server's
  `@CommandArgs` annotations, and replies were checked against a real B42 dedicated
  server (access levels are the lowercase B42 roles: user, priority, observer, gm,
  moderator, admin).
- Options tab: all 144 server options grouped like the game's settings screen, with
  descriptions, defaults and ranges (dumped from a real B42 server by
  `tools/DumpServerOptions.lua`, built by `tools/MakeServerOptions.cs`). Values are
  validated before `changeoption` and each reply is checked, since the server silently
  keeps the old value on bad input. ResetID, ServerPlayerID and Seed have no default and
  ask before changing.
- Sandbox tab: the 269 sandbox options, edited in the server's `<name>_SandboxVars.lua`
  over SFTP or in a local copy. Only the changed values are rewritten (comments, order,
  line endings and unknown mod settings stay), a backup is kept, and the server applies
  the file at its next start (checked on a real B42 server, also on an existing world).
- SFTP probe (optional): looks for `Server/*.ini`, workshop folders
  (`content/108600`), `Zomboid/mods` and logs. The SSH host key is saved at the first
  connection and checked afterwards.
- The profile lives in `%APPDATA%\SpiffoCON\profile.json`; passwords are encrypted with
  DPAPI (current Windows user).

## Planned

- Chat log tail over SFTP,
  optional server-side Lua bridge.
- Base-game item icons (the dedicated server has no textures).

## Build

```
dotnet build SpiffoCON.slnx
dotnet test tests/SpiffoCON.Core.Tests
```

`src/SpiffoCON.Core` holds everything that is not UI (RCON, commands, SFTP, catalog,
Steam) and targets plain `net10.0`.

To refresh the base-game catalog after a game update, download the free dedicated
server (`steamcmd +login anonymous +app_update 380870 +quit`) and run:

```
dotnet run tools/MakeVanillaSnapshot.cs -- "<dedicated server folder>"
```
