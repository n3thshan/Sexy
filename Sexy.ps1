#requires -Version 5.1
<#
    Sexy.ps1

    Keeps the Windows user proxy in sync with a simple daily schedule.

    Actions:
      Auto    - used by Task Scheduler; respects a pause, applies the schedule
      On      - turn proxy ON and pause the schedule
      Off     - turn proxy OFF and pause the schedule
      Pause   - leave the proxy as-is and suspend automatic changes
      Resume  - clear the pause and apply the schedule now
      Status  - show current state without changing anything

    This script only changes HKCU Internet Settings for the current user.
    It does not need administrator rights.
#>

[CmdletBinding()]
param(
    [ValidateSet('Auto', 'On', 'Off', 'Pause', 'Resume', 'Status')]
    [string]$Action = 'Auto',

    [switch]$Interactive,

    [switch]$DryRun,

    # Testing only, for example: -TestTime '2026-10-27 00:30'
    [datetime]$TestTime = [datetime]::MinValue
)

$ErrorActionPreference = 'Stop'

$scriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$settingsPath = Join-Path $scriptDir 'settings.psd1'
$logPath      = Join-Path $scriptDir 'proxy-schedule.log'
$pausePath    = Join-Path $scriptDir 'paused.flag'
$helperDll    = Join-Path $scriptDir 'SexyToggle.dll'
$helperSrc    = Join-Path $scriptDir 'SexyToggle.cs'

$script:Now = if ($TestTime -ne [datetime]::MinValue) { $TestTime } else { Get-Date }

function Write-Log {
    param([string]$Message)

    try {
        if ((Test-Path -LiteralPath $logPath) -and ((Get-Item -LiteralPath $logPath).Length -gt 1MB)) {
            Move-Item -LiteralPath $logPath -Destination "$logPath.1" -Force
        }

        $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
        Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    }
    catch {
        # Logging must never stop the proxy switch.
    }
}

function Show-Popup {
    param(
        [string]$Message,
        [int]$Seconds = 5,
        [int]$Icon = 64
    )

    if (-not $Interactive) {
        return
    }

    try {
        $shell = New-Object -ComObject WScript.Shell
        [void]$shell.Popup($Message, $Seconds, 'Sexy', $Icon)
    }
    catch {
        # A missing popup must not stop the action.
    }
}

function Get-Settings {
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        throw "Settings file not found: $settingsPath"
    }

    return Import-PowerShellDataFile -LiteralPath $settingsPath
}

function Convert-TimeToMinutes {
    param([string]$Text)

    if ($Text -eq '24:00') {
        return 1440
    }

    if ($Text -notmatch '^([01]?\d|2[0-3]):([0-5]\d)$') {
        throw "Invalid time '$Text'. Use HH:mm, for example 07:00, or 24:00."
    }

    return ([int]$Matches[1] * 60) + [int]$Matches[2]
}

function Get-V2rayNInfo {
    param($Settings)

    $defaultConfig = Join-Path $env:LOCALAPPDATA 'Programs\v2rayN-windows-64\guiConfigs\guiNConfig.json'
    $configPath = if ($Settings.V2rayNConfigPath) { [string]$Settings.V2rayNConfigPath } else { $defaultConfig }

    $fallbackBypass = '<local>;localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*'

    $port = 10808
    $server = '127.0.0.1:10808'
    $bypass = $fallbackBypass
    $loaded = $false

    try {
        if (Test-Path -LiteralPath $configPath) {
            $json = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
            $inbound = @($json.Inbound) | Where-Object { $_.Protocol -eq 'socks' } | Select-Object -First 1

            if ($inbound -and $inbound.LocalPort) {
                $port = [int]$inbound.LocalPort
            }

            $exceptions = [string]$json.SystemProxyItem.SystemProxyExceptions
            if ($json.SystemProxyItem.NotProxyLocalAddress) {
                $exceptions = "<local>;$exceptions"
            }
            if ($exceptions) {
                $bypass = $exceptions
            }

            $advanced = [string]$json.SystemProxyItem.SystemProxyAdvancedProtocol
            if ($advanced) {
                $server = $advanced.Replace('{ip}', '127.0.0.1').Replace('{http_port}', [string]$port).Replace('{socks_port}', [string]$port)
            }
            else {
                $server = "127.0.0.1:$port"
            }

            $loaded = $true
        }
    }
    catch {
        Write-Log "WARN could not read v2rayN config: $($_.Exception.Message)"
    }

    if ($Settings.ProxyServer -and $Settings.ProxyServer -ne 'auto') {
        $server = [string]$Settings.ProxyServer
    }
    if ($Settings.Bypass -and $Settings.Bypass -ne 'auto') {
        $bypass = [string]$Settings.Bypass
    }

    return [pscustomobject]@{
        Server       = $server
        Bypass       = $bypass
        Port         = $port
        ConfigLoaded = $loaded
        ConfigPath   = $configPath
    }
}

function Get-ProxyState {
    param([string]$Target)

    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $ie = Get-ItemProperty -Path $key -ErrorAction Stop

    $enabled = ([int]$ie.ProxyEnable -eq 1)
    $server = [string]$ie.ProxyServer
    $pac = [string]$ie.AutoConfigURL

    if ($enabled) {
        if ($server -and ($server -eq $Target -or $server.Contains($Target))) {
            return 'On'
        }
        return 'Other'
    }

    if ($pac) {
        return 'Other'
    }

    return 'Off'
}

function Test-ProxyPort {
    param(
        [int]$Port,
        [int]$TimeoutMs = 400
    )

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $result = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        if ($result.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) {
            $client.EndConnect($result)
            return $client.Connected
        }
        return $false
    }
    catch {
        return $false
    }
    finally {
        $client.Close()
    }
}

function Get-DesiredState {
    param(
        $Settings,
        [datetime]$At,
        [int]$Port,
        [bool]$RequirePort
    )

    $start = Convert-TimeToMinutes $Settings.OnStart
    $end = Convert-TimeToMinutes $Settings.OnEnd

    if ($start -ge 1440) {
        throw 'OnStart must be earlier than 24:00.'
    }
    if ($start -eq $end) {
        throw 'OnStart and OnEnd cannot be the same.'
    }
    if ($end -gt 1440) {
        throw 'OnEnd cannot be later than 24:00.'
    }

    $days = @($Settings.Days)
    $today = $At.DayOfWeek.ToString()
    $minutes = ($At.Hour * 60) + $At.Minute
    $on = $false

    if ($start -lt $end) {
        $on = ($days -contains $today) -and ($minutes -ge $start) -and ($minutes -lt $end)
    }
    else {
        $yesterday = $At.AddDays(-1).DayOfWeek.ToString()
        $on = (($days -contains $today) -and ($minutes -ge $start)) -or (($days -contains $yesterday) -and ($minutes -lt $end))
    }

    if ($on -and $RequirePort) {
        $portOk = Test-ProxyPort -Port $Port
        if (-not $portOk -and (Get-Process -Name 'v2rayN' -ErrorAction SilentlyContinue)) {
            # v2rayN may still be starting after logon or wake.
            Start-Sleep -Milliseconds 1500
            $portOk = Test-ProxyPort -Port $Port
        }
        if (-not $portOk) {
            return [pscustomobject]@{
                State       = 'Off'
                Reason      = "v2rayN port $Port is not listening"
                ScheduleOn  = $true
            }
        }
    }

    return [pscustomobject]@{
        State      = if ($on) { 'On' } else { 'Off' }
        Reason     = if ($on) { 'inside schedule window' } else { 'outside schedule window' }
        ScheduleOn = $on
    }
}

function Get-NextChange {
    param(
        $Settings,
        [datetime]$At
    )

    $start = Convert-TimeToMinutes $Settings.OnStart
    $end = Convert-TimeToMinutes $Settings.OnEnd
    $days = @($Settings.Days)
    $candidates = @()

    for ($i = 0; $i -lt 10; $i++) {
        $day = $At.Date.AddDays($i)
        if ($days -notcontains $day.DayOfWeek.ToString()) {
            continue
        }

        $startAt = $day.AddMinutes($start)
        if ($startAt -gt $At) {
            $candidates += $startAt
        }

        if ($end -gt $start) {
            $endAt = $day.AddMinutes($end)
        }
        else {
            $endAt = $day.AddDays(1).AddMinutes($end)
        }

        if ($endAt -gt $At) {
            $candidates += $endAt
        }
    }

    if ($candidates.Count -eq 0) {
        return $null
    }

    return ($candidates | Sort-Object | Select-Object -First 1)
}

function Initialize-Helper {
    if ('Sexy.WinInetProxy' -as [type]) {
        return
    }

    if (-not (Test-Path -LiteralPath $helperDll)) {
        if (-not (Test-Path -LiteralPath $helperSrc)) {
            throw "Helper source not found: $helperSrc"
        }

        Add-Type -TypeDefinition (Get-Content -LiteralPath $helperSrc -Raw) -Language CSharp -OutputAssembly $helperDll -OutputType Library
    }

    Add-Type -Path $helperDll
}

function Apply-State {
    param(
        [string]$Desired,
        [string]$Current,
        [string]$Target,
        [string]$Bypass,
        [string]$Reason,
        [bool]$Manual
    )

    if ($Desired -eq $Current) {
        return [pscustomobject]@{
            Changed = $false
            Message = "Proxy is already $Desired."
        }
    }

    if ($Current -eq 'Other') {
        $message = 'Another proxy configuration is active, so nothing was changed.'
        Write-Log "SKIP $message"
        return [pscustomobject]@{
            Changed = $false
            Message = $message
            Skipped = $true
        }
    }

    if ($DryRun) {
        $message = "DRY RUN: would turn proxy $Desired ($Reason)."
        Write-Log $message
        return [pscustomobject]@{
            Changed = $false
            Message = $message
            DryRun  = $true
        }
    }

    Initialize-Helper

    if ($Desired -eq 'On') {
        [Sexy.WinInetProxy]::SetProxy($Target, $Bypass)
    }
    else {
        [Sexy.WinInetProxy]::ClearProxy()
    }

    $kind = if ($Manual) { 'manual' } else { 'schedule' }
    Write-Log "Proxy $($Desired.ToUpper()) ($kind; $Reason; target=$Target)"

    return [pscustomobject]@{
        Changed = $true
        Message = "Proxy turned $($Desired.ToUpper())."
    }
}

function Set-Pause {
    if (-not (Test-Path -LiteralPath $pausePath)) {
        Set-Content -LiteralPath $pausePath -Value ('Paused at ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) -Encoding ASCII
    }
}

function Clear-Pause {
    if (Test-Path -LiteralPath $pausePath) {
        Remove-Item -LiteralPath $pausePath -Force
    }
}

try {
    $settings = Get-Settings
    $info = Get-V2rayNInfo -Settings $settings
    $target = $info.Server
    $bypass = $info.Bypass
    $port = $info.Port
    $paused = Test-Path -LiteralPath $pausePath
    $requirePort = [bool]$settings.RequirePort

    switch ($Action) {
        'Pause' {
            Set-Pause
            Write-Log 'Schedule paused indefinitely; current proxy state left unchanged.'
            $message = "Schedule paused.`nThe proxy will stay as it is until you use Resume Schedule."
            Show-Popup -Message $message -Seconds 6
            if (-not $Interactive) { Write-Output $message }
        }

        'Resume' {
            Clear-Pause
            $desired = Get-DesiredState -Settings $settings -At $script:Now -Port $port -RequirePort $requirePort
            $current = Get-ProxyState -Target $target
            $result = Apply-State -Desired $desired.State -Current $current -Target $target -Bypass $bypass -Reason $desired.Reason -Manual $false

            $message = "Schedule resumed.`n$($result.Message)"
            Write-Log "Schedule resumed. $($result.Message)"
            Show-Popup -Message $message -Seconds 6
            if (-not $Interactive) { Write-Output $message }
        }

        'Status' {
            $current = Get-ProxyState -Target $target
            $portOpen = Test-ProxyPort -Port $port
            $next = Get-NextChange -Settings $settings -At $script:Now
            $desiredNow = Get-DesiredState -Settings $settings -At $script:Now -Port $port -RequirePort $requirePort
            $ie = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'

            $stateText = switch ($current) {
                'On'    { 'ON (v2rayN)' }
                'Off'   { 'OFF (direct internet)' }
                default { 'OTHER (another proxy is configured)' }
            }

            $pauseText = if ($paused) { 'YES (automatic changes suspended)' } else { 'no' }
            $nextText = if ($paused) { 'paused' } elseif ($next) { $next.ToString('yyyy-MM-dd HH:mm') } else { 'none' }
            $portText = if ($portOpen) { "listening on 127.0.0.1:$port" } else { "NOT listening on 127.0.0.1:$port" }

            $lines = @(
                "Proxy: $stateText",
                "Server: $target",
                "v2rayN port: $portText",
                "Schedule: ON $($settings.OnStart)-$($settings.OnEnd)",
                "Schedule state now: $($desiredNow.State) ($($desiredNow.Reason))",
                "Days: $($settings.Days -join ', ')",
                "Paused: $pauseText",
                "Next scheduled change: $nextText"
            )

            if ($current -eq 'Other') {
                $lines += "Current ProxyEnable: $($ie.ProxyEnable)"
                $lines += "Current ProxyServer: $($ie.ProxyServer)"
                $lines += "Current AutoConfigURL: $($ie.AutoConfigURL)"
            }
            if ($TestTime -ne [datetime]::MinValue) {
                $lines += "Test time: $($script:Now.ToString('yyyy-MM-dd HH:mm'))"
            }

            $statusText = $lines -join "`r`n"
            if ($Interactive) {
                Show-Popup -Message $statusText -Seconds 15 -Icon 64
            }
            else {
                Write-Output $statusText
            }
        }

        'On' {
            $current = Get-ProxyState -Target $target
            if ($current -eq 'Other') {
                $message = 'Another proxy configuration is active, so the proxy was not changed.'
                Write-Log "SKIP $message"
                Show-Popup -Message $message -Seconds 0 -Icon 16
                if (-not $Interactive) { Write-Output $message }
                break
            }

            if ($requirePort -and -not (Test-ProxyPort -Port $port)) {
                $message = "Cannot turn the proxy ON: 127.0.0.1:$port is not listening. Start v2rayN first."
                Write-Log "ERROR $message"
                Show-Popup -Message $message -Seconds 0 -Icon 16
                if (-not $Interactive) { Write-Output $message }
                exit 1
            }

            Set-Pause
            $result = Apply-State -Desired 'On' -Current $current -Target $target -Bypass $bypass -Reason 'manual override' -Manual $true
            $message = "$($result.Message)`nSchedule is paused. Use Resume Schedule to resume it."
            Show-Popup -Message $message -Seconds 6
            if (-not $Interactive) { Write-Output $message }
        }

        'Off' {
            $current = Get-ProxyState -Target $target
            if ($current -eq 'Other') {
                $message = 'Another proxy configuration is active, so the proxy was not changed.'
                Write-Log "SKIP $message"
                Show-Popup -Message $message -Seconds 0 -Icon 16
                if (-not $Interactive) { Write-Output $message }
                break
            }

            Set-Pause
            $result = Apply-State -Desired 'Off' -Current $current -Target $target -Bypass $bypass -Reason 'manual override' -Manual $true
            $message = "$($result.Message)`nSchedule is paused. Use Resume Schedule to resume it."
            Show-Popup -Message $message -Seconds 6
            if (-not $Interactive) { Write-Output $message }
        }

        'Auto' {
            if ($paused) {
                break
            }

            $desired = Get-DesiredState -Settings $settings -At $script:Now -Port $port -RequirePort $requirePort
            $current = Get-ProxyState -Target $target
            $result = Apply-State -Desired $desired.State -Current $current -Target $target -Bypass $bypass -Reason $desired.Reason -Manual $false

            if ($result.Changed) {
                # The task runs hidden; no popup is needed.
            }
            elseif ($DryRun) {
                Write-Output $result.Message
            }
        }
    }
}
catch {
    $message = "Sexy error: $($_.Exception.Message)"
    Write-Log "ERROR $message"
    Show-Popup -Message $message -Seconds 0 -Icon 16
    if (-not $Interactive) { Write-Error $message }
    exit 1
}
