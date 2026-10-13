# Aetheris

A lightweight Minecraft server panel for Windows. It gives you Aternos-style controls for the server you host yourself, so you don't have to work in a command prompt.

- **Dashboard:** big Start/Stop button, status, server icon, address with a copy button, players, version, uptime, free PC memory, and live **server RAM and CPU graphs** (RAM measured the same way as Task Manager)
- **Mods & plugins:** drag and drop `.jar`, `.zip` (loose jars or whole server packs with configs) and Modrinth `.mrpack` modpacks. Browse and install from Modrinth with required dependencies installed automatically. Enable or disable each one. Datapacks too.
- **Auto-reconnect:** the panel reconnects to Aetheris by itself, the playit.gg tunnel is restarted if it closes, and a connection watchdog restarts the server if it stops accepting players. Tray pop-ups on crashes.
- **Console:** live, colour-coded log, search and filters (warnings, errors, chat, joins), log download, command box with history (↑/↓) and quick commands
- **Options:** every important `server.properties` setting as toggles and sliders (including pause-when-empty, spawn monsters and file compression), a MOTD editor with colour codes and a live preview, and server icon upload. On Paper, Purpur or Spigot you also get **Allow the End**, spawn limits, autosave interval and connection throttle (`bukkit.yml`).
- **Game rules:** keepInventory, mobGriefing, daylight cycle and more, applied instantly
- **Players:** online players (op/kick/ban), whitelist, operators, bans and IP bans. Works offline too, with premium UUID lookup
- **Worlds:** switch, create, reset (new seed or type) and delete worlds
- **Schedules:** run commands automatically every X minutes or daily at a set time (announcements, saves, clearing items), with ready-made templates
- **Guide:** built-in, searchable documentation covering every feature, playit.gg setup, what applies when, Java versions, commands, performance tips and troubleshooting
- **Restart reminder:** a yellow bar on every page shows which changes still need a restart, until you restart
- **Backups:** one-click zip backups that are safe while running, plus restore, download and automatic backups
- **Software:** one-click install of Vanilla, Paper, Purpur or Fabric (any version), switch jars, check Java
- **Files:** browse, edit, upload, download, rename and delete files in the server folder
- **Settings:** RAM, Java path, optimized JVM flags, crash auto-restart, start with Windows, daily restart, playit.gg

## Install

1. Copy `dist\Aetheris.exe` and `dist\Aetheris.exe.config` anywhere. Your server folder is a good place.
2. Double-click `Aetheris.exe`. The panel opens and a crystal icon appears in the system tray.
3. Choose your server folder, click **I agree** on the EULA banner, then click **Start**.

Nothing to install: it uses the .NET Framework that's already built into Windows 10 and 11.

## Updating

Just run the new `Aetheris.exe`. If an older copy is still running, Aetheris offers to switch over. It saves and stops the old copy's server, closes the old copy, then starts the server again in the new version. To do this without a prompt (for example from a script), run `Aetheris.exe --replace`.

## Low resource use

- The panel window is just a view. **Close it any time**, and the server keeps running.
- Aetheris itself stays in the tray. About 10 seconds after the panel closes, it trims its memory to a few MB.
- No timers run while idle (one check per minute for scheduled backups and restarts), so CPU and GPU use is 0%.
- The panel window pauses all updates while it's minimized or hidden.
- Right-click the tray icon to Start, Stop, Restart or Exit. Exiting saves and stops the server first.

## playit.gg

Put your tunnel address (for example `something.joinmc.link`) in **Settings → playit.gg & address**. It then shows on the dashboard for easy copying. You can also point Aetheris at `playit.exe` so the tunnel starts with the server. Make sure **Options → Server port** matches the tunnel's local port (default 25565).

## Build from source

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

This compiles `src\Aetheris.cs` with the C# compiler that ships with Windows. `src\index.html` is embedded into the exe.

The panel only listens on `127.0.0.1`, so nobody else on your network or the internet can open it.
