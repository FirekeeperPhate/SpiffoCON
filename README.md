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
  - Base-game icons: **Game icons…** reads the `Item_*` sprites from the texture packs
    (`media/texturepacks/*.pack`) of your own Project Zomboid install into SpiffoCON's cache.
    The dedicated server has no textures, and the game's art is not shipped with SpiffoCON.
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
- Logs tab: follows the server's Zomboid/Logs over SFTP (or a local folder) every 5 s:
  player chat (which RCON can't see), broadcasts, joins, admin actions and any other log
  the server writes. Switches to the new files by itself when the server restarts, and has
  a quick broadcast box.
- Accounts tab: every account the server knows, online or not, from a copy of its SQLite
  databases read over SFTP (or a local Zomboid folder): role (banned included), last login,
  character name, last saved position, dead or alive, and the kick/ban history with reasons.
  Password hashes are never read. Ban, unban and "To Players tab" (with the last position)
  go through RCON.
- Bridge tab, with the **SpiffoCON Bridge** mod (`bridge/SpiffoCONBridge`, B42, server
  side only): player positions, health, role, vehicle and inventory, vehicles in loaded
  areas and world state, which RCON can't give. It talks through files in the server's
  `Zomboid/Lua` folder, read and written over SFTP (`spiffocon_in.txt` /
  `spiffocon_out.txt`). Bridge v2 adds admin actions, each done the way the game's own
  server code does it (with its client sync) and written to the admin log: full heal,
  remove items (not worn or attached ones), repair, refuel or remove a vehicle, all asked
  for confirmation. The bridge runs while the server runs a game:
  an empty server with `PauseEmpty=true` is paused and the bridge waits.
- SFTP probe (optional): looks for `Server/*.ini`, workshop folders
  (`content/108600`), `Zomboid/mods` and logs. The SSH host key is saved at the first
  connection and checked afterwards.
- The profile lives in `%APPDATA%\SpiffoCON\profile.json`; passwords are encrypted with
  DPAPI (current Windows user).

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

## Publishing the bridge mod

The mod is on the Workshop, unlisted:
[SpiffoCON Bridge](https://steamcommunity.com/sharedfiles/filedetails/?id=3809683986)
(Workshop id `3809683986`; `bridge/SpiffoCONBridge/workshop.txt` keeps the id so new uploads
update the same item). Unlisted items don't show in searches, nor in hosts' mod browsers: add
`SpiffoCONBridge` to `Mods=` and `3809683986` to `WorkshopItems=` by hand. To upload an update:

1. In SpiffoCON's Bridge tab press **Prepare Workshop upload**: it copies
   `bridge/SpiffoCONBridge` to `%USERPROFILE%\Zomboid\Workshop\SpiffoCONBridge` (keeping
   the `id=` line the game writes into `workshop.txt` after the first upload).
2. Start Project Zomboid, open **Workshop** from the main menu, pick SpiffoCON Bridge and
   upload it (`workshop.txt` sets it unlisted).
3. On the server add `SpiffoCONBridge` to `Mods=` and the Workshop id to `WorkshopItems=`
   (**Copy server settings** puts both on the clipboard), then restart the server.

`tools/MakeBridgeImages.cs` redraws `preview.png` and `poster.png`.

## Installers

```
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

runs the tests, publishes `publish\light` (framework-dependent, needs the .NET 10 Desktop
Runtime) and `publish\full` (self-contained), and builds with Inno Setup
`installer\Output\SpiffoCON-Setup-<version>-Light.exe` / `-Full.exe`. The version comes from
`<Version>` in `src\SpiffoCON\SpiffoCON.csproj`. Installs per user by default (all users can
be chosen); Light and Full replace each other. User data (`%APPDATA%\SpiffoCON`,
`%LOCALAPPDATA%\SpiffoCON`) is never touched by setup or uninstall.
`tools/MakeAppIcon.cs` redraws `src/SpiffoCON/Assets/spiffocon.ico`.

## License

MIT, see [LICENSE](LICENSE). Project Zomboid and its art belong to The Indie Stone: SpiffoCON
ships none of it (base-game icons are read from your own game install).
