#!/bin/sh
# RunnerWatch installer for Linux and macOS (per user, no sudo).
#
#   curl -fsSL https://raw.githubusercontent.com/GustavoDanielL/runnerwatch/main/install.sh | sh
#
# RUNNERWATCH_VERSION=v0.2.0 pins a version. On macOS, installing with curl avoids the Gatekeeper
# "downloaded from the internet" block that a browser download would trigger (builds aren't notarized yet).
set -eu

REPO="GustavoDanielL/runnerwatch"
VERSION="${RUNNERWATCH_VERSION:-latest}"

step() { printf '\033[36m==>\033[0m %s\n' "$1"; }
die() { printf '\033[31merror:\033[0m %s\n' "$1" >&2; exit 1; }

os=$(uname -s)
arch=$(uname -m)
case "$arch" in
  x86_64|amd64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) die "unsupported CPU: $arch" ;;
esac
case "$os" in
  Linux) rid="linux-$arch"; file="RunnerWatch-$rid.tar.gz" ;;
  Darwin) rid="osx-$arch"; file="RunnerWatch-$rid.zip" ;;
  *) die "unsupported OS: $os (on Windows use install.ps1)" ;;
esac

if [ "$VERSION" = latest ]; then base="https://github.com/$REPO/releases/latest/download"
else base="https://github.com/$REPO/releases/download/$VERSION"; fi

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT INT TERM

step "Downloading $file ($VERSION)"
curl -fsSL "$base/$file" -o "$tmp/$file"
curl -fsSL "$base/SHA256SUMS.txt" -o "$tmp/SHA256SUMS.txt"

expected=$(grep " $file\$" "$tmp/SHA256SUMS.txt" | awk '{print $1}')
if command -v sha256sum >/dev/null 2>&1; then actual=$(sha256sum "$tmp/$file" | awk '{print $1}')
else actual=$(shasum -a 256 "$tmp/$file" | awk '{print $1}'); fi
[ -n "$expected" ] && [ "$expected" = "$actual" ] || die "checksum mismatch for $file"

pkill -x RunnerWatch 2>/dev/null || true

if [ "$os" = Darwin ]; then
  dest="$HOME/Applications/RunnerWatch.app"
  step "Installing to $dest"
  mkdir -p "$HOME/Applications"
  rm -rf "$dest"
  ditto -x -k "$tmp/$file" "$HOME/Applications"
  xattr -dr com.apple.quarantine "$dest" 2>/dev/null || true
  open "$dest"
else
  data="${XDG_DATA_HOME:-$HOME/.local/share}"
  dest="$data/runnerwatch"
  bin="$HOME/.local/bin"
  step "Installing to $dest"
  rm -rf "$dest"
  mkdir -p "$dest" "$bin" "$data/applications" "$data/icons/hicolor/256x256/apps"
  tar -xzf "$tmp/$file" -C "$dest"
  chmod +x "$dest/RunnerWatch"
  ln -sf "$dest/RunnerWatch" "$bin/runnerwatch"
  cp "$dest/icon-256.png" "$data/icons/hicolor/256x256/apps/runnerwatch.png"
  sed "s|^Exec=.*|Exec=$dest/RunnerWatch|" "$dest/runnerwatch.desktop" > "$data/applications/runnerwatch.desktop"
  command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" 2>/dev/null || true
  nohup "$dest/RunnerWatch" >/dev/null 2>&1 &
  case ":$PATH:" in *":$bin:"*) ;; *) echo "Note: add $bin to your PATH to run 'runnerwatch' from a terminal." ;; esac
  echo "GNOME: the tray icon needs the AppIndicator extension (preinstalled on Ubuntu)."
fi

printf '\033[32mRunnerWatch is running.\033[0m Settings and log: tray/menu-bar icon.\n'
echo "Uninstall: curl -fsSL https://raw.githubusercontent.com/$REPO/main/scripts/uninstall.sh | sh"
