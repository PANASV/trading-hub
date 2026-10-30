#!/bin/zsh
# Arma los instaladores de Windows (x64 y ARM64) desde una Mac o Linux: publica la app self-contained y la empaqueta con
# NSIS (en Mac: brew install makensis). Uso: ./build-windows.sh [versión]
# Deja dist/TradingHub-Setup-x64.exe (PC con Intel o AMD) y dist/TradingHub-Setup-arm64.exe (Windows en ARM).
set -e
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# makensis lee el script en UTF-8 solo con un locale UTF-8 (en una terminal con «C» falla con «Bad text encoding»)
export LC_ALL=en_US.UTF-8 LANG=en_US.UTF-8
VERSION="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)}"
command -v makensis >/dev/null || { echo "Falta makensis (en Mac: brew install makensis)"; exit 1; }
mkdir -p dist
for ARCH in x64 arm64; do
  OUT="dist/win-$ARCH"
  rm -rf "$OUT"
  dotnet publish src/TradingHub.App/TradingHub.App.csproj -c Release -r "win-$ARCH" --self-contained true -o "$OUT" -v q -p:Version="$VERSION"
  makensis -V2 -DVERSION="$VERSION" -DARCH="$ARCH" -DSRC="$PWD/$OUT" -DOUT="$PWD/dist/TradingHub-Setup-$ARCH.exe" installer/TradingHub.nsi
  echo "Listo: dist/TradingHub-Setup-$ARCH.exe"
done
