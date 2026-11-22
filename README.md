<p align="center"><img src="assets/logo.png" alt="AzerothCore CPanel" width="160"></p>

# AzerothCore CPanel

**English** · [Português](README.pt-BR.md) · [Español](README.es.md)

A Windows control panel made for [AzerothCore](https://www.azerothcore.org/) servers (World of Warcraft 3.3.5a).
Instead of opening `authserver.exe` and `worldserver.exe` by hand, the panel takes care of it with one click.

> **Status:** version 1.4 available.

## Download

Download `AzerothCorePanel.exe` from the [releases page](https://github.com/EricksOliveira/AzerothCore-CPanel/releases/latest).
It is a single file and does not need .NET to be installed.

1. Open `AzerothCorePanel.exe`
2. Click **Browse...** and choose the Build or Repack folder
3. Click **Start**

## Features

- [x] Choose the Build or Repack folder; the panel finds `authserver.exe` and `worldserver.exe`
- [x] **Start**: starts authserver, then worldserver
- [x] **Stop**: clean shutdown (`server shutdown 1`), without losing character data
- [x] **Restart**: stops and starts both servers
- [x] Start or stop only one server with the **Start** and **Stop** buttons on its row in the status area
- [x] Live logs for each server, in tabs, and a box to send worldserver console commands
- [x] Restarts worldserver by itself when it asks for a restart (`server restart`, exit code 2)
- [x] If the panel closes or crashes, the servers are not left running hidden (Job Object)
- [x] Automatic restart after a crash, with a limit so it does not loop
- [x] System tray icon: minimizes next to the clock, menu with Start/Stop/Restart, and notifications when a server
  crashes
- [x] Start the panel with Windows (straight to the tray), optionally starting the servers once MySQL is ready
- [x] CPU and RAM use of each server, refreshed every second
- [x] Online players: total in the status area and the tray, and a tab with name, level, race, class, faction and map
- [x] MySQL control: state, start and stop (portable MySQL in the Repack or a Windows service), and start MySQL
  together with Start
- [x] GM commands through SOAP: tab with GM account, shortcuts, history, and "Kick" in the players list
- [x] Database backup: on demand or scheduled (1h, 6h, 12h, daily), as .zip, keeping the last N
- [x] `worldserver.conf` and `authserver.conf` editor: searchable table with each option's description and the
  `.dist` default
- [x] Light and dark themes: sun and moon buttons in the top-right corner, switching at once
- [x] Three languages: English (default), Portuguese and Spanish, picked from the flag button in the top-right corner

Ideas and suggestions are welcome: open an [issue](https://github.com/EricksOliveira/AzerothCore-CPanel/issues).

## Requirements

- Windows 10 or later
- `Console.Enable = 1` in `worldserver.conf` (the default). Without the console, Stop cannot ask for a clean
  shutdown and ends the process by force after the timeout.
- To build: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## How it works

- Each server starts with its `.exe` folder as the working folder, so AzerothCore finds the `configs` folder.
- **Stop** sends `server shutdown 1` to worldserver and waits for it to close (60 seconds by default). If it does
  not close, the process is ended by force. authserver is ended directly, since it keeps no game data.
- The **Start**, **Stop** and **Restart** buttons act on both servers. To start or stop only one, use the **Start**
  and **Stop** buttons on its row (Worldserver or Authserver) in the status area.
- **Restart a server that crashes** (on by default): if a server closes without being asked to, the panel starts
  it again after 5 seconds. After 3 crashes in 5 minutes it gives up, since the problem is probably the
  configuration or the database. A `server shutdown` given in game or in the console does not count as a crash.
- **System tray**: the icon stays next to the clock while the panel is open. Click it to open the panel; right-click
  shows Start, Stop, Restart and Exit. With **Minimize to tray** on (the default), minimizing removes the panel from
  the taskbar. A notification appears when a server crashes. On Windows 11 the icon starts in the hidden icons area
  (the ^ arrow next to the clock); drag it to the taskbar to keep it visible.
- **Start with Windows**: with **Start the panel with Windows** on, the panel opens by itself when you sign in,
  straight to the tray. With **And start the servers**, it waits until MySQL accepts connections (up to 3 minutes,
  reading the address from `LoginDatabaseInfo` in `worldserver.conf`) and starts the servers. It uses the `Run`
  registry key of your user, without administrator rights.
- **CPU and RAM**: next to each server's state. CPU adds up all cores (100% = every core busy, like Task Manager).
  RAM is the physical memory in use (working set); hover to see the peak.
- **Online players**: while worldserver is online, the panel queries the characters database every 15 seconds
  (using `CharacterDatabaseInfo` from `worldserver.conf`). The total shows in the status area and the tray, and the
  **Players** tab shows the list.
- **MySQL**: the panel looks for a `mysqld.exe` inside the server folder (portable MySQL, common in Repacks) and,
  if there is none, for a MySQL or MariaDB Windows service. The **MySQL** row in the status area shows its state and
  has **Start** and **Stop** buttons (Stop is only enabled while the servers are stopped). With **Start MySQL
  together with the servers** (the default), Start starts MySQL first when it is stopped. Stop does not stop MySQL.
  - Portable MySQL: started with the `my.ini` of its folder and shut down with `SHUTDOWN`, using the
    `LoginDatabaseInfo` account. It keeps running if the panel closes, so it is never cut off in the middle of a
    write.
  - Windows service: starting and stopping services needs administrator rights; if the panel is not running as
    administrator, Windows asks for permission (UAC).
- **GM commands (SOAP)**: the **GM commands** tab runs commands through worldserver's SOAP service and shows each
  answer. It also works when worldserver was started outside the panel. It needs `SOAP.Enabled = 1` in
  `worldserver.conf` (the **Enable SOAP in worldserver.conf** button changes only that line; restart worldserver
  afterwards) and a game account with GM level 3. The password is saved encrypted for your Windows user (DPAPI).
  There are shortcuts (server info, save characters, announce, kick, create account, grant GM, reload config),
  history with the arrow keys and, in the **Players** tab, right-click kicks a player.
- **Backup**: the **Backup** tab uses `mysqldump` (found next to MySQL) to save the chosen databases (accounts and
  characters by default; world is large and can be rebuilt) into a `backup_YYYY-MM-DD_HH-MM-SS.zip` with one `.sql`
  per database. It can run on demand or automatically (every 1, 6 or 12 hours, or once a day), and the panel deletes
  backups older than the last N. The default folder is `Backups` inside the server folder. It works while the
  servers run (`--single-transaction`). To restore: `mysql -u root -p acore_char < acore_char.sql`.
- **Configuration**: the **Configuration** tab edits `worldserver.conf` and `authserver.conf` in a table (section,
  option, value and the `.conf.dist` default), with search over name, value and description, and the full
  description of the selected option. **Only different from default** shows what was changed. Options that exist in
  the `.dist` but are missing from the file (common after updating the core) are shown in gray and appended at the
  end when their value is changed. Saving replaces only the edited lines, keeping comments and spacing, and keeps
  the previous version as `.conf.bak`. If the `.conf` does not exist, **Create from .dist** copies the default.
- **Theme**: the sun (light) and moon (dark) buttons in the top-right corner switch the theme at once, without
  restarting, including the title bar and the tray menu. Until you pick one, the panel follows the Windows app mode.
- **Language**: the flag button in the top-right corner opens a menu with English (default), Portuguese and Spanish. The
  whole panel switches at once, including notifications, the tray menu and number and date formats.
- Only one panel runs at a time: opening it again brings up the one already open (or in the tray).
- Settings are stored in `%APPDATA%\AzerothCorePanel\settings.json`.

## Building

```powershell
dotnet build src/AzerothCorePanel
```

To produce a single `.exe` that runs without installing .NET:

```powershell
dotnet publish src/AzerothCorePanel -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## Dependencies

- [MySqlConnector](https://mysqlconnector.net/) (MIT), to query the characters database.

## License

[MIT](LICENSE)
