# SpiffoCON

Remote admin console for Project Zomboid (Build 42) dedicated servers, over RCON.
Windows, .NET 10, WPF.

## Status (0.1.0)

- RCON client written for PZ: replies matched by request id (a late reply is never shown
  for the next command), long replies in one or several packets, automatic reconnect
  before the next command after a drop, UTF-8.
- Console with history (Up/Down); a leading `/` is stripped.
- Broadcast editor for `servermsg`: colors (`<RGB:r,g,b>`), multi-line (`<LINE>`),
  live preview, safe quoting (inner `"` become `'`), and a reply check that does not
  report a delivered message as failed.
- SFTP probe (optional): looks for `Server/*.ini`, workshop folders
  (`content/108600`), `Zomboid/mods` and logs. The SSH host key is saved at the first
  connection and checked afterwards.
- The profile lives in `%APPDATA%\SpiffoCON\profile.json`; passwords are encrypted with
  DPAPI (current Windows user).

## Planned

- Item and vehicle catalog that includes mods, parsed from `media/scripts`. Mod files
  come from SFTP, then the local Steam workshop folder, then SteamCMD (anonymous
  `workshop_download_item 108600 <id>`). The mod list comes from `showoptions` over
  RCON, so it works without file access too.
- Players (kick, ban, teleport, access level, additem/addvehicle), server options,
  chat log tail over SFTP, optional server-side Lua bridge.

## Build

```
dotnet build SpiffoCON.slnx
dotnet test tests/SpiffoCON.Core.Tests
```

`src/SpiffoCON.Core` holds everything that is not UI (RCON, commands, SFTP) and targets
plain `net10.0`.
