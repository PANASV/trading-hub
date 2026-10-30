# Arma los instaladores de Windows (x64 y ARM64) en una PC con Windows: publica la app self-contained y la empaqueta con
# NSIS (winget install NSIS.NSIS). Uso: .\build-windows.ps1 [-Version 1.1.0]
# Deja dist\TradingHub-Setup-x64.exe (PC con Intel o AMD) y dist\TradingHub-Setup-arm64.exe (Windows en ARM).
param([string]$Version = "")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
if (-not $Version) {
  $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
}
$makensis = Get-Command makensis -ErrorAction SilentlyContinue
$makensis = if ($makensis) { $makensis.Source } else { Join-Path ${env:ProgramFiles(x86)} "NSIS\makensis.exe" }
if (-not (Test-Path $makensis)) { throw "Falta NSIS: instalalo con «winget install NSIS.NSIS»" }
New-Item -ItemType Directory -Force dist | Out-Null
foreach ($arch in "x64", "arm64") {
  $out = "dist\win-$arch"
  if (Test-Path $out) { Remove-Item -Recurse -Force $out }
  dotnet publish src\TradingHub.App\TradingHub.App.csproj -c Release -r "win-$arch" --self-contained true -o $out -v q "-p:Version=$Version"
  if ($LASTEXITCODE) { throw "dotnet publish falló ($arch)" }
  & $makensis -V2 "-DVERSION=$Version" "-DARCH=$arch" "-DSRC=$PWD\$out" "-DOUT=$PWD\dist\TradingHub-Setup-$arch.exe" installer\TradingHub.nsi
  if ($LASTEXITCODE) { throw "makensis falló ($arch)" }
  Write-Host "Listo: dist\TradingHub-Setup-$arch.exe"
}
