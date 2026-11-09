#requires -Version 5.1
<#
    SexyControl.ps1

    Step 1 of the UI refresh: same engine, a sharper and more honest window.

    Formerly ProxyControl1.ps1 (renamed to SexyControl.ps1, 2026-11).
    The old ProxyControl.ps1 is gone; the desktop shortcut and the scheduled
    task use SexyLauncher.exe.

    Features:
      - Per-monitor DPI aware, so text is crisp at 125-200% scaling.
      - Fixed layout sized from the real DPI, so nothing is clipped.
      - The schedule line is read from settings.psd1, not hardcoded.
      - Shows v2rayN's port state, a next-change countdown, and the
        scheduled task's last run and result.
      - "Turn Proxy ON" is disabled with a visible reason when it cannot work.
      - Inline result banner instead of popup boxes.
      - No timers and no polling. The window refreshes when it is shown,
        when it gets focus (at most once per 5 seconds), after an action,
        and when you press Refresh Status.

    Run it directly:
        powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\SexyControl.ps1
#>

[CmdletBinding()]
param(
    # Diagnostic only: print what the window would show, then exit.
    [switch]$SelfTest
)

# ---------------------------------------------------------------------------
# DPI awareness. Must run before any window or HWND exists in this process.
# powershell.exe has no DPI manifest, so without this the whole window is
# rendered at 96 DPI and bitmap-stretched (blurry) on a scaled display.
# ---------------------------------------------------------------------------
$script:Scale = 1.0

try {
    if (-not ('SexyControl.Native' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace SexyControl
{
    public static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();
    }
}
'@
    }

    # -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    [void][SexyControl.Native]::SetProcessDpiAwarenessContext([IntPtr](-4))
}
catch {
    # Awareness can only be set once per process; ignore a failure here and
    # fall back to whatever the host already declared.
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:ScheduleScript = Join-Path $scriptDir 'Sexy.ps1'
$script:SettingsPath   = Join-Path $scriptDir 'settings.psd1'
$script:LogPath        = Join-Path $scriptDir 'proxy-schedule.log'
$script:PausePath      = Join-Path $scriptDir 'paused.flag'
$script:TaskName       = 'v2rayN Proxy Schedule'
$script:PsExe          = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

$script:LastRefresh = [datetime]::MinValue
$script:Refreshing  = $false
$script:Busy        = $false
$script:LastError   = $false
$script:CanTurnOn   = $true

# ---------------------------------------------------------------------------
# Palette (light, matching the existing window instead of a themed rewrite)
# ---------------------------------------------------------------------------
$script:CBack    = [System.Drawing.Color]::FromArgb(243, 243, 243)
$script:CCard    = [System.Drawing.Color]::White
$script:CBorder  = [System.Drawing.Color]::FromArgb(224, 224, 224)
$script:CText    = [System.Drawing.Color]::FromArgb(26, 26, 26)
$script:CMuted   = [System.Drawing.Color]::FromArgb(107, 107, 107)
$script:CAccent  = [System.Drawing.Color]::FromArgb(0, 103, 192)
$script:CGreen   = [System.Drawing.Color]::FromArgb(15, 123, 15)
$script:CRed     = [System.Drawing.Color]::FromArgb(196, 43, 28)
$script:CInfoBg  = [System.Drawing.Color]::FromArgb(243, 243, 243)
$script:COkBg    = [System.Drawing.Color]::FromArgb(231, 246, 231)
$script:CBadBg   = [System.Drawing.Color]::FromArgb(253, 231, 233)

# ---------------------------------------------------------------------------
# Settings (read locally; the engine stays the source of truth for state)
# ---------------------------------------------------------------------------
$script:Settings = $null

function Get-LocalSettings {
    try {
        if (Test-Path -LiteralPath $script:SettingsPath) {
            return Import-PowerShellDataFile -LiteralPath $script:SettingsPath
        }
    }
    catch {
    }
    return $null
}

$script:Settings = Get-LocalSettings

# ---------------------------------------------------------------------------
# Single instance (separate mutex so both windows can be compared)
# ---------------------------------------------------------------------------
$mutex = New-Object System.Threading.Mutex($false, 'Local\SexyControlGui1')
$haveMutex = $false
try {
    $haveMutex = $mutex.WaitOne(0)
}
catch [System.Threading.AbandonedMutexException] {
    # A previous instance was killed; the mutex is ours now.
    $haveMutex = $true
}

if (-not $haveMutex) {
    if ($SelfTest) {
        'SELFTEST SKIP  : another Sexy 1 window is already open'
        exit 2
    }

    [void][System.Windows.Forms.MessageBox]::Show(
        'Sexy 1 is already open.',
        'Sexy 1',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information)
    return
}

# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------
function S {
    param([double]$Value)

    return [int][Math]::Round($Value * $script:Scale)
}

function Format-Days {
    param($Days)

    $all = @('Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday')
    $set = @($Days | ForEach-Object { [string]$_ })

    if ($set.Count -eq 0) {
        return 'no days'
    }
    if ($set.Count -eq 7) {
        return 'every day'
    }

    $weekdays = @('Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday')
    $onlyWeekdays = $true
    foreach ($d in $weekdays) {
        if ($set -notcontains $d) { $onlyWeekdays = $false }
    }
    if ($set.Count -eq 5 -and $onlyWeekdays) {
        return 'Mon-Fri'
    }

    $short = @{
        Monday = 'Mon'; Tuesday = 'Tue'; Wednesday = 'Wed'; Thursday = 'Thu'
        Friday = 'Fri'; Saturday = 'Sat'; Sunday = 'Sun'
    }
    $names = @()
    foreach ($d in $all) {
        if ($set -contains $d) { $names += $short[$d] }
    }
    return ($names -join ', ')
}

function Format-Ago {
    param([datetime]$When)

    $span = [datetime]::Now - $When
    if ($span.TotalSeconds -lt 0) { return 'in the future' }
    if ($span.TotalSeconds -lt 60) { return 'just now' }
    if ($span.TotalMinutes -lt 60) { return ('{0} min ago' -f [int][Math]::Floor($span.TotalMinutes)) }
    if ($span.TotalHours -lt 24) { return ('{0}h {1}m ago' -f [int][Math]::Floor($span.TotalHours), $span.Minutes) }
    return ('{0}d {1}h ago' -f [int][Math]::Floor($span.TotalDays), $span.Hours)
}

function Format-Next {
    param([string]$Text)

    if (-not $Text) { return 'unknown' }

    $parsed = [datetime]::MinValue
    $ok = [datetime]::TryParseExact(
        $Text,
        'yyyy-MM-dd HH:mm',
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::None,
        [ref]$parsed)

    if (-not $ok) { return $Text }

    $span = $parsed - [datetime]::Now
    if ($span.TotalSeconds -le 0) { return ('{0} (due now)' -f $Text) }
    if ($span.TotalMinutes -lt 1) { return ('{0} (in under a minute)' -f $Text) }
    if ($span.TotalHours -lt 1) { return ('{0} (in {1} min)' -f $Text, [int][Math]::Floor($span.TotalMinutes)) }
    if ($span.TotalDays -lt 1) { return ('{0} (in {1}h {2}m)' -f $Text, [int][Math]::Floor($span.TotalHours), $span.Minutes) }
    return ('{0} (in {1}d {2}h)' -f $Text, [int][Math]::Floor($span.TotalDays), $span.Hours)
}

function Format-TaskResult {
    param([int64]$Code)

    switch ($Code) {
        0      { return 'OK' }
        267009 { return 'still running' }
        267011 { return 'has not run yet' }
        default { return ('0x{0:X8}' -f [uint32]($Code -band 0xFFFFFFFF)) }
    }
}

function Invoke-ScheduleAction {
    param([string]$Action)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:PsExe
    $psi.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Action {1}' -f $script:ScheduleScript, $Action
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    try {
        # Read both pipes asynchronously: a large stderr write would otherwise
        # deadlock against a blocking ReadToEnd() on stdout.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        if (-not $process.WaitForExit(20000)) {
            try { $process.Kill() } catch { }
            throw 'The scheduler did not respond within 20 seconds.'
        }

        [void]$stdoutTask.Wait(2000)
        [void]$stderrTask.Wait(2000)

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output   = $stdoutTask.Result
            Error    = $stderrTask.Result
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-EngineStatus {
    $result = Invoke-ScheduleAction -Action 'Status'
    $text = [string]$result.Output

    if (-not $text.Trim()) {
        $reason = if ($result.Error) { $result.Error.Trim() } else { 'the scheduler returned no status' }
        throw $reason
    }

    $map = @{}
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match '^(?<key>[^:]+):\s*(?<value>.*)$') {
            $map[$Matches['key'].Trim()] = $Matches['value'].Trim()
        }
    }

    if (-not $map.ContainsKey('Proxy')) {
        throw 'the scheduler output was not understood'
    }

    return $map
}

function Get-PausedSince {
    if (-not (Test-Path -LiteralPath $script:PausePath)) { return $null }

    try {
        $raw = Get-Content -LiteralPath $script:PausePath -Raw
        if ($raw -match '(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
            return [datetime]::ParseExact(
                $Matches[1],
                'yyyy-MM-dd HH:mm:ss',
                [System.Globalization.CultureInfo]::InvariantCulture)
        }
    }
    catch {
    }
    return $null
}

function Set-Result {
    param(
        [string]$Text,
        [ValidateSet('ok', 'error', 'info')]
        [string]$Kind = 'info'
    )

    switch ($Kind) {
        'ok'    { $script:Banner.BackColor = $script:COkBg;  $script:BannerLabel.ForeColor = $script:CGreen }
        'error' { $script:Banner.BackColor = $script:CBadBg; $script:BannerLabel.ForeColor = $script:CRed }
        default { $script:Banner.BackColor = $script:CInfoBg; $script:BannerLabel.ForeColor = $script:CText }
    }

    $script:BannerLabel.Text = $Text
    $script:Banner.Visible = [bool]$Text
}

function Set-Hint {
    param([string]$Text)

    $script:HintLabel.Text = $Text
    $tooltip.SetToolTip($script:HintLabel, $Text)
    if ($Text) {
        $script:HintLabel.ForeColor = $script:CRed
    }
}

# ---------------------------------------------------------------------------
# Window shell
# ---------------------------------------------------------------------------
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Sexy'
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::None
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedSingle
$form.MaximizeBox = $false
$form.MinimizeBox = $true
$form.ShowInTaskbar = $true
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.BackColor = $script:CBack
$form.Icon = [System.Drawing.SystemIcons]::Application

# Force the handle so the real DPI is known before anything is positioned.
[void]$form.Handle
$dpi = 0
try { $dpi = [int][SexyControl.Native]::GetDpiForWindow($form.Handle) } catch { }
if ($dpi -le 0) {
    try { $dpi = [int][SexyControl.Native]::GetDpiForSystem() } catch { }
}
if ($dpi -le 0) { $dpi = 96 }
$script:Scale = $dpi / 96.0

$form.ClientSize = New-Object System.Drawing.Size((S 570), (S 590))

# Center on the screen under the cursor, then re-check DPI in case the move
# landed on a monitor with different scaling.
$screen = [System.Windows.Forms.Screen]::FromPoint([System.Windows.Forms.Cursor]::Position)
$area = $screen.WorkingArea
$form.Location = New-Object System.Drawing.Point(
    ($area.X + [int](($area.Width - $form.Width) / 2)),
    ($area.Y + [int](($area.Height - $form.Height) / 2)))

$dpi2 = 0
try { $dpi2 = [int][SexyControl.Native]::GetDpiForWindow($form.Handle) } catch { }
if ($dpi2 -gt 0 -and $dpi2 -ne $dpi) {
    $script:Scale = $dpi2 / 96.0
    $form.ClientSize = New-Object System.Drawing.Size((S 570), (S 590))
    $area = $screen.WorkingArea
    $form.Location = New-Object System.Drawing.Point(
        ($area.X + [int](($area.Width - $form.Width) / 2)),
        ($area.Y + [int](($area.Height - $form.Height) / 2)))
}

# Fonts: sizes are in points, so they scale with DPI by themselves.
$family = 'Segoe UI Variable Text'
try {
    $installed = (New-Object System.Drawing.Text.InstalledFontCollection).Families
    $match = $installed | Where-Object { $_.Name -eq $family } | Select-Object -First 1
    if (-not $match) { $family = 'Segoe UI' }
}
catch {
    $family = 'Segoe UI'
}

$script:FFamily = $family
$fTitle  = New-Object System.Drawing.Font($family, 16, [System.Drawing.FontStyle]::Bold)
$fBase   = New-Object System.Drawing.Font($family, 9)
$fButton = New-Object System.Drawing.Font($family, 9.5)
$fSmall  = New-Object System.Drawing.Font($family, 8.5)
$form.Font = $fBase

$tooltip = New-Object System.Windows.Forms.ToolTip

# --- Title + subtitle -------------------------------------------------------
$title = New-Object System.Windows.Forms.Label
$title.Text = 'Sexy'
$title.Font = $fTitle
$title.ForeColor = $script:CText
$title.BackColor = [System.Drawing.Color]::Transparent
$title.Location = New-Object System.Drawing.Point((S 16), (S 12))
$title.Size = New-Object System.Drawing.Size((S 538), (S 32))

$subtitle = New-Object System.Windows.Forms.Label
$subtitle.Text = 'Reads the scheduler. No background polling: it refreshes on focus or on demand.'
$subtitle.Font = $fSmall
$subtitle.ForeColor = $script:CMuted
$subtitle.BackColor = [System.Drawing.Color]::Transparent
$subtitle.Location = New-Object System.Drawing.Point((S 18), (S 46))
$subtitle.Size = New-Object System.Drawing.Size((S 536), (S 18))
$subtitle.AutoEllipsis = $true

# --- Status card ------------------------------------------------------------
$card = New-Object System.Windows.Forms.Panel
$card.Location = New-Object System.Drawing.Point((S 16), (S 68))
$card.Size = New-Object System.Drawing.Size((S 538), (S 228))
$card.BackColor = $script:CCard
$card.Add_Paint({
    param($sender, $e)

    $pen = New-Object System.Drawing.Pen($script:CBorder, 1)
    try {
        $e.Graphics.DrawRectangle($pen, 0, 0, $sender.Width - 1, $sender.Height - 1)
    }
    finally {
        $pen.Dispose()
    }
})

$script:ValueLabels = @{}
$rows = @(
    @{ Key = 'Proxy';    Caption = 'Proxy' }
    @{ Key = 'Server';   Caption = 'Server' }
    @{ Key = 'Port';     Caption = 'v2rayN port' }
    @{ Key = 'Schedule'; Caption = 'Schedule' }
    @{ Key = 'Now';      Caption = 'Right now' }
    @{ Key = 'Paused';   Caption = 'Paused' }
    @{ Key = 'Next';     Caption = 'Next change' }
    @{ Key = 'LastRun';  Caption = 'Last check' }
)

$rowTop = 14
$rowStep = 26
$i = 0
foreach ($row in $rows) {
    $y = S ($rowTop + ($i * $rowStep))

    $caption = New-Object System.Windows.Forms.Label
    $caption.Text = $row.Caption
    $caption.Font = $fBase
    $caption.ForeColor = $script:CMuted
    $caption.BackColor = [System.Drawing.Color]::Transparent
    $caption.Location = New-Object System.Drawing.Point((S 14), $y)
    $caption.Size = New-Object System.Drawing.Size((S 124), (S 22))
    $caption.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft

    $value = New-Object System.Windows.Forms.Label
    $value.Text = '-'
    $value.Font = $fBase
    $value.ForeColor = $script:CText
    $value.BackColor = [System.Drawing.Color]::Transparent
    $value.Location = New-Object System.Drawing.Point((S 142), $y)
    $value.Size = New-Object System.Drawing.Size((S 382), (S 22))
    $value.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
    $value.AutoEllipsis = $true
    $value.AccessibleName = $row.Caption
    $value.TabStop = $false

    $card.Controls.Add($caption)
    $card.Controls.Add($value)

    $script:ValueLabels[$row.Key] = $value
    $i++
}

function Get-Shade {
    param(
        [System.Drawing.Color]$Color,
        [double]$Factor
    )

    return [System.Drawing.Color]::FromArgb(
        $Color.A,
        [int][Math]::Round($Color.R * $Factor),
        [int][Math]::Round($Color.G * $Factor),
        [int][Math]::Round($Color.B * $Factor))
}

function Set-ButtonPrimary {
    param(
        $Button,
        [System.Drawing.Color]$Accent
    )

    $Button.UseVisualStyleBackColor = $false
    $Button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $Button.FlatAppearance.BorderSize = 1
    $Button.FlatAppearance.BorderColor = $Accent
    $Button.BackColor = $Accent
    $Button.ForeColor = [System.Drawing.Color]::White
    $Button.FlatAppearance.MouseOverBackColor = Get-Shade $Accent 0.90
    $Button.FlatAppearance.MouseDownBackColor = Get-Shade $Accent 0.80
}

function Set-ButtonSecondary {
    param($Button)

    $Button.UseVisualStyleBackColor = $false
    $Button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $Button.FlatAppearance.BorderSize = 1
    $Button.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(208, 208, 208)
    $Button.BackColor = [System.Drawing.Color]::White
    $Button.ForeColor = [System.Drawing.Color]::FromArgb(32, 32, 32)
    $Button.FlatAppearance.MouseOverBackColor = [System.Drawing.Color]::FromArgb(249, 249, 249)
    $Button.FlatAppearance.MouseDownBackColor = [System.Drawing.Color]::FromArgb(238, 238, 238)
}

function Set-ButtonDisabled {
    param($Button)

    Set-ButtonSecondary $Button
    $Button.BackColor = [System.Drawing.Color]::FromArgb(237, 237, 237)
    $Button.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(224, 224, 224)
    $Button.FlatAppearance.MouseOverBackColor = $Button.BackColor
    $Button.FlatAppearance.MouseDownBackColor = $Button.BackColor
    $Button.Enabled = $false
}

# ---------------------------------------------------------------------------
# Buttons
# ---------------------------------------------------------------------------
function New-Button {
    param(
        [string]$Text,
        [int]$X,
        [int]$Y
    )

    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.Font = $fButton
    $button.Location = New-Object System.Drawing.Point($X, $Y)
    $button.Size = New-Object System.Drawing.Size((S 170), (S 40))
    $button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $button.UseVisualStyleBackColor = $false
    Set-ButtonSecondary $button

    return $button
}

$onButton = New-Button -Text '&Turn Proxy ON' -X (S 16) -Y (S 304)
$onButton.Enabled = $false
$onButton.Add_Click({ Do-Action 'On' })

$offButton = New-Button -Text 'Turn Proxy &OFF' -X (S 200) -Y (S 304)
$offButton.Add_Click({ Do-Action 'Off' })

$resumeButton = New-Button -Text '&Resume Schedule' -X (S 384) -Y (S 304)
$resumeButton.Add_Click({ Do-Action 'Resume' })

$pauseButton = New-Button -Text '&Pause Schedule' -X (S 16) -Y (S 350)
$pauseButton.Add_Click({ Do-Action 'Pause' })

$editButton = New-Button -Text '&Edit Schedule' -X (S 200) -Y (S 350)
$editButton.Add_Click({
    if (-not (Test-Path -LiteralPath $script:SettingsPath)) {
        Set-Result 'settings.psd1 is missing. Restore it before editing.' 'error'
        return
    }

    try {
        Start-Process -FilePath 'notepad.exe' -ArgumentList ('"{0}"' -f $script:SettingsPath)
        Set-Result 'Settings opened in Notepad. Saving the file and focusing this window reloads the schedule.' 'info'
    }
    catch {
        Set-Result $_.Exception.Message 'error'
    }
})

$logButton = New-Button -Text 'Open &Log' -X (S 384) -Y (S 350)
$logButton.Add_Click({
    try {
        if (Test-Path -LiteralPath $script:LogPath) {
            Start-Process -FilePath 'notepad.exe' -ArgumentList ('"{0}"' -f $script:LogPath)
        }
        else {
            Set-Result 'The log file does not exist yet.' 'error'
        }
    }
    catch {
        Set-Result $_.Exception.Message 'error'
    }
})

$refreshButton = New-Button -Text '&Refresh Status' -X (S 16) -Y (S 396)
$refreshButton.Add_Click({
    Update-Status
    if ($script:LastError) { return }
    Set-Result 'Status refreshed.' 'info'
})

$closeButton = New-Button -Text '&Close' -X (S 200) -Y (S 396)
$closeButton.Add_Click({ $form.Close() })

# Fixed tab order, independent of control creation order.
$onButton.TabIndex = 0
$offButton.TabIndex = 1
$resumeButton.TabIndex = 2
$pauseButton.TabIndex = 3
$editButton.TabIndex = 4
$logButton.TabIndex = 5
$refreshButton.TabIndex = 6
$closeButton.TabIndex = 7

# --- Hint (why Turn ON is unavailable) --------------------------------------
$script:HintLabel = New-Object System.Windows.Forms.Label
$script:HintLabel.Font = $fSmall
$script:HintLabel.ForeColor = $script:CRed
$script:HintLabel.BackColor = [System.Drawing.Color]::Transparent
$script:HintLabel.Location = New-Object System.Drawing.Point((S 16), (S 440))
$script:HintLabel.Size = New-Object System.Drawing.Size((S 538), (S 18))
$script:HintLabel.Text = ''
$script:HintLabel.AutoEllipsis = $true

# --- Result banner ----------------------------------------------------------
$script:Banner = New-Object System.Windows.Forms.Panel
$script:Banner.Location = New-Object System.Drawing.Point((S 16), (S 462))
$script:Banner.Size = New-Object System.Drawing.Size((S 538), (S 54))
$script:Banner.BackColor = $script:CInfoBg
$script:Banner.Visible = $false

$script:BannerLabel = New-Object System.Windows.Forms.Label
$script:BannerLabel.Font = $fBase
$script:BannerLabel.ForeColor = $script:CText
$script:BannerLabel.BackColor = [System.Drawing.Color]::Transparent
$script:BannerLabel.Location = New-Object System.Drawing.Point((S 10), (S 6))
$script:BannerLabel.Size = New-Object System.Drawing.Size((S 518), (S 42))
$script:BannerLabel.Text = ''
$script:Banner.Controls.Add($script:BannerLabel)

# --- Footer note ------------------------------------------------------------
$note = New-Object System.Windows.Forms.Label
$note.Text = "The task runs at 00:00, 07:00, and every 5 minutes as a safety check.`r`nManual ON/OFF pauses the schedule; use Resume Schedule to hand control back."
$note.Font = $fSmall
$note.ForeColor = $script:CMuted
$note.BackColor = [System.Drawing.Color]::Transparent
$note.Location = New-Object System.Drawing.Point((S 16), (S 522))
$note.Size = New-Object System.Drawing.Size((S 538), (S 40))

$footer = New-Object System.Windows.Forms.Label
$footer.Text = 'Sexy 1.0'
$footer.Font = $fSmall
$footer.ForeColor = $script:CMuted
$footer.BackColor = [System.Drawing.Color]::Transparent
$footer.Location = New-Object System.Drawing.Point((S 16), (S 566))
$footer.Size = New-Object System.Drawing.Size((S 200), (S 16))

$form.Controls.AddRange(@(
    $title,
    $subtitle,
    $card,
    $onButton,
    $offButton,
    $resumeButton,
    $pauseButton,
    $editButton,
    $logButton,
    $refreshButton,
    $closeButton,
    $script:HintLabel,
    $script:Banner,
    $note,
    $footer
))

# ---------------------------------------------------------------------------
# Status refresh
# ---------------------------------------------------------------------------
function Update-Status {
    if ($script:Refreshing) { return }

    $script:Refreshing = $true
    $script:LastError = $false
    Set-ControlsEnabled $false
    $form.UseWaitCursor = $true
    [System.Windows.Forms.Application]::DoEvents()

    try {
        $map = Get-EngineStatus
        # Re-read so an edit in Notepad takes effect on the next refresh.
        $script:Settings = Get-LocalSettings

        $state = [string]$map['Proxy']
        $script:ValueLabels['Proxy'].Text = $state
        if ($state -like 'ON*') {
            $script:ValueLabels['Proxy'].ForeColor = $script:CGreen
        }
        elseif ($state -like 'OTHER*') {
            $script:ValueLabels['Proxy'].ForeColor = $script:CRed
        }
        else {
            $script:ValueLabels['Proxy'].ForeColor = $script:CText
        }

        $script:ValueLabels['Server'].Text = [string]$map['Server']
        $tooltip.SetToolTip($script:ValueLabels['Server'], [string]$map['Server'])

        $portText = [string]$map['v2rayN port']
        if ($portText) {
            $portListening = -not ($portText -match '^\s*NOT listening')
            $script:ValueLabels['Port'].Text = $portText
            if ($portListening) {
                $script:ValueLabels['Port'].ForeColor = $script:CGreen
            }
            else {
                $script:ValueLabels['Port'].ForeColor = $script:CRed
            }
        }
        else {
            # Missing data must never look healthy.
            $portListening = $false
            $portText = 'unknown (not reported by the scheduler)'
            $script:ValueLabels['Port'].Text = $portText
            $script:ValueLabels['Port'].ForeColor = $script:CRed
        }
        $tooltip.SetToolTip($script:ValueLabels['Port'], $portText)

        if ($script:Settings) {
            $scheduleText = 'ON {0}-{1}, {2}' -f $script:Settings.OnStart, $script:Settings.OnEnd, (Format-Days $script:Settings.Days)
        }
        else {
            $scheduleText = [string]$map['Schedule']
        }
        $script:ValueLabels['Schedule'].Text = $scheduleText

        $script:ValueLabels['Now'].Text = [string]$map['Schedule state now']

        $pausedText = [string]$map['Paused']
        $isPaused = $pausedText -like 'YES*'
        if ($isPaused) {
            $since = Get-PausedSince
            if ($since) {
                $pausedText = 'yes, since {0} ({1})' -f $since.ToString('yyyy-MM-dd HH:mm'), (Format-Ago $since)
            }
            else {
                $pausedText = 'yes'
            }
            $script:ValueLabels['Paused'].ForeColor = $script:CRed
        }
        else {
            $pausedText = 'no'
            $script:ValueLabels['Paused'].ForeColor = $script:CText
        }
        $script:ValueLabels['Paused'].Text = $pausedText

        $nextRaw = [string]$map['Next scheduled change']
        $script:ValueLabels['Next'].Text = Format-Next $nextRaw
        $tooltip.SetToolTip($script:ValueLabels['Next'], $nextRaw)

        $lastRunText = 'unknown'
        $taskInfo = $null
        try {
            $taskInfo = Get-ScheduledTaskInfo -TaskName $script:TaskName -ErrorAction Stop
        }
        catch {
            $taskInfo = $null
        }

        if ($taskInfo) {
            $resultText = Format-TaskResult ([int64]$taskInfo.LastTaskResult)
            $lastRun = $taskInfo.LastRunTime

            if ($lastRun -and $lastRun.Year -gt 1900) {
                $lastRunText = '{0} - {1} ({2})' -f $lastRun.ToString('yyyy-MM-dd HH:mm'), $resultText, (Format-Ago $lastRun)
            }
            else {
                $lastRunText = 'never - {0}' -f $resultText
            }

            $missed = [int]$taskInfo.NumberOfMissedRuns
            if ($missed -gt 0) {
                $lastRunText += ' - {0} missed' -f $missed
            }
        }
        else {
            $lastRunText = 'task not found'
        }
        $script:ValueLabels['LastRun'].Text = $lastRunText
        $tooltip.SetToolTip($script:ValueLabels['LastRun'], $lastRunText)

        # Decide whether "Turn Proxy ON" can work at all.
        $script:CanTurnOn = $true
        $hint = ''
        if ($state -like 'OTHER*') {
            $script:CanTurnOn = $false
            $hint = 'Turn ON is unavailable while another proxy is configured. Clear it first.'
        }
        elseif (-not $portText -or $portText -like 'unknown*') {
            $script:CanTurnOn = $false
            $hint = 'Turn ON is unavailable: the scheduler did not report the v2rayN port.'
        }
        elseif (-not $portListening) {
            $script:CanTurnOn = $false
            $hint = 'Turn ON is unavailable: v2rayN is not listening, so the proxy would break your connection.'
        }

        Set-Hint $hint
        Update-ButtonVisuals -State $state -PausedNow $isPaused

        $script:LastRefresh = [datetime]::Now
    }
    catch {
        $script:LastError = $true
        $script:CanTurnOn = $false
        foreach ($key in $script:ValueLabels.Keys) {
            $script:ValueLabels[$key].Text = '-'
        }
        Set-Hint ''
        foreach ($button in @($offButton, $resumeButton, $pauseButton)) {
            $button.Enabled = $true
            Set-ButtonSecondary $button
        }
        Set-ButtonDisabled $onButton
        Set-Result ('Could not read status: {0}' -f $_.Exception.Message) 'error'
    }
    finally {
        $form.UseWaitCursor = $false
        $script:Refreshing = $false
    }
}

function Set-ControlsEnabled {
    param([bool]$Value)

    foreach ($button in @($onButton, $offButton, $resumeButton, $pauseButton)) {
        if ($Value) {
            $button.Enabled = $true
        }
        else {
            Set-ButtonDisabled $button
        }
    }

    $editButton.Enabled = $Value
    $logButton.Enabled = $Value
    $refreshButton.Enabled = $Value

    if ($Value -and -not $script:CanTurnOn) {
        Set-ButtonDisabled $onButton
    }
}

# Exactly one button carries the accent colour: the one that makes sense next.
function Update-ButtonVisuals {
    param(
        [string]$State,
        [bool]$PausedNow
    )

    foreach ($button in @($onButton, $offButton, $resumeButton, $pauseButton)) {
        $button.Enabled = $true
        Set-ButtonSecondary $button
    }

    if (-not $script:CanTurnOn) {
        Set-ButtonDisabled $onButton
    }
}

function Do-Action {
    param([string]$Action)

    if ($script:Busy -or $script:Refreshing) { return }

    $script:Busy = $true
    Set-ControlsEnabled $false
    $form.UseWaitCursor = $true
    [System.Windows.Forms.Application]::DoEvents()

    try {
        $result = Invoke-ScheduleAction -Action $Action
        $text = if ($result.Output) { $result.Output.Trim() } elseif ($result.Error) { $result.Error.Trim() } else { 'Done.' }

        if ($result.ExitCode -ne 0) {
            Set-Result $text 'error'
        }
        elseif ($text -match 'not changed|already|Nothing to do') {
            # The engine refused or had nothing to do; do not paint it green.
            Set-Result $text 'info'
        }
        else {
            Set-Result $text 'ok'
        }
    }
    catch {
        Set-Result $_.Exception.Message 'error'
    }
    finally {
        $form.UseWaitCursor = $false
        $script:Busy = $false
        Set-ControlsEnabled $true
    }

    Update-Status
}

# ---------------------------------------------------------------------------
# Wiring
# ---------------------------------------------------------------------------
$form.AcceptButton = $refreshButton
$form.CancelButton = $closeButton

$form.Add_Shown({
    $form.Activate()
    if (([datetime]::Now - $script:LastRefresh).TotalSeconds -ge 5) {
        Update-Status
    }
})

# No timer: refresh only when the user actually brings the window back.
$form.Add_Activated({
    if ($script:Refreshing -or $script:Busy) { return }
    if (([datetime]::Now - $script:LastRefresh).TotalSeconds -lt 5) { return }
    Update-Status
})

if ($SelfTest) {
    try {
        $map = Get-EngineStatus
        $portText = [string]$map['v2rayN port']
        $listening = -not ($portText -match '^\s*NOT listening')

        'DPI scale      : {0:N2} (dpi {1})' -f $script:Scale, [int][Math]::Round($script:Scale * 96)
        'Window (px)    : {0} x {1}' -f $form.ClientSize.Width, $form.ClientSize.Height
        'Proxy          : {0}' -f $map['Proxy']
        'Server         : {0}' -f $map['Server']
        'Port listening : {0}' -f $listening
        'Now            : {0}' -f $map['Schedule state now']
        'Paused         : {0}' -f $map['Paused']
        if ($script:Settings) {
            'Schedule text  : ON {0}-{1}, {2}' -f $script:Settings.OnStart, $script:Settings.OnEnd, (Format-Days $script:Settings.Days)
        }
        'Next           : {0}' -f (Format-Next ([string]$map['Next scheduled change']))

        $taskInfo = Get-ScheduledTaskInfo -TaskName $script:TaskName -ErrorAction Stop
        'Task last run  : {0} - {1}' -f $taskInfo.LastRunTime.ToString('yyyy-MM-dd HH:mm'), (Format-TaskResult ([int64]$taskInfo.LastTaskResult))
        'Task next run  : {0}' -f $taskInfo.NextRunTime.ToString('yyyy-MM-dd HH:mm')

        if ($script:Settings) {
            'Can turn ON    : {0}' -f ($listening -and ($map['Proxy'] -notlike 'OTHER*'))
        }

        # Exercise the real button logic and report any disabled button.
        $selftestState = [string]$map['Proxy']
        $selftestPaused = ([string]$map['Paused']) -like 'YES*'
        $script:CanTurnOn = $listening -and ($selftestState -notlike 'OTHER*')
        Update-ButtonVisuals -State $selftestState -PausedNow $selftestPaused
        foreach ($candidate in @($onButton, $offButton, $resumeButton, $pauseButton)) {
            if (-not $candidate.Enabled) {
                'Disabled button: {0}' -f ($candidate.Text -replace '&', '')
            }
        }

        exit 0
    }
    catch {
        'SELFTEST ERROR : {0}' -f $_.Exception.Message
        exit 1
    }
}

try {
    [void]$form.ShowDialog()
}
catch {
    [void][System.Windows.Forms.MessageBox]::Show(
        $_.Exception.Message,
        'Sexy',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error)
}
finally {
    $tooltip.Dispose()
    try {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
    catch {
    }
}
