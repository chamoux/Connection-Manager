# Connection Manager

Keeps your ping low and your phone data safe on a Windows 11 gaming PC.

- **Game Mode.** While you play, background updaters (Google, Edge, Razer, Windows Update, Office, Adobe…) can't use the internet, so they can't spike your ping. Nothing is stopped or frozen: they just wait until you're done.
- **Data Saver.** On your phone hotspot, only the apps you choose can use the internet. It turns itself on when it sees the hotspot.
- **Extras.**
  - Games outside Steam are detected when they run full screen.
  - You can cap how fast chosen apps upload.
  - After each game, a report shows which apps used a lot of data while you played.

Design: [the spec](docs/superpowers/specs/2026-10-08-connection-manager-design.md). Plans: [Phase 1](docs/superpowers/plans/2026-10-08-game-mode.md) and [Phases 2–3](docs/superpowers/plans/2026-10-09-phase-2-3.md).

## How it works

| Part | Runs as | Job |
| --- | --- | --- |
| **ConnectionManager** service | LocalSystem, starts with Windows | Detects games and your network, and does all the blocking (Windows Filtering Platform), the download caps, the Wi-Fi tuning and the traffic meter. |
| Tray app (`ConnectionManager.exe`) | You, without admin rights | Icon, menu, hotkey, notifications and the settings window. Exiting it doesn't stop the protection. |

They talk over a locked-down local pipe. Only programs in an active, signed-in session can send it changes, and the tray checks that it is talking to the real service before it sends anything.

**Safe by design**
- Blocks live in the service's session with Windows' filtering engine. If the service stops or crashes, Windows removes every block by itself, so your internet can't stay broken. The service then restarts on its own.
- No drivers, so anti-cheat isn't affected.
- Only two changes outside that session last. Each is recorded before it's made and undone later:
  - **Windows download caps** (Delivery Optimization policy): undone when Data Saver turns off, at every service start and stop, by "Restore everything", and by the uninstaller.
  - **A Wi-Fi network you mark as metered:** undone by "Restore everything", by unticking it, and by the uninstaller.
- Upload caps vanish when the PC restarts. The service removes and re-creates them at every start.

## Install on the gaming PC

1. On the dev PC, run `powershell -ExecutionPolicy Bypass -File tools\publish.ps1`. It builds everything and creates `artifacts\ConnectionManagerSetup-1.0.0.exe`.
2. Copy `artifacts\ConnectionManagerSetup-1.0.0.exe` to the gaming PC (USB stick, cloud drive, or drag it out of this VM) and double-click it. Approve the admin prompt.
   - If the file came through a browser or cloud drive, Windows may say "Windows protected your PC", because the file isn't code-signed. Choose **More info** → **Run anyway**.
   - An admin window shows the install and closes by itself when it's done. It replaces an older version or a Phase 1 install by itself.
3. The first window starts the app and says so; press Enter to close it. The Connection Manager window opens, and its icon stays in the tray (it may be under the ^ arrow).

If you start the setup exe with **Run as administrator**, it can't start the tray without admin rights. Start the tray from **Connection Manager** in the Start menu, or sign out and back in.

**Advanced:** publish still creates `artifacts\ConnectionManager-1.0.0.zip`, the same app with the install scripts: extract it and double-click `install.cmd`. Scripts can install from an admin prompt with `ConnectionManagerSetup-1.0.0.exe --unattended`, which never waits for a key.

## Use

**Open the app** in any of these ways:
- click the tray icon (it may be under the ^ arrow);
- right-click the tray icon and choose **Open Connection Manager**, the first item;
- start **Connection Manager** from the Start menu. This also works while it is already running.

At sign-in it starts quietly, with just the tray icon. Closing the window leaves it running in the tray.

**Icon colours**

| Colour | Meaning |
| --- | --- |
| Green | Game Mode on |
| Purple | Data Saver on |
| Half green, half purple | Both on |
| Grey | Waiting |
| Red | Both off |
| Amber | A problem (see the log) |
| Hollow grey ring | The service isn't running |

**Game Mode**
- **Auto (default):** on when a Steam game (or a full-screen game outside Steam) runs, and off 2 minutes after you quit.
- **Manual:** press **Ctrl+Alt+G** (or right-click the icon → Game Mode) to force it on. Press it again to go back to Auto. A forced "On" goes back to Auto when the PC restarts.
- **Never blocked**, even if you add them: Windows itself, Steam, your games, Discord, anti-cheat and your antivirus. Windows Update is the one exception: the built-in list pauses its download services (Windows Update, BITS and Delivery Optimization).
- **On Wi-Fi**, Game Mode also pauses Windows' background Wi-Fi scans, which can cause ping spikes every minute.
- **In Steam:** go to Settings → Downloads and turn off "Allow downloads during gameplay". Steam itself is never blocked.

**Data Saver**
- **Auto (default):** on when you're on Wi-Fi and the network is one of these:
  - one you marked "This network is my hotspot";
  - an iPhone hotspot;
  - any metered Wi-Fi. You can turn that rule off in Settings.

  It's always off on Ethernet.
- **What still gets internet:**
  - the apps you allow;
  - Steam, your running game and anti-cheat;
  - your antivirus;
  - Windows' basics (finding websites, getting an address, the "connected" check, the clock).
  - Windows Update downloads only if you allow them; then they get a speed cap, 200 KB/s by default.
- **Choosing apps:** in the tray menu use **Allowed on hotspot → Add program**, or in Settings → Data Saver tick apps from the list of what used data in the last 24 hours.
- **Mark this Wi-Fi as metered** (tray menu): tells Windows itself to hold back updates, OneDrive and Edge on that network.

**The window** (click the tray icon). Everything above, plus:
- the usage list (what used data in the last 24 hours);
- post-game reports, with one click to block an app during games;
- upload caps;
- notifications;
- **Restore everything**.

It uses no memory once you close it.

**Not a game?** If a program is listed as a game when it isn't, click it in the menu's Games list to mark it as not a game.

**Game not detected?** If your Steam library is reached through a junction or symlink, it isn't trusted. Use Games → Add a game and pick the exe from the real folder.

## Check it works on the gaming PC (20 minutes)

**Game Mode**
1. Start a Steam game. Within 3 seconds the icon turns green, and hovering shows "Game Mode on for <game>.exe".
2. While playing, open PowerShell as administrator and run:
   `netsh wfp show filters file=$env:TEMP\f.xml verbose=on; (Select-String $env:TEMP\f.xml -Pattern "Connection Manager: block").Count`
   The number should be more than 0.
3. Still in the game: join an online match and talk on Discord. Both must work normally.
4. Open Edge and go to edge://settings/help. The update check should fail while Game Mode is on.
5. **Windows Update:** check there's no traffic from its download services.
   - Open NetWatch if you have it, or Resource Monitor (Start → type `resmon`) → Network.
   - Look for the BITS, DoSvc (Delivery Optimization) and wuauserv (Windows Update) services; there should be no traffic from them.
   - In Resource Monitor these show up as `svchost.exe` lines. Task Manager → Services shows each one's PID, so you can match them.
6. **Razer:** while the game runs, change DPI, check your lighting and try a macro. All should still work.
7. Quit the game. After 2 minutes the icon turns grey, step 2 prints 0, and edge://settings/help can check for updates again.
8. **Crash test:**
   1. Press Ctrl+Alt+G; the icon turns green.
   2. In Task Manager → Services, right-click **ConnectionManager** → Go to details, then End task. Step 2 must print 0 within a few seconds.
   3. The service restarts by itself within 5 seconds. Game Mode comes back as Auto, because a forced "On" doesn't survive a restart.

**Data Saver**

9. Connect to your phone hotspot. Within about 15 seconds the icon turns purple and a notification says why, for example "Metered Wi-Fi" or "iPhone hotspot". If it doesn't, tick **This network is my hotspot** in the tray menu.
10. A browser you didn't allow can't load pages. An app you allowed works. Steam and your game work.
11. Plug in Ethernet (or switch back to home Wi-Fi). Data Saver turns off by itself.
12. Tick **Mark this Wi-Fi as metered** on the hotspot. Windows Settings → Network → that Wi-Fi shows "Metered connection" on. Untick it, and it goes back.
13. **Wi-Fi scan pause:** play a game on the hotspot for 5 minutes. Ping shouldn't jump every 60 seconds the way it did before.
14. Reboot while Data Saver is on. After you sign in, it comes back by itself (Auto) and the internet still works for allowed apps.

**Extras**

15. Run a game that isn't from Steam, in full screen. Within about 10 seconds Game Mode turns on and the menu lists it under running games.
16. In Settings → Upload caps, add an app (for example OneDrive) at 100 KB/s. While it uploads, Resource Monitor → Network shows it at or below about 100 KB/s sent.
17. After a game, if any app used more than 5 MB while you played, a notification offers the post-game report. Click it to see the list.
18. Open Settings, then close it. In Task Manager, no `msedgewebview2.exe` processes are left from it.

**Known limits**
- **Antivirus proxy.** If your antivirus has "web protection" that sends traffic through a local proxy (some Kaspersky, ESET or Avast setups), updaters may still get through. The antivirus makes the internet connection for them, and the antivirus itself is never blocked.
- **Upload caps on a PC not in a domain.** Upload caps may need a Windows setting before they take effect (`HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\QoS`, value `Do not use NLA` = 1, then a restart). Connection Manager doesn't change that for you.
- **Wi-Fi scans.** The pause stops Windows' own background scans. If another app (or the Wi-Fi flyout) asks for a scan, it still happens.

## Security notes

- **Phase 1's known risk is gone.** Phase 1's elevated sign-in task could be abused by other programs on your account to get admin rights. Phase 2 replaced it with a Windows service and a tray app that has no admin rights. The installer removes the old task.
- **Changes are limited to settings.** Programs running as you can still change Connection Manager's settings, the same way you can from the tray. They can't gain admin rights through it. The service also refuses to:
  - touch network paths or links;
  - block core Windows files;
  - change anything for a session that isn't the active one.
- **Every change is logged** in the service log (tray → Open log folder), with the program that asked for it.

## Uninstall

Open **Settings → Apps → Installed apps**, choose **Connection Manager → Uninstall**, and approve the admin prompt. An admin window first puts back what Connection Manager changed in Windows (download caps, metered networks, upload caps), then removes the service, the tray app and its sign-in entry. It keeps your settings and logs. Press Enter to close it.

To remove settings and logs too:
1. Open PowerShell as administrator.
2. Run `powershell -ExecutionPolicy Bypass -File "C:\Program Files\ConnectionManager\setup\uninstall.ps1" -RemoveSettings`.

If something could not be put back, the settings folder stays, because it lists what is left. The uninstaller says so.

If you installed from the zip, `uninstall.cmd` in the extracted folder works too.

## Develop

- Build: `dotnet build ConnectionManager.slnx`
- Unit tests: `dotnet test tests/ConnectionManager.Core.Tests` and `dotnet test tests/ConnectionManager.Setup.Tests`
- Settings UI: in `web\`, run `pnpm install`, then `pnpm dev`, which uses sample data in a browser. Run tests with `pnpm test`.
- Admin tests: real WFP filters against a copy of curl, plus the pipe, Delivery Optimization caps, the traffic meter and QoS.
  `powershell -ExecutionPolicy Bypass -File tools\run-elevated.ps1 -Command "dotnet test tests\ConnectionManager.Integration.Tests"`
- Release: `powershell -ExecutionPolicy Bypass -File tools\publish.ps1` (version 1.0.0 unless you add `-Version`).
  - It runs `pnpm install --frozen-lockfile` and `pnpm build` in `web\`, publishes the service and the tray into `artifacts\ConnectionManager\app`, and zips them with the installer.
  - It then makes the setup exe, `artifacts\ConnectionManagerSetup-<version>.exe`: the zip inside one native (NativeAOT) exe. That needs Visual Studio Build Tools with the C++ workload.
  - It also builds `artifacts\IpcProbe`, a small command-line client of the installed service, for the smoke test. It isn't shipped.
    `ConnectionManager.IpcProbe status | set-game auto|on|off | set-saver auto|on|off`
- Smoke test of the real install. Run the release step first.
  - It installs with the setup exe (`--unattended`) and checks the Apps & Features entry. It checks the Game Mode and Data Saver filters, and kills the service to check that its filters go and that it restarts. Then it uninstalls with the installed uninstaller, keeping settings.
  - It refuses to run over an existing install. Add `-Reinstall` to allow that, then run the setup exe again afterwards.
  `powershell -ExecutionPolicy Bypass -File tools\run-elevated.ps1 -Command "& .\tools\smoke.ps1"`
