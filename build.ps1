# TokenWatch bauen (Windows, .NET 8 SDK)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet publish src/TokenWatch -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o dist

Write-Host ""
Write-Host "Fertig: $PSScriptRoot\dist\TokenWatch.exe" -ForegroundColor Green
