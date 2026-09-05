param(
    [Parameter(Mandatory = $true)]
    [int]$UnityProcessId,
    [Parameter(Mandatory = $true)]
    [long]$UnityProcessStartUtcTicks,
    [Parameter(Mandatory = $true)]
    [string]$ReportDirectory,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 3600)]
    [int]$InactivityTimeoutSeconds,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 7200)]
    [int]$AbsoluteTimeoutSeconds
)

$ErrorActionPreference = 'Stop'
$reportPath = [System.IO.Path]::GetFullPath($ReportDirectory)
$heartbeatPath = Join-Path $reportPath 'heartbeat.txt'
$completedPath = Join-Path $reportPath 'completed.txt'
$currentTestPath = Join-Path $reportPath 'current-test.txt'
$timeoutPath = Join-Path $reportPath 'timeout.txt'
$watchStartedUtc = [DateTime]::UtcNow

while ($true) {
    $unity = Get-Process -Id $UnityProcessId -ErrorAction SilentlyContinue
    if ($null -eq $unity) {
        exit 0
    }

    if ($unity.StartTime.ToUniversalTime().Ticks -ne $UnityProcessStartUtcTicks) {
        exit 0
    }

    if (Test-Path -LiteralPath $completedPath) {
        exit 0
    }

    $nowUtc = [DateTime]::UtcNow
    $lastProgressUtc = $watchStartedUtc
    if (Test-Path -LiteralPath $heartbeatPath) {
        $lastProgressUtc = (Get-Item -LiteralPath $heartbeatPath).LastWriteTimeUtc
    }

    $inactiveSeconds = ($nowUtc - $lastProgressUtc).TotalSeconds
    $totalSeconds = ($nowUtc - $watchStartedUtc).TotalSeconds
    if ($inactiveSeconds -ge $InactivityTimeoutSeconds -or $totalSeconds -ge $AbsoluteTimeoutSeconds) {
        $reason = if ($inactiveSeconds -ge $InactivityTimeoutSeconds) { 'inactivity' } else { 'absolute' }
        $currentTest = if (Test-Path -LiteralPath $currentTestPath) {
            Get-Content -LiteralPath $currentTestPath -Raw -Encoding UTF8
        } else {
            'Current test was not recorded.'
        }
        $record = @(
            "Utc: $($nowUtc.ToString('O'))"
            "Reason: $reason"
            "InactivitySeconds: $([Math]::Round($inactiveSeconds, 3))"
            "TotalSeconds: $([Math]::Round($totalSeconds, 3))"
            "UnityProcessId: $UnityProcessId"
            ''
            $currentTest.TrimEnd()
            ''
        ) -join [Environment]::NewLine
        [System.IO.File]::WriteAllText($timeoutPath, $record, [System.Text.UTF8Encoding]::new($false))
        Stop-Process -Id $UnityProcessId -Force
        exit 124
    }

    Start-Sleep -Milliseconds 500
}
