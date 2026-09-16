param(
    [string]$Executable
)

$ErrorActionPreference = "Stop"
$testRoot = Split-Path -Parent $PSScriptRoot
$testExecutable = if ([string]::IsNullOrWhiteSpace($Executable)) {
    Join-Path $testRoot "bin\Release\net8.0-windows10.0.19041.0\PartMap.exe"
} else {
    [System.IO.Path]::GetFullPath((Join-Path $testRoot $Executable))
}

if (-not (Test-Path -LiteralPath $testExecutable)) {
    throw "PartMap.exe was not found. Build the Release configuration first."
}

$testProcess = Start-Process -FilePath $testExecutable -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 3
    if ($testProcess.HasExited) {
        throw "PartMap exited during startup with code $($testProcess.ExitCode)."
    }

    Write-Output "PASS  WPF startup smoke test"
}
finally {
    if (-not $testProcess.HasExited) {
        Stop-Process -Id $testProcess.Id
        $testProcess.WaitForExit()
    }
}
