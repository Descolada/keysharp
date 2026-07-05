#!/usr/bin/env bash
# Uninstalls Keysharp and Keyview from this Mac, regardless of whether they were
# installed via the .pkg (system-wide, /Applications + /usr/local/bin) or the
# .dmg (drag-to-Applications, no terminal commands).
#
# Removes: the app bundles, terminal shims, the .pkg receipt (if present), and
# the per-user Application Support / Preferences / Caches data. Does not revoke
# Accessibility / Input Monitoring / Screen Recording permissions — macOS only
# lets the user do that, from System Settings -> Privacy & Security.
if [ -z "${BASH_VERSION:-}" ]; then exec /usr/bin/env bash "$0" "$@"; fi
set -euo pipefail

PKG_IDENTIFIER="org.keysharp.pkg"
APP_PATHS=(
  "/Applications/Keysharp.app"
  "/Applications/Keyview.app"
  "${HOME}/Applications/Keysharp.app"
  "${HOME}/Applications/Keyview.app"
)
SHIM_PATHS=(
  "/usr/local/bin/keysharp"
  "/usr/local/bin/keyview"
  "/usr/local/bin/keysharp-uninstall"
  "${HOME}/.local/bin/AutoHotkey.exe"
)
DATA_PATHS=(
  "${HOME}/Library/Application Support/Keysharp"
)

log() { printf '%s\n' "$*"; }

# Re-exec under sudo if any target we need to remove is root-owned (system .pkg
# installs apps and shims as root; .dmg drag-installs are owned by the user).
needs_sudo() {
  for path in "${APP_PATHS[@]}" "${SHIM_PATHS[@]}"; do
    [[ -e "${path}" && ! -w "${path}" ]] && return 0
  done
  return 1
}

if [[ "${EUID}" -ne 0 ]] && needs_sudo; then
  log "Some Keysharp files are owned by root; re-running with sudo..."
  exec sudo /usr/bin/env bash "$0" "$@"
fi

log "Stopping Keysharp and Keyview if they are running..."
# Kill the daemon AND any running scripts (pkill matches by path; killall by name as a fallback), so no
# stale instance keeps holding the global input hook / permissions after removal.
pkill -f 'Keysharp.app/Contents/MacOS/Keysharp' 2>/dev/null || true
pkill -f 'Keyview.app/Contents/MacOS/Keyview' 2>/dev/null || true
killall Keysharp Keyview 2>/dev/null || true

log "Removing app bundles..."
for app in "${APP_PATHS[@]}"; do
  if [[ -e "${app}" ]]; then
    rm -rf "${app}"
    log "  removed ${app}"
  fi
done

log "Removing terminal commands..."
for shim in "${SHIM_PATHS[@]}"; do
  if [[ -e "${shim}" ]]; then
    rm -f "${shim}"
    log "  removed ${shim}"
  fi
done

if pkgutil --pkg-info "${PKG_IDENTIFIER}" >/dev/null 2>&1; then
  log "Forgetting package receipt ${PKG_IDENTIFIER}..."
  pkgutil --forget "${PKG_IDENTIFIER}" || true
fi

log "Removing stored settings and cached data..."
for data in "${DATA_PATHS[@]}"; do
  if [[ -e "${data}" ]]; then
    rm -rf "${data}"
    log "  removed ${data}"
  fi
done
shopt -s nullglob
for pattern in "${HOME}/Library/Preferences/org.keysharp."* "${HOME}/Library/Caches/org.keysharp."*; do
  if [[ -e "${pattern}" ]]; then
    rm -rf "${pattern}"
    log "  removed ${pattern}"
  fi
done
shopt -u nullglob

log ""
log "Keysharp has been uninstalled."
log "macOS may still show Accessibility, Input Monitoring, or Screen Recording entries"
log "for Keysharp/Keyview. Remove them from System Settings -> Privacy & Security if desired."
