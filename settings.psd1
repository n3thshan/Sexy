@{
    # Proxy ON window, local time, 24-hour clock.
    # "24:00" means midnight at the end of the day.
    OnStart = '07:00'
    OnEnd   = '24:00'

    # Days when the ON window applies.
    Days = @(
        'Monday'
        'Tuesday'
        'Wednesday'
        'Thursday'
        'Friday'
        'Saturday'
        'Sunday'
    )

    # 'auto' reads the port, proxy address and bypass list from v2rayN.
    # You can also put an explicit value, for example '127.0.0.1:10808'.
    ProxyServer = 'auto'
    Bypass      = 'auto'

    # Leave empty to use the normal v2rayN install path.
    V2rayNConfigPath = ''

    # Do not turn the proxy ON unless the v2rayN proxy port is listening.
    RequirePort = $true
}
