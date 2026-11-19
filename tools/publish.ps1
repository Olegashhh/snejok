param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\WinterDesktop.App\WinterDesktop.App.csproj'
$outDir = Join-Path $root 'publish'
$zip = Join-Path $outDir "WinterDesktop-$Runtime.zip"

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -o $outDir

Compress-Archive -Path (Join-Path $outDir 'WinterDesktop.exe') -DestinationPath $zip -Force

Write-Output "Done: $zip"
