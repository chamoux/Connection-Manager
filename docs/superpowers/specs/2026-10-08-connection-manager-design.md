# Connection Manager: design

2026-10-08 · Approved · Built on the dev VM, runs on your Windows 11 gaming PC

## Summary

- **Game Mode.** When a Steam game starts, background updaters lose internet until the game closes, plus 2 minutes. They keep running, so mouse settings and RGB still work. Nothing is stopped or frozen.
- **Data Saver.** On your phone hotspot, only apps you choose get internet. It turns itself on when it sees the hotspot. Windows' own downloads get speed caps; caps for other apps are left out (see "Speed caps").
- **Safe by design.**
  - Blocks live in the app's own session with Windows' filtering engine. If the app closes or crashes, Windows deletes every block by itself, so the internet can't stay broken.
  - It uses only built-in Windows features and no drivers, which keeps it safe with anti-cheat.

## Your setup

|   |   |
| --- | --- |
| Gaming PC | A separate Windows 11 PC. This machine is a Hyper-V VM, so we build here and copy the app over. |
| Games | Steam only |
| Network | Ethernet at home, phone hotspot over Wi-Fi |
| Data Saver | Allowlist, speed caps, auto-on for hotspot |

## What the research found

| Option | Verdict |
| --- | --- |
| Block the updaters' internet with the Windows Filtering Platform (WFP, the engine under Windows Firewall) | **Use it.** No driver, safe with anti-cheat. Open connections are cut too, and apps just retry later. |
| Freeze (suspend) updater processes | Avoid. It can deadlock apps, and Razer macros and Chroma need Synapse running. |
| Stop services or disable tasks | Avoid. Windows Update repairs itself, Google re-creates its services, and Office breaks. |
| Lower CPU priority | Doesn't help ping. Spikes come from a full connection, not CPU. |
| Metered connection | Helps for Windows Update, Edge, OneDrive and the Store. Google, Razer and Steam ignore it. |
| Delivery Optimization and BITS caps | **Use them.** Safe KB/s caps on Windows Update, Store, Office and Edge downloads. |
| Windows QoS speed limits | Uploads only. They can't limit downloads. |
| Per-app download caps | Need a packet driver (WinDivert) that anti-cheats and antiviruses flag. **Skip** (see "Speed caps"). |
| Pause Wi-Fi background scans | **Use it on Wi-Fi.** Windows scans every 60 s, and each scan can spike ping. |

**Known culprits and what Game Mode blocks:**

| App | Blocked during games |
| --- | --- |
| Google Updater | Every `GoogleUpdater\<version>\updater.exe`, machine and per-user, plus legacy `GoogleUpdate.exe`. Paths are re-scanned each time because the version folder changes. |
| Edge Update | `MicrosoftEdgeUpdate.exe` |
| Razer Synapse 3 / 4 | Razer's updater processes. The exact list gets confirmed on your PC, since Synapse 4 has no setting to turn auto-update off. |
| Windows Update, Store, Office and Edge downloads | The `DoSvc` (Delivery Optimization), `BITS` and `wuauserv` services, blocked by service identity. Their downloads show up as `svchost`, not as the app. |
| Office, Adobe | `OfficeClickToRun.exe`, `AdobeARM.exe`. The download resumes later. |
| Steam | Not blocked, because games need it. Turn off "Allow downloads during gameplay" in Steam's settings. |
| Anything else | Shown in a post-game report ("X downloaded 120 MB during your game"), with one click to add it. |

## How it works

### Game Mode

It detects a game in this order, most trusted first:

1. Your own list: "always a game" and "never a game".
2. A running program inside a Steam library folder (`steamapps\common`, found from Steam's `libraryfolders.vdf`). Redistributable installers and crash handlers are excluded.
3. Manual: a double-click on the tray icon or a hotkey such as Ctrl+Alt+G.

It checks running programs every 2 seconds.

It turns off when the game process exits, after a 2-minute grace period you can change, so it doesn't flicker when a game restarts.

While Game Mode is on:

- The updater list above is blocked.
- On Wi-Fi (your hotspot), background Wi-Fi scans are paused. Windows undoes this by itself when Wi-Fi disconnects, and the app re-applies it after a reconnect.
- Apps that use the network are recorded for the post-game report.

### Data Saver

- **Auto on/off.** It turns on for:
  - networks you mark as your hotspot;
  - Android hotspots, which tell Windows they are metered;
  - iPhone hotspots, recognised by their `172.20.10.1` gateway;
  - optionally, any Wi-Fi that Windows sees as metered.

  It turns off on Ethernet. The tray has a manual override.
- **Allowlist.** Everything loses internet except:
  - your chosen apps;
  - Steam and the running game;
  - essentials: DNS, DHCP and Windows' network-status check;
  - this app.

  Local traffic (localhost) is never blocked.
- **App picker.** It lists apps that used data recently, with MB used, so you can tick what to allow.
- **Metered toggle.** One click marks the hotspot as metered in Windows, so Windows Update, OneDrive and Edge hold off by themselves.
- **Speed caps.** Windows Update, Store, Office and Edge downloads are capped in KB/s through Delivery Optimization and BITS. Set Steam's own download limit in Steam.

### Speed caps (read first)

Capping any app's download speed needs a packet driver called WinDivert. I recommend leaving it out:

- **Anti-cheat.** Easy Anti-Cheat has refused to start while it was loaded, and FACEIT blocks risky drivers. Windows now keeps a record of every driver loaded since boot, so unloading it before a game may not hide it.
- **Antivirus.** Kaspersky and Malwarebytes flag it, because malware has abused it.
- **Unmaintained.** The last release was in 2022.
- **Saves little data.** A cap slows a 500 MB update, but the update still uses 500 MB. Blocking is what saves data; caps mainly push videos to lower quality.

**Without the driver you still get:**

- allowlist blocking;
- KB/s caps on Windows' own downloads;
- Steam's built-in download limit;
- later, if you want, per-app upload caps through Windows QoS.

**Decision (2026-10-08): per-app download caps are dropped.** No WinDivert or other driver.

### Safety

- Blocks are WFP filters in a "dynamic session": Windows deletes them when the app exits or crashes. No leftover rules to clean up.
- If the app crashes, the blocks vanish, so the internet keeps working. Task Scheduler tries to restart it, and it always starts again at your next logon or from the Start menu.
- Never blocked: the game, Steam, anti-cheat services, Discord, core Windows networking, and this app.
- A few settings do persist: the Delivery Optimization and BITS caps, and the metered flag if you set it.
  - Each one is recorded before it changes.
  - Each is restored at every start, and by "Restore everything" in the tray menu.
  - The Wi-Fi scan pause undoes itself when Wi-Fi disconnects.

## Architecture

| Option | Pros | Cons |
| --- | --- | --- |
| **A. One tray app running as admin (recommended)** | One process, and it sees your desktop and Steam directly. No UAC prompts: it starts at logon through Task Scheduler with "highest privileges". | After a crash, blocks are off until it restarts. It fails open: the internet keeps working. |
| B. Background service plus tray (NetWatch style) | Most robust | About 40% more code (IPC, security). The service can't see your desktop, so the tray has to report game state anyway. |
| C. Add it to NetWatch | Reuses the UI | NetWatch isn't on the gaming PC, asks for UAC on every action, and is a monitor, not a controller. |

**Reused from NetWatch** (copied in, not linked): ETW capture (process start/stop, bytes per app), process path resolution, signature and publisher checks, and the Vue + Tailwind UI setup.

| Part | Job |
| --- | --- |
| GameDetector | Steam libraries, your list and the hotkey become "game started / stopped" events |
| NetworkWatcher | Ethernet or Wi-Fi, which network, metered or not, then "on hotspot: yes / no" |
| ModeController | Pure logic: inputs become the wanted mode (Normal, Game, Data Saver, or both) |
| FilterEngine | Owns the WFP dynamic session. Turns the wanted mode into block and allow filters and applies only the difference. |
| CapManager | Delivery Optimization and BITS KB/s caps. Saves the old values and restores them. |
| WifiTuner | Pauses Wi-Fi background scans during games and re-applies the pause after a reconnect |
| TrafficMeter | ETW bytes per app, for live usage and the post-game report |
| Tray + Window | Tray menu and hotkey. The settings window opens on demand and fully closes when hidden, so it uses no RAM while you play. |
| Settings | JSON in `C:\ProgramData\ConnectionManager`, writable by admins only |

**Flow:** detectors feed ModeController, and FilterEngine and CapManager reconcile. They compute the full set of filters and caps for the wanted mode, then change only the difference. The same code path runs at start-up, after a crash, and on every change.

**Stack:**

- C# / .NET 10, the same as NetWatch and already installed here.
- WinForms tray icon, and a WebView2 window built with Vue 3, TypeScript and Tailwind v4 (pnpm).
- WFP and the Wi-Fi API through P/Invoke, and TraceEvent for ETW.
- Tests: xUnit and Vitest.
- Self-contained build: copy one folder to the gaming PC and run `install.ps1`.

## Phases

1. **Game Mode**, which fixes the ping problem: tray app, Steam detection, your list and hotkey, the WFP blocklist with the built-in catalog, and the installer. Then test on the gaming PC.
2. **Data Saver**: allowlist, hotspot auto-detect, metered toggle, Windows download caps, the Wi-Fi scan pause for games on the hotspot, a settings window with live usage per app, and the post-game report.
3. **Extras**: auto-detect for non-Steam games (full-screen window using the GPU), and per-app upload caps (QoS).

**Optional step 0:** install NetWatch on the gaming PC and play one match. It records exactly which apps download when a game starts, so the blocklist starts from real data.

## Testing

- **Unit tests, no admin needed:** mode logic, detection and the grace timer, Steam file parsing, path expansion, the filter-plan builder, and the hotspot classifier.
- **Admin tests on this VM:**
  - real WFP filters block a test copy of curl;
  - killing the app removes the filters (check with `netsh wfp show filters`);
  - the caps are restored after a crash;
  - ETW process events arrive.
- **On the gaming PC, a 10-minute checklist:**
  - start and quit a Steam game;
  - Razer devices still work while blocked;
  - connect and disconnect the hotspot;
  - an allowed app works and a blocked app doesn't;
  - the Wi-Fi scan pause holds during a game;
  - reboot during Data Saver.

## Risks and unknowns

- **Steam detection.** Steam's `RunningAppID` registry value is reported unreliable, so it isn't used. Detection by library path covers Steam games within 2 seconds.
- **Wi-Fi names on 24H2.** Windows 11 24H2 locks most Wi-Fi APIs behind location access. The app reads the Wi-Fi name with `GetConnectedSsid`, the one call Microsoft still allows without it, and also uses the metered flag and the gateway. To be confirmed on your PC.
- **Razer.** Blocking its exes should keep DPI, RGB and macros working, because the devices are local USB. Unverified until tested on your PC.
- **Other antivirus.** WFP filters should keep working if an antivirus replaces Windows Firewall, because they sit underneath it. To be confirmed on your PC.
- **Steam and metered.** Steam ignores the metered flag, so use its own download limit and turn off "Allow downloads during gameplay".
- **Exact process names.** The Razer and NVIDIA names on your PC get confirmed by step 0 or the post-game report.

## Sources

- WFP dynamic sessions delete their filters when the session ends: <https://learn.microsoft.com/en-us/windows/win32/api/fwpmtypes/ns-fwpmtypes-fwpm_session0>
- Changing filters re-checks connections that are already open: <https://learn.microsoft.com/en-us/windows/win32/fwp/ale-re-authorization>
- Google Updater design (paths, tasks, schedule): <https://chromium.googlesource.com/chromium/src/+/main/docs/updater/design_doc.md>
- Delivery Optimization carries Windows Update, Store, Office and Edge: <https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization>
- Delivery Optimization bandwidth policies: <https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference>
- BITS bandwidth policies: <https://learn.microsoft.com/en-us/windows/win32/bits/group-policies>
- Metered connections: <https://support.microsoft.com/en-us/windows/metered-connections-in-windows-7b33928f-a144-b265-97b6-f2e95a87c408>
- Android hotspots' metered hint: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-dhcpe/94450e7e-5e67-4cb0-b844-a70846c0f481>
- QoS policies only affect outbound traffic: <https://learn.microsoft.com/en-us/windows-server/networking/technologies/qos/qos-policy-faq>
- Wi-Fi scans run every 60 s and add latency: <https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlanscan>
- Background-scan and streaming-mode settings: <https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/ne-wlanapi-wlan_intf_opcode>
- Wi-Fi API changes on 24H2 and `GetConnectedSsid`: <https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes>
- Risks of suspending threads: <https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-suspendthread>
- WinDivert docs: <https://reqrypt.org/windivert-doc.html>
- Easy Anti-Cheat refusing to start with WinDivert loaded: <https://github.com/Flowseal/zapret-discord-youtube/issues/4813>
- Kaspersky flags WinDivert: <https://forum.kaspersky.com/topic/exclusions-wont-work-for-goodbyedpi-52810>
- Malwarebytes flags WinDivert: <https://www.malwarebytes.com/blog/detections/riskware-windivert-vulndriver>
- Windows records every driver loaded since boot (used by Vanguard): <https://www.riotgames.com/en/news/vanguard-on-demand>
- Steam "Allow downloads during gameplay": <https://help.steampowered.com/faqs/view/71AB-698D-57EB-178C>
- Steam ignores metered connections: <https://steamcommunity.com/discussions/forum/10/4362373157501647612>
- Synapse 4 has no auto-update switch: <https://insider.razer.com/general-discussion-6/synapse-4-no-control-of-updates-80575>

## Phase 2 and 3 decisions (2026-10-09)

Research for these decisions: the scratchpad notes from 2026-10-09 (IPC, Wi-Fi, caps, WebView2, Phase 3). The user asked for the build to go ahead without stopping, so these choices were made without asking.

- **Architecture B replaces A.** The design's "Option B" is now the architecture. It removes the Phase 1 known risk (an elevated logon task that the user's environment can hijack).
  - **LocalSystem service** `ConnectionManager.Service.exe` does all the privileged work:
    - WFP;
    - game detection, by listing processes;
    - network watching;
    - Delivery Optimization (DO) caps;
    - Wi-Fi tuning;
    - the ETW traffic meter;
    - settings.
  - **Unelevated tray app** `ConnectionManager.exe`, started from the HKLM Run key, has the icon, the hotkey, the settings window and (in Phase 3) full-screen detection.
  - **They talk over a named pipe**, `\.\pipe\ConnectionManager.v1`:
    - The pipe is created with P/Invoke and the SDDL `O:SYG:SYD:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x12019b;;;IU)S:(ML;;NWNR;;;ME)`, with `FIRST_PIPE_INSTANCE` and `REJECT_REMOTE_CLIENTS`.
    - The client checks the server before it writes anything: owner SYSTEM, session 0, and the PID that the service manager reports.
    - The server checks the client's token: session not 0, Medium integrity or higher, not an AppContainer. Changes are accepted only from an active session.
    - Messages are length-prefixed strict JSON (at most 64 KiB) with a fixed command list. Status is pushed to subscribers as newest-only snapshots.
- **Filters in one sublayer, by weight:**

  | Weight | Filter |
  |---|---|
  | 15 | loopback permit |
  | 12 | essential permits (DNS, DHCP, network-status check, time, Defender) |
  | 10 | Game Mode blocks |
  | 8 | Data Saver permits |
  | 2 | Data Saver block-all |

  So an updater stays blocked during a game even if Data Saver allows it.
- **Paths from outside are untrusted.** "Outside" means sent by the tray, read from Steam files, or under the user's profile.
  - Before the SYSTEM service opens such a path, it must be a local drive path (`X:\`, fixed or removable) with no reparse point in any part. That rules out UNC, device paths, alternate data streams and links.
  - The service never writes to or runs a path it was given.
- **Data Saver: auto mode.**
  - **On** when the computer is on Wi-Fi and the network is one of these:
    - in the user's hotspot list (by network name);
    - an iPhone gateway (172.20.10.1);
    - metered, if "any metered Wi-Fi" is ticked (on by default).
  - **Off** on Ethernet.
  - **Manual On** for Game Mode and Data Saver goes back to Auto when the service starts, so a forgotten switch can't block updates forever.
- **Data Saver: what may use the internet:**
  - essentials;
  - security software (Defender's service, and known antivirus processes when running);
  - Steam;
  - running games and anti-cheat;
  - the user's allowed apps;
  - optionally Windows downloads. When allowed, they are capped through the DO policy values `DOMaxBackground/ForegroundDownloadBandwidth` (KB/s). The old values are journaled, and the cap is restored exactly.
  - BITS caps are skipped: Windows Update doesn't use BITS.
- **Metered toggle.** `netsh wlan set profileparameter cost=fixed` is applied only to an Unrestricted network, and undone with `cost=default`. Each change is journaled.
- **Wi-Fi scan pause.** During Game Mode, one WLAN handle is kept open with background scan off and streaming mode on. The pause is re-applied after each reconnect, and Windows undoes it on disconnect or when the service exits.
- **Traffic meter.** The ETW kernel network events are copied from NetWatch (TraceEvent 3.2.8). The meter keeps 24 hourly totals per app, and takes snapshots per game session for the post-game report.
- **Settings window.**
  - **Host.** WebView2 1.0.4191.47 runs in the tray process. Its data folder is in `%LOCALAPPDATA%`, and the UI is served from a virtual host (`app.connectionmanager.example`, Deny mapping).
  - **Locked down.**
    - The CSP is in a meta tag.
    - DevTools and context menus are off, and so are downloads, permissions, new windows and external schemes.
    - Navigation can't leave the app's origin.
    - The bridge is a postMessage whitelist.
  - **Memory.** Closing the window disposes the browser.
  - **Front end.** Vue 3, TypeScript and Tailwind v4, built with pnpm.
- **Phase 3.**
  - **Non-Steam games.** The tray looks for a full-screen foreground window whose 3D GPU load stays at or above 20% for 10 s, excluding browsers, video players and similar. The service checks the reported PID and path before treating it as a game.
  - **Upload caps.** QoS policies go in the ActiveStore, which a reboot clears. They are created through `Microsoft.Management.Infrastructure` and named `ConnectionManager-*`, and are removed when the service starts and stops.
