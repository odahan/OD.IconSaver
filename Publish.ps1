$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'app\IconSaver.csproj'
$destination = Join-Path $PSScriptRoot 'Publish'
$publishOptions = @(
    '--configuration', 'Release'
    '--runtime', 'win-x64'
    '--self-contained', 'true'
    '--output', $destination
    '-p:PublishSingleFile=true'
    '-p:IncludeAllContentForSelfExtract=true'
    '-p:EnableCompressionInSingleFile=true'
    '-p:DebugType=None'
    '-m:1'
)

& dotnet publish $project @publishOptions
if ($LASTEXITCODE -ne 0) {
    throw "Publishing Icon Saver failed (exit code $LASTEXITCODE)."
}

Write-Host "Published $(Join-Path $destination 'IconSaver.exe')"
