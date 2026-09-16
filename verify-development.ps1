param(
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command,
        [Parameter(Mandatory)]
        [string]$Description
    )

    Write-Host "`n== $Description ==" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE"
    }
}

Push-Location $projectRoot
try {
    Invoke-Checked { dotnet --info } ".NET environment"
    Invoke-Checked { dotnet restore .\PartMap.csproj } "Restore application"
    Invoke-Checked { dotnet restore .\tests\PartMap.SmokeTests\PartMap.SmokeTests.csproj } "Restore tests"
    Invoke-Checked { dotnet run --project .\tests\PartMap.SmokeTests\PartMap.SmokeTests.csproj -c Release --no-restore } "Run smoke tests"
    Invoke-Checked { dotnet build .\PartMap.csproj -c Release --no-restore -p:TreatWarningsAsErrors=true } "Strict Release build"

    Write-Host "`n== Development EXE startup ==" -ForegroundColor Cyan
    & .\tests\startup-smoke.ps1

    if ($Publish) {
        Write-Host "`n== Publish win-x64 ==" -ForegroundColor Cyan
        & .\publish-portable.ps1 -OutputDirectory "dist\PartMap-win10-x64" -RuntimeIdentifier win-x64
        & .\tests\startup-smoke.ps1 -Executable "dist\PartMap-win10-x64\PartMap.exe"

        Write-Host "`n== Publish win-x86 ==" -ForegroundColor Cyan
        & .\publish-portable.ps1 -OutputDirectory "dist\PartMap-win10-x86" -RuntimeIdentifier win-x86
        & .\tests\startup-smoke.ps1 -Executable "dist\PartMap-win10-x86\PartMap.exe"
    }

    Write-Host "`nPASS  PartMap development environment verified" -ForegroundColor Green
}
finally {
    Pop-Location
}
