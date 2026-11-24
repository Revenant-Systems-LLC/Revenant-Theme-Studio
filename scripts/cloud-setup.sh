#!/usr/bin/env bash
# One-shot setup for a Linux cloud session: install the .NET SDK this repo
# needs, then build the solution in Release.
#
# The -windows projects compile here because of EnableWindowsTargeting in
# Directory.Build.props. The WPF app and the WPF test project can't run on
# Linux (there is no Microsoft.WindowsDesktop.App runtime), so this script
# builds only.
#
# Usage: bash scripts/cloud-setup.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SDK_CHANNELS=("10.0")

have_sdk() {
  command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q "^$1\."
}

install_sdk() {
  local channel="$1"
  if have_sdk "$channel"; then
    echo "SDK $channel already installed."
    return
  fi
  # Ubuntu ships the SDKs in its own archive. Use that first, because some
  # sandboxes block the Microsoft download host that dotnet-install.sh uses.
  if command -v apt-get >/dev/null 2>&1 && apt-cache show "dotnet-sdk-$channel" >/dev/null 2>&1; then
    local sudo=""
    [ "$(id -u)" -ne 0 ] && sudo="sudo"
    $sudo env DEBIAN_FRONTEND=noninteractive apt-get install -y "dotnet-sdk-$channel"
  else
    local installer
    installer="$(mktemp)"
    curl -sSL https://dot.net/v1/dotnet-install.sh -o "$installer"
    bash "$installer" --channel "$channel" --install-dir "$HOME/.dotnet"
    rm -f "$installer"
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
  fi
}

if command -v apt-get >/dev/null 2>&1 && ! apt-cache show "dotnet-sdk-${SDK_CHANNELS[0]}" >/dev/null 2>&1; then
  sudo_cmd=""
  [ "$(id -u)" -ne 0 ] && sudo_cmd="sudo"
  $sudo_cmd apt-get update -y >/dev/null || true
fi

for channel in "${SDK_CHANNELS[@]}"; do
  install_sdk "$channel"
done

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
dotnet --list-sdks

cd "$REPO_ROOT"
dotnet build Revenant-Theme-Studio.sln -c Release
