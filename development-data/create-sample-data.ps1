$ErrorActionPreference = "Stop"
$dataRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$sampleRoot = [System.IO.Path]::GetFullPath((Join-Path $dataRoot "sample-models"))
$requiredPrefix = $dataRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $sampleRoot.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Sample output must stay under: $dataRoot"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force -Path $sampleRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $sampleRoot "批次A") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $sampleRoot "历史") | Out-Null

$contentTypes = @'
<?xml version="1.0" encoding="UTF-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
  <Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml" />
</Types>
'@

$relationships = @'
<?xml version="1.0" encoding="UTF-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel" />
</Relationships>
'@

$model = @'
<?xml version="1.0" encoding="UTF-8"?>
<model unit="millimeter" xml:lang="zh-CN" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
  <resources>
    <object id="1" type="model">
      <mesh>
        <vertices>
          <vertex x="0" y="0" z="0" /><vertex x="20" y="0" z="0" />
          <vertex x="0" y="20" z="0" /><vertex x="0" y="0" z="20" />
        </vertices>
        <triangles>
          <triangle v1="0" v2="2" v3="1" /><triangle v1="0" v2="1" v3="3" />
          <triangle v1="0" v2="3" v3="2" /><triangle v1="1" v2="2" v3="3" />
        </triangles>
      </mesh>
    </object>
  </resources>
  <build><item objectid="1" /></build>
</model>
'@

function New-Sample3mf {
    param([Parameter(Mandatory)][string]$Destination)

    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("PartMap-Sample-" + [guid]::NewGuid().ToString("N"))
    $temporaryArchive = "$temporaryRoot.3mf"
    try {
        New-Item -ItemType Directory -Force -Path (Join-Path $temporaryRoot "_rels") | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $temporaryRoot "3D") | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $temporaryRoot "[Content_Types].xml"), $contentTypes, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText((Join-Path $temporaryRoot "_rels\.rels"), $relationships, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText((Join-Path $temporaryRoot "3D\3dmodel.model"), $model, [System.Text.UTF8Encoding]::new($false))
        $zip = [System.IO.Compression.ZipFile]::Open($temporaryArchive, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Get-ChildItem -LiteralPath $temporaryRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
                $entryName = $_.FullName.Substring($temporaryRoot.Length + 1).Replace('\', '/')
                $entry = $zip.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new([DateTime]::Parse('2020-01-01T00:00:00Z'))
                $input = [IO.File]::OpenRead($_.FullName)
                try { $output = $entry.Open(); try { $input.CopyTo($output) } finally { $output.Dispose() } } finally { $input.Dispose() }
            }
        }
        finally {
            $zip.Dispose()
        }
        Move-Item -LiteralPath $temporaryArchive -Destination $Destination -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) {
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
        }
        if (Test-Path -LiteralPath $temporaryArchive) {
            Remove-Item -LiteralPath $temporaryArchive -Force
        }
    }
}

# Generate a repository-safe placeholder diagram instead of tracking user-provided artwork.
$diagramPath = Join-Path $dataRoot "sample-product-diagram.png"
$diagramBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zt9sAAAAASUVORK5CYII="
[System.IO.File]::WriteAllBytes($diagramPath, [Convert]::FromBase64String($diagramBase64))

New-Sample3mf (Join-Path $sampleRoot "示例产品-头盔-1个.3mf")
New-Sample3mf (Join-Path $sampleRoot "示例产品-头盔-1个.gcode(1).3mf")
New-Sample3mf (Join-Path $sampleRoot "批次A\示例产品-肩膀-1对.3mf")
New-Sample3mf (Join-Path $sampleRoot "历史\示例产品-旧头盔_2026-08-27.3mf")

Write-Host "Sample data created at: $sampleRoot"


