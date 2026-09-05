[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$UnityEditorPath,

    [Parameter(Mandatory)]
    [ValidateSet('EditMode', 'PlayMode')]
    [string]$Mode,

    [string]$TestFilter,

    [string]$ReportDirectory,

    [switch]$IncludeStress,

    [ValidateRange(1, 3600)]
    [int]$InactivityTimeoutSeconds = 90,

    [ValidateRange(1, 7200)]
    [int]$AbsoluteTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
    throw "Unity Editor executable does not exist: $UnityEditorPath"
}
if (Test-Path -LiteralPath (Join-Path $repoRoot 'Temp\UnityLockfile')) {
    throw "This project is already open in Unity: $repoRoot"
}
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $repoRoot ('outputs\regression\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
}
$reportPath = [System.IO.Path]::GetFullPath($ReportDirectory)
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'outputs\regression'))
$outputPrefix = $outputRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $reportPath.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Report directory must be under $outputRoot"
}
New-Item -ItemType Directory -Force -Path $reportPath | Out-Null
$resultPath = Join-Path $reportPath ($Mode.ToLowerInvariant() + '.xml')
$logPath = Join-Path $reportPath ($Mode.ToLowerInvariant() + '.log')
$timeoutPath = Join-Path $reportPath 'timeout.txt'

function Stop-OwnedProcessTree([int]$RootProcessId) {
    $children = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ParentProcessId -eq $RootProcessId } |
        Select-Object -ExpandProperty ProcessId)
    foreach ($childProcessId in $children) {
        Stop-OwnedProcessTree -RootProcessId $childProcessId
    }
    Stop-Process -Id $RootProcessId -Force -ErrorAction SilentlyContinue
}

function Remove-StaleProjectLock {
    $lockPath = Join-Path $repoRoot 'Temp\UnityLockfile'
    if (-not (Test-Path -LiteralPath $lockPath)) {
        return
    }
    $activeOwner = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'Unity.exe' -and $_.CommandLine -like "*$repoRoot*" })
    if ($activeOwner.Count -eq 0) {
        Remove-Item -LiteralPath $lockPath -Force
    }
}

$arguments = @(
    '-batchmode',
    '-projectPath', $repoRoot,
    '-runTests',
    '-testPlatform', $Mode,
    '-testResults', $resultPath,
    '-logFile', $logPath
)
if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
    $arguments += @('-testFilter', $TestFilter)
}
if (-not $IncludeStress) {
    $arguments += @('-testCategory', '!Stress')
}

$startedUtc = [DateTime]::UtcNow
$lastProgressUtc = $startedUtc
$lastLogLength = -1L
$unityProcess = Start-Process -FilePath $UnityEditorPath -ArgumentList $arguments -PassThru -WindowStyle Hidden
Write-Output "UNITY_TEST_STARTED pid=$($unityProcess.Id) mode=$Mode includeStress=$IncludeStress report=$reportPath"

try {
    while (-not $unityProcess.HasExited) {
        $nowUtc = [DateTime]::UtcNow
        if (Test-Path -LiteralPath $logPath) {
            $log = Get-Item -LiteralPath $logPath
            if ($log.Length -ne $lastLogLength) {
                $lastLogLength = $log.Length
                $lastProgressUtc = $log.LastWriteTimeUtc
            }
        }
        $inactiveSeconds = ($nowUtc - $lastProgressUtc).TotalSeconds
        $totalSeconds = ($nowUtc - $startedUtc).TotalSeconds
        if ($inactiveSeconds -ge $InactivityTimeoutSeconds -or $totalSeconds -ge $AbsoluteTimeoutSeconds) {
            $reason = if ($inactiveSeconds -ge $InactivityTimeoutSeconds) { 'inactivity' } else { 'absolute' }
            $record = @(
                "Utc: $($nowUtc.ToString('O'))"
                "Reason: $reason"
                "InactivitySeconds: $([Math]::Round($inactiveSeconds, 3))"
                "TotalSeconds: $([Math]::Round($totalSeconds, 3))"
                "UnityProcessId: $($unityProcess.Id)"
                "Log: $logPath"
                ''
            ) -join [Environment]::NewLine
            [System.IO.File]::WriteAllText($timeoutPath, $record, [System.Text.UTF8Encoding]::new($false))
            Stop-OwnedProcessTree -RootProcessId $unityProcess.Id
            $lockPath = Join-Path $repoRoot 'Temp\UnityLockfile'
            for ($attempt = 0; $attempt -lt 20 -and (Test-Path -LiteralPath $lockPath); $attempt++) {
                Start-Sleep -Milliseconds 100
            }
            if (Test-Path -LiteralPath $lockPath) {
                Remove-Item -LiteralPath $lockPath -Force
            }
            throw "Unity test timed out ($reason). Diagnostic: $timeoutPath"
        }
        Start-Sleep -Milliseconds 500
        $unityProcess.Refresh()
    }
}
finally {
    Remove-StaleProjectLock
    $unityProcess.Dispose()
}

if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
    throw "Unity exited without a test result. Log: $logPath"
}
[xml]$result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8
$run = $result.'test-run'
if ($null -eq $run) {
    throw "Unity wrote an invalid test result. Result: $resultPath"
}
Write-Output "UNITY_TEST_FINISHED result=$($run.result) total=$($run.total) passed=$($run.passed) failed=$($run.failed) skipped=$($run.skipped) duration=$($run.duration)s"
if ($run.failed -ne '0' -or $run.result -ne 'Passed') {
    throw "Unity tests failed. Result: $resultPath"
}
