# AGENTS.md — v2rayN Proxy Schedule

Guidance for AI agents and humans working in this folder.

## What this is

A per-user Windows proxy scheduler for v2rayN. A scheduled task flips the
system proxy (WinINET / HKCU Internet Settings) ON and OFF on a daily window,
plus a DPI-aware WinForms window ("Sexy") for manual override.

Hard constraints of the design:

- **No administrator rights.** Only `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`.
- **No console windows, ever.** Everything starts through `SexyLauncher.exe`,
  a GUI-subsystem host that launches PowerShell with `CreateNoWindow = true`.
- **Never steal someone else's proxy.** If the active proxy is neither ours nor
  "off" (state `Other`), every action refuses to touch it and just logs `SKIP`.
- **v2rayN is not controlled.** It keeps running and stays set to
  "Do not change system proxy". Only the Windows proxy setting is switched.
- **Zero background work in the UI.** No timers, no threads, no polling. The
  window reads state on demand. Do not add a `Timer` or a background loop.

## File map

| File | Role |
| --- | --- |
| `Sexy.ps1` | The engine. All scheduling, state detection, pause handling, logging. Single entry point for every action. |
| `SexyControl.ps1` | The window. DPI-aware WinForms, light theme, inline results. Shells out to the engine. |
| `settings.psd1` | User-editable schedule config (`Import-PowerShellDataFile`). |
| `SexyToggle.cs` → `SexyToggle.dll` | C# helper: `InternetSetOption(PER_CONNECTION_OPTION)` + registry mirror + `INTERNET_OPTION_SETTINGS_CHANGED` / `REFRESH`. |
| `SexyLauncher.cs` → `SexyLauncher.exe` | Windowless launcher. No args = open the window; `-Action X` = run the engine and propagate its exit code. |
| `README.txt` | End-user documentation. Keep it in sync when behavior changes. |
| `proxy-schedule.log`, `.log.1` | Runtime log (rotated at 1 MB). |
| `paused.flag` | Runtime marker. Exists only while the schedule is paused. |

`SexyToggle.dll` and `SexyLauncher.exe` are **committed build artifacts**
(there is no `.csproj`). Rebuild them whenever their `.cs` source changes —
see Build. The `1` suffix on the UI files is historical; keep it unless you are
deliberately renaming (then update the shortcut and the task in the same step).

The **WinUI 3 port** lives in `src/Sexy.WinUI/` (C#, .NET 8,
Windows App SDK, unpackaged + self-contained). It is a modern Mica-styled
replacement for `SexyControl.ps1`; the engine and task are unchanged.
Built on demand by `.github/workflows/build-portable.yml` and distributed as a
portable artifact.

## How it flows

```
Task Scheduler ──> SexyLauncher.exe -Action Auto ──> Sexy.ps1
Desktop shortcut ─> SexyLauncher.exe (no args) ──> SexyControl.ps1 ──> Sexy.ps1 -Action <x>
                                                          │
                                    settings.psd1 ────────┤
                                    v2rayN guiNConfig.json┤ (port / server / bypass when set to 'auto')
                                    HKCU Internet Settings┤ (current state)
                                                          v
                                            SexyToggle.dll ──> WinINET + registry + refresh
```

### Engine details worth knowing before editing

- **Actions:** `Auto` (task, respects pause), `On`, `Off` (both set the pause
  flag), `Pause`, `Resume` (clears pause and applies now), `Status` (read-only).
- **Schedule model:** `OnStart`/`OnEnd` in minutes; `24:00` is valid and means
  1440. If `start > end` the window is treated as overnight and the *start day*
  must be listed in `Days` (see the `yesterday` branch in `Get-DesiredState`).
- **`RequirePort`:** ON is downgraded to OFF when the v2rayN port is not
  listening, so a dead proxy never breaks networking. One retry (1.5 s) happens
  if `v2rayN` is running, to survive logon/wake races.
- **`Get-NextChange`** scans up to 10 days ahead; it is used only for display.
- **`ProxyServer`/`Bypass` = `'auto'`** reads v2rayN's `guiNConfig.json`
  (`Inbound` socks `LocalPort`, `SystemProxyItem.*`), falling back to
  `127.0.0.1:10808` and a standard private-range bypass list.
- **Log format:** `yyyy-MM-dd HH:mm:ss <message>`; prefixes `WARN`, `SKIP`,
  `ERROR` are meaningful and greppable.
- **Errors:** `$ErrorActionPreference = 'Stop'`, one top-level `try/catch` that
  logs, optionally pops up (`-Interactive` only), then `exit 1`. Keep that shape.
- **Idempotence:** `Apply-State` returns early when desired == current, so the
  5-minute safety check is silent when nothing needs to change.

### Window details (`SexyControl.ps1`)

- **DPI:** `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` runs before
  `Add-Type -AssemblyName System.Windows.Forms` and before any HWND. Keep that
  order. `AutoScaleMode = None`; WinForms will not scale for you.
- **Layout:** every `Location`/`Size`/spacing value goes through `S()`, which
  multiplies by `GetDpiForWindow(...) / 96`. Never hardcode raw pixels.
  Font sizes stay in points and scale by themselves.
- **Fixed window:** 570×590 DIPs, non-resizable, centered manually on the
  screen under the cursor, then DPI is re-checked in case the move landed on a
  different monitor. Content is grouped in a bordered status card.
- **Buttons:** neutral by default; only `Turn Proxy ON` can be disabled, and a
  disabled button is painted grey with a red one-line reason above the banner.
  There is deliberately no accent/primary button.
- **Refresh model:** on `Shown`, on `Activated` (max once per 5 s), after any
  action, and on the Refresh button. `Update-Status` disables the controls while
  an engine call is in flight and guards re-entry with `$script:Refreshing`.
- **Engine calls:** `Invoke-ScheduleAction` reads both pipes asynchronously and
  kills the child after 20 s. Never reintroduce a blocking `ReadToEnd()`.
- **Status parsing:** `Get-EngineStatus` splits `Key: value` lines from
  `-Action Status`. The engine's Status text is effectively a contract: adding
  a line is safe, renaming one requires updating the window.
- **Diagnostic:** `.\SexyControl.ps1 -SelfTest` prints exactly what the window
  would show and exits without opening a window. Safe to run against live state.
- **Mutex:** `Local\SexyControlGui1` (single instance; abandoned mutex is
  adopted).

## Commands

Run from this folder in Windows PowerShell 5.1 (`powershell.exe`), not
PowerShell 7 — the code targets `#requires -Version 5.1` and WinForms/WinINET.

Read-only inspection (safe):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Sexy.ps1 -Action Status
.\SexyControl.ps1 -SelfTest
Get-ScheduledTask -TaskName 'v2rayN Proxy Schedule' | Format-List *
Get-ScheduledTaskInfo -TaskName 'v2rayN Proxy Schedule'
Get-Content .\proxy-schedule.log -Tail 30
```

Launch the window without a console (always prefer this):

```powershell
Start-Process .\SexyLauncher.exe -WorkingDirectory $PWD
```

Simulation (see caveats below):

```powershell
.\Sexy.ps1 -Action Auto -DryRun
.\Sexy.ps1 -Action Status -TestTime '2026-10-29 00:30'
```

Build (verified on this machine; matches the shipped binaries):

```powershell
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:library /out:SexyToggle.dll /r:System.dll SexyToggle.cs
& $csc /nologo /target:winexe   /out:SexyLauncher.exe /r:System.dll SexyLauncher.cs
```

Rules for builds:

- `/target:winexe` is mandatory for the launcher. A console-subsystem build, or
  starting `powershell.exe` from a console, produces a visible Windows Terminal
  window even with `-WindowStyle Hidden` (Windows Terminal ignores `SW_HIDE`).
- Close the running window before rebuilding the exe; Windows locks it.
- `Sexy.ps1` can regenerate `SexyToggle.dll` on the fly via
  `Initialize-Helper`, so a missing DLL is recoverable if the folder is writable.
- After rebuilding, run `-SelfTest` and `-Action Status` through the launcher to
  confirm the exit code and that no terminal window appears.

## Safety rules (this machine is live)

The proxy is in active use and the scheduled task is installed. Therefore:

- **Do not run state-changing commands**: `-Action On`, `-Action Off`,
  `-Action Pause`, `-Action Resume`, or a bare `-Action Auto` without `-DryRun`.
- `-DryRun` is **not fully side-effect free**: `On`/`Off` call `Set-Pause`
  *before* the dry-run check (creating `paused.flag`), and `Resume` clears the
  flag. Only `Status`, `-SelfTest`, and `Auto -DryRun` are safe on the live
  machine.
- To exercise real toggles, get explicit user approval first, snapshot the
  current state, and restore it afterwards (turn the schedule back on with
  `Resume`, or clear `paused.flag` if the user was not paused before).
- Never hand-edit or delete `proxy-schedule.log`, `paused.flag`, or the
  scheduled task unless the user asked for it.
- Do not "clean up" the `.log.1` rotation file or the binaries.

Because every run reads real `HKCU` state, copying the folder to a temp
directory does **not** sandbox proxy changes. Simulate with `-DryRun` /
`-TestTime` / `-SelfTest` instead.

## This machine (environment-specific)

- Project path: `C:\Users\Admin\Documents\Projects\v2rayN-proxy-schedule` (paths are resolved
  from `$MyInvocation.MyCommand.Path` / `AppDomain.BaseDirectory`, but the task
  and shortcut embed the absolute path).
- Display: 2880×1800 at **200% scaling**. Process is per-monitor DPI aware;
  the window is 1140×1180 physical pixels.
- Scheduled task: `v2rayN Proxy Schedule`, principal `SPECTRE\Admin`
  (Interactive, RunLevel Limited), action
  `SexyLauncher.exe -Action Auto`, workdir the project folder.
  Triggers: at logon (+30 s), repeating every 5 min for 3650 days, daily 00:00,
  daily 07:00. `MultipleInstances = IgnoreNew`, `ExecutionTimeLimit = PT2M`,
  `StartWhenAvailable = $true`.
- Desktop shortcut: `%USERPROFILE%\Desktop\Sexy.lnk` →
  `SexyLauncher.exe`, no arguments, icon `shell32.dll,167`.
- Default v2rayN config:
  `%LOCALAPPDATA%\Programs\v2rayN-windows-64\guiConfigs\guiNConfig.json`.
- PowerShell 5.1.26100; .NET Framework 4.8.1; inbox `csc.exe` at the path above.

## Conventions

- Match the existing style: PowerShell 5.1-compatible syntax only (no ternary,
  `??`, `-Parallel`), `Verb-Noun` functions, comment-based file headers,
  explicit `param()` blocks, 4-space indentation, CRLF line endings.
- Keep user-facing strings plain ASCII and short; the window is fixed-size, so
  long text belongs in the status values or the result banner, not in labels.
- Any behavior change must update `README.txt` (end-user wording) and this file.
- Log every state change; never let logging or popups break a proxy switch
  (existing helpers swallow their own exceptions — keep that).
- Keep the `Other`-state guard and the `RequirePort` guard. Removing either is a
  regression, not a simplification.
- The window must stay a *reader*: it may only change the proxy through
  `Sexy.ps1`, never by touching the registry itself.

## The WinUI 3 port (`src/Sexy.WinUI/`)

The app is **tray-only**. There is no main window: the tray shield opens a
small panel (H.NotifyIcon `TrayPopup`), which is the entire interface. Only
the engine changes the proxy - the UI is a *reader*.

| File | Role |
| --- | --- |
| `Sexy.WinUI.csproj` | Unpackaged, self-contained (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`), `net8.0-windows10.0.19041.0`, win-x64. Package refs: `Microsoft.WindowsAppSDK` 2.6.5, `H.NotifyIcon.WinUI` 2.3.2 (2.4.1+ dropped .NET 8 - do not upgrade without retargeting to net10). |
| `App.xaml` / `App.xaml.cs` | Single-instance mutex (`Local\SexyWinUI`), DPI awareness P/Invoke, app startup. |
| `MainWindow.xaml` / `.cs` | Invisible 1x1 host window. H.NotifyIcon needs a visual tree + `Loaded` before it can create the native tray icon, so the app keeps an empty host window, activates it once and hides it. It hosts the `TaskbarIcon` (tray shield) and the `TrayPanel`. |
| `TrayPanel.xaml` / `.cs` | The whole UI: two switch rows (PROXY / SCHEDULE, big state word + reason line), footer grid (Refresh / Edit Schedule... / Open Log... / Exit), and a "Start with Windows (to tray)" toggle row (reuses `StartupService`). |
| `EditScheduleDialog.xaml` / `.cs` | In-app editor for `settings.psd1`. |
| `EngineService.cs` | Spawns `Sexy.ps1 -Action <x>`, parses `Key: value` Status lines, reads the log tail, Task Scheduler state via `TaskInfo.ps1`. Finds the engine via: `SEXY_ENGINE_DIR` env var -> exe folder -> up to 5 parent folders -> `C:\Users\Admin\Documents\Projects\v2rayN-proxy-schedule`. |
| `SettingsService.cs` | Regex read/write of `settings.psd1` (writer keeps comments, backs up to `settings.psd1.bak` first). |
| `StartupService.cs` | Reads/writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Sexy` = `"<exe path>" --tray` (per-user, no admin). |
| `TaskInfo.ps1` | Read-only Task Scheduler query; shipped next to the exe. |

Behavior notes:

- **Engine resolution:** the portable exe never builds or modifies the engine.
  On this machine it falls back to the original project folder, so the
  scheduled task, the log and `settings.psd1` stay shared.
- **The interface is the tray panel.** Left-click the shield to open it;
  click it again (or click elsewhere) to close. The panel is a `TrayPopup`
  owned by the `TaskbarIcon` (`PopupActivation = PopupActivationMode.LeftClick`),
  anchored at the tray.
- **Refresh model:** the panel refreshes on open (Loaded fires per open -
  the popup is re-created), after any action it triggers, and on the Refresh
  button. No timers, no background polling.
- **Proxy/Schedule rows** mirror each other: big state word (ON green /
  PAUSED amber / OFF grey / OTHER red), the switch, and a reason/detail
  line. The proxy switch disables with the reason when another proxy is
  set or v2rayN is not listening (the old `OTHER*`/`RequirePort` rules - do
  not bypass).
- **Tray icon:** `GeneratedIconSource`, text \uEA18 (Shield) in Segoe Fluent
  Icons; near-white = off, tinted green = ON. File-based `BitmapImage`/PNG
  tray icons crash the icon pipeline (0xC000027B in CoreMessagingXP.dll) -
  never do it again.
- **Dialogs scroll:** Edit Schedule and the Log dialog wrap their content
  in a ScrollViewer so they fit smaller screens.
- **Tray actions run hidden and silent** (tray menu + switches): no balloons,
  no opening anything. Engine refusals surface on the HintText line inside
  the panel and are cleared by the next refresh/action.
- **No balloon notifications anywhere** (the old "always open the window" and
  later "balloon result" designs are both gone).
- **Start with Windows:** footer toggle writes the HKCU Run entry; the exe
  ignores `--tray` (every start is tray-only). Deleting the exe removes the
  need to clean up anything else.
- DPI awareness is set at runtime in `App` (P/Invoke
  `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`) before any window is
  created. Do NOT add an `app.manifest`/`ApplicationManifest`: the MSIX
  tooling owns the embedded manifest and mangles merged ones, producing the
  "side-by-side configuration is incorrect" startup error. `EnableMsixTooling=true`
  stays (it fixes the published-exe startup crash 0xC000027B in
  Microsoft.UI.Xaml.dll).
- Tray icons need `H.NotifyIcon.WinUI`; the Win32 popup menu calls `ICommand`s
  attached to `MenuFlyoutItem`s, never `Click` handlers.
- The build happens in GitHub Actions (`.github/workflows/build-portable.yml`):
  `windows-latest`, .NET 8 SDK, `dotnet publish -r win-x64 --self-contained`,
  output uploaded as the `Sexy-portable` artifact.
- The exe is **unsigned**; Windows SmartScreen may warn on first run.
- This machine is the build machine only for CI - the app itself is developed
  against the live engine. Keep all engine calls read-only during testing.
## Known rough edges

These are pre-existing; fix only if asked, and mention them rather than
silently changing behavior:

1. `-TestTime` shifts only the engine's notion of "now"; the pause marker and
   log timestamps still use the real clock.
2. The 5-minute heartbeat is a repetition pattern on a time trigger, not a
   maintenance trigger; it stops if that trigger is disabled.
3. The show-on-focus refresh blocks for ~0.6 s while it spawns PowerShell. This
   is the deliberate cost of "zero background work"; do not replace it with a
   timer.
4. `Set-ButtonPrimary`, `Get-Shade` and `$script:CAccent` are unused leftovers
   from the accent-button experiment. They are harmless; remove them together
   if you touch that block.
5. `Update-ButtonVisuals` still takes `-State`/`-PausedNow` but no longer uses
   them; the parameters are kept so call sites stay stable.
6. The status card is parsed from human-readable engine output. Renaming a
   Status line is a breaking change for the window.
