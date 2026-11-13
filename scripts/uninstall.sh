#!/bin/sh
# Removes RunnerWatch on Linux/macOS. --purge also deletes the settings, the log and the saved token.
set -u
pkill -x RunnerWatch 2>/dev/null || true

if [ "$(uname -s)" = Darwin ]; then
  rm -rf "$HOME/Applications/RunnerWatch.app"
  rm -f "$HOME/Library/LaunchAgents/io.github.gustavodaniell.runnerwatch.plist"
else
  data="${XDG_DATA_HOME:-$HOME/.local/share}"
  rm -rf "$data/runnerwatch"
  rm -f "$HOME/.local/bin/runnerwatch" "$data/applications/runnerwatch.desktop" \
        "$data/icons/hicolor/256x256/apps/runnerwatch.png" \
        "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/runnerwatch.desktop"
fi

if [ "${1:-}" = --purge ]; then
  rm -rf "${XDG_CONFIG_HOME:-$HOME/.config}/RunnerWatch" "${XDG_DATA_HOME:-$HOME/.local/share}/RunnerWatch"
  if [ "$(uname -s)" = Darwin ]; then
    rm -rf "$HOME/Library/Application Support/RunnerWatch"   # .NET 8+ maps app data here on macOS
    security delete-generic-password -s RunnerWatch -a github-token >/dev/null 2>&1 || true
  else
    secret-tool clear service RunnerWatch account github-token 2>/dev/null || true
  fi
fi
echo "RunnerWatch removed."
