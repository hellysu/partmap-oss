param(
    [string]$OutputDirectory = "dist\PartMap-win10-x64",
    [ValidateSet("win-x64", "win-x86")]
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
$allowedDistRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "dist"))
$allowedPrefix = $allowedDistRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $outputPath.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Output directory must stay inside: $allowedDistRoot"
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}

dotnet publish (Join-Path $projectRoot "PartMap.csproj") `
    -c Release `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:NuGetAudit=false `
    -o $outputPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Force -Path (Join-Path $outputPath "products") | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination (Join-Path $outputPath "使用说明.md") -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "LICENSE") -Destination (Join-Path $outputPath "LICENSE") -Force

Write-Host "Portable build created at: $outputPath"
