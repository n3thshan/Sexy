# Reads the state of the 'v2rayN Proxy Schedule' scheduled task.
# Prints one line:  <last run>|<last result>|<missed runs>|<next run>
# Prints NOTFOUND when the task does not exist.
# Read-only: it never starts the task.

$info = Get-ScheduledTask -TaskName 'v2rayN Proxy Schedule' -ErrorAction SilentlyContinue
if (-not $info) {
    'NOTFOUND'
    exit 0
}

$i = Get-ScheduledTaskInfo -TaskName 'v2rayN Proxy Schedule' -ErrorAction Stop
'{0}|{1}|{2}|{3}' -f $i.LastRunTime.ToString('yyyy-MM-dd HH:mm'), [int64]$i.LastTaskResult, $i.NumberOfMissedRuns, $i.NextRunTime.ToString('yyyy-MM-dd HH:mm')
