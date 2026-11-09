Sexy
=============

Schedule
--------
Proxy ON  : every day, 07:00 to 00:00
Proxy OFF : every day, 00:00 to 07:00

When the proxy is OFF, internet works directly and v2rayN keeps running.

Desktop icon
------------
There is one shortcut on the Desktop: "Sexy".
It opens a small window with these buttons:

Turn Proxy ON      Turns the proxy ON now and pauses the schedule.
                   Greyed out, with a reason, when v2rayN's port is down
                   or when another proxy is configured.
Turn Proxy OFF     Turns the proxy OFF now and pauses the schedule.
Resume Schedule    Resumes the schedule and applies the correct state now.
Pause Schedule     Leaves the proxy as it is and stops automatic changes.
Edit Schedule      Opens settings.psd1 in Notepad.
Open Log           Opens the change log in Notepad.
Refresh Status     Re-reads the current state.
Close              Closes the window.

The window shows:
  Proxy              ON, OFF, or "another proxy is configured"
  Server             the proxy address currently in use
  v2rayN port        listening or not, with the port number
  Schedule           the real times and days from settings.psd1
  Right now          what the schedule says should happen at this moment
  Paused             whether the schedule is paused, and since when
  Next change        the next scheduled time, with a countdown
  Last check         when the background task last ran, and its result

The window does not poll. It reads the state when it opens, when you bring
it back into focus, after a button press, and when you press Refresh Status.
So a countdown freezes while the window sits in the background; pressing
Refresh Status or clicking back into the window updates it.

Results of button presses appear in a coloured strip inside the window
(green = done, grey = nothing to do, red = problem). There are no popups.

Important
---------
1. v2rayN must stay set to "Do not change system proxy" in its tray menu.
2. Do not use v2rayN's system proxy menu to switch the proxy. Use this window.
3. If v2rayN is not running, the scheduler keeps the proxy OFF so the internet
   still works directly.
4. The background task runs at logon (30 second delay), exactly at 00:00 and
   07:00, and every 5 minutes as a safety check. Its name is
   "v2rayN Proxy Schedule".
5. Turn Proxy ON and Turn Proxy OFF pause the schedule so it will not undo
   your choice. Use Resume Schedule to hand control back to the schedule.

Changing the schedule
---------------------
1. Open the Sexy window and click "Edit Schedule".
2. Change OnStart and OnEnd. Use 24-hour time. 24:00 means midnight.
3. Save the file.
4. Click back into the Sexy window (or press Refresh Status) to load
   the new schedule. The Schedule row updates immediately.

Files
-----
C:\Users\Admin\Documents\Projects\v2rayN-proxy-schedule

SexyLauncher.exe  Windowless starter used by the shortcut and the task.
                    With no arguments it opens the window; with
                    "-Action Auto" it runs the scheduled check.
SexyControl.ps1   The window (DPI-aware, light theme).
Sexy.ps1           Schedule engine and actions.
settings.psd1       Schedule settings (Edit Schedule opens this).
SexyToggle.dll     Helper that switches the Windows proxy.
proxy-schedule.log  Change log.
paused.flag         Exists only while the schedule is paused.

SexyLauncher.cs and SexyToggle.cs are the sources for the two compiled
files. They are only needed if you want to rebuild; see AGENTS.md.

Fresh builds (the WinUI 3 tray app)
----------------------------------
The repository also contains a newer, tray-based app built with WinUI 3:
src\Sexy.WinUI. It is a modern replacement for the small window and it
is in testing. The old window (SexyControl.ps1) is untouched and keeps
working in the meantime.

How it works: it ships as a self-contained portable build (no installer).
Right now it is built by a GitHub Actions workflow on the repo. Download the
"Sexy-portable" artifact from the latest green build, unzip it
anywhere, and run Sexy.WinUI.exe.

The app is TRAY-ONLY. There is no main window: it sits in the system tray
as a Fluent shield. Left-click the shield to open the panel, which is the
whole interface - two rows: Proxy and Schedule, each showing the state in
big letters with a switch next to it. Tap a row's switch to toggle that
thing. Blocked states (another proxy set / v2rayN not listening) disable
the proxy switch and show the reason underneath.

Footer of the panel:
  Refresh         - re-read the scheduler
  Edit Schedule... - write settings.psd1 (a .bak copy is kept)
  Open Log...      - the last 200 lines of the log
  Exit             - quit

There is also a "Start with Windows (to tray)" switch at the bottom of the
panel. When on, Sexy starts at logon with no window at all (the HKCU Run
key, value "Sexy"). The right-click tray menu has Turn ON/OFF, Pause/Resume
and Exit.

The new app looks for the engine (Sexy.ps1) in this order:
  1. The folder named by the SEXY_ENGINE_DIR environment variable.
  2. The folder the exe is in.
  3. Up to five parent folders of the exe.
  4. C:\Users\Admin\Documents\Projects\v2rayN-proxy-schedule
So on this PC it works out of the box, and the desktop task and log stay
shared with the old setup.

The tray icon is a white Fluent shield glyph (\uEA18). It turns green when
the proxy is ON. Tray actions are silent; there are no balloons anywhere.
State is read from the scheduler on open, after any action, and on
Refresh - there is no background polling.

The Edit Schedule and Open Log dialogs scroll their content when they do
not fit. The scheduled task and the engine are NOT changed by the new app.

No console flashes
------------------
Both the Desktop icon and the background task start PowerShell through
SexyLauncher.exe. It is a windowless program, so no console or terminal
window is created at all. If you ever replace the shortcut or the task by
hand, point them at SexyLauncher.exe, not at powershell.exe and not at
SexyControl.ps1 directly.

Removing
--------
1. Open PowerShell and run:
   Unregister-ScheduledTask -TaskName 'v2rayN Proxy Schedule' -Confirm:$false
2. Delete C:\Users\Admin\Documents\Projects\v2rayN-proxy-schedule
3. Delete "Sexy" from the Desktop
4. If you want v2rayN to control the proxy again, right-click its tray icon
   and choose "Set system proxy".
