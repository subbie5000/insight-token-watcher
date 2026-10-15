# Insight Token Watcher bauen (Windows, .NET 8 SDK)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet publish src/InsightTokenWatcher -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o dist

Write-Host ""
Write-Host "Fertig: $PSScriptRoot\dist\InsightTokenWatcher.exe" -ForegroundColor Green
