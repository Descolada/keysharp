#!/usr/bin/env bash
# Installs Keysharp and Keyview from this DMG into /Applications, then offers
# to add the `keysharp` / `keyview` terminal commands and the VS Code
# AutoHotkey v2 extension compatibility shim.
if [ -z "${BASH_VERSION:-}" ]; then exec /usr/bin/env bash "$0" "$@"; fi
set -euo pipefail

SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

log() { printf '%s\n' "$*"; }

ask_yes_no() {
  local prompt="$1"
  local answer
  read -r -p "${prompt} [y/N] " answer </dev/tty || answer=""
  [[ "${answer}" =~ ^[Yy] ]]
}

# Stop ALL running Keysharp/Keyview instances (the compile daemon AND any running scripts), not just the
# daemon: a lingering old-build instance keeps holding the global input hook and its granted permissions,
# so newly-launched scripts misbehave until it is killed. pkill matches by path; killall is a name-based
# fallback (both target the current user's processes here).
pkill -f 'Keysharp.app/Contents/MacOS/Keysharp' 2>/dev/null || true
pkill -f 'Keyview.app/Contents/MacOS/Keyview' 2>/dev/null || true
killall Keysharp Keyview 2>/dev/null || true

log "Installing Keysharp..."
# A prior .pkg install lays the bundles down root-owned in /Applications; a plain
# user cannot rm/overwrite them, and under `set -e` the failed rm would abort the
# whole install, silently leaving the old version in place. Escalate JUST the bundle
# replacement when an existing bundle is not user-writable, so the later per-user
# shim steps still run as the invoking user (a blanket `exec sudo` would resolve
# ${HOME} to root's and mis-target the ~/.local shim). Stored as a plain string, not
# an array, so it stays safe under macOS's bash 3.2 + `set -u` when empty.
app_sudo=""
for app in Keysharp Keyview; do
  if [[ -e "/Applications/${app}.app" && ! -w "/Applications/${app}.app" ]]; then
    app_sudo="sudo"
    log "Existing Keysharp is owned by root (installed via the .pkg); requesting an administrator password to replace it."
    break
  fi
done

for app in Keysharp Keyview; do
  if [[ -d "${SRC_DIR}/${app}.app" ]]; then
    ${app_sudo} rm -rf "/Applications/${app}.app"
    ${app_sudo} cp -R "${SRC_DIR}/${app}.app" "/Applications/"
    log "  installed /Applications/${app}.app"
  else
    log "  warning: ${SRC_DIR}/${app}.app not found, skipping" >&2
  fi
done

# If this drag/DMG install just replaced a prior .pkg install, forget the stale
# package receipt so `pkgutil` (and the uninstaller's receipt check) no longer
# reports a system .pkg install that no longer reflects reality.
if pkgutil --pkg-info org.keysharp.pkg >/dev/null 2>&1; then
  ${app_sudo} pkgutil --forget org.keysharp.pkg >/dev/null 2>&1 || true
fi

# The .pkg ships an on-PATH uninstaller at /usr/local/bin/keysharp-uninstall; the
# .dmg does not. If a prior .pkg left one behind it is now frozen at that OLD
# version, so refresh it in place from this DMG's uninstaller (Uninstall.command is
# the current uninstall.sh) instead of leaving a stale copy on PATH. We only touch
# it when it already exists — a pure drag-install still uninstalls via the DMG's
# Uninstall.command. sudo only when the file or its dir is not user-writable (the
# .pkg installed it root-owned).
if [[ -e "/usr/local/bin/keysharp-uninstall" && -f "${SRC_DIR}/Uninstall.command" ]]; then
  uninstall_sudo=""
  if [[ ! -w "/usr/local/bin/keysharp-uninstall" || ! -w "/usr/local/bin" ]]; then
    uninstall_sudo="sudo"
  fi
  ${uninstall_sudo} install -m 0755 "${SRC_DIR}/Uninstall.command" "/usr/local/bin/keysharp-uninstall"
  log "Refreshed the /usr/local/bin/keysharp-uninstall command to this version."
fi

# Remove TCC permission entries created under an incorrectly-cased bundle id (org.keysharp.Keysharp /
# org.keysharp.Keyview) by earlier or ad-hoc-signed builds. The canonical ids are all-lowercase
# (org.keysharp.keysharp / org.keysharp.keyview); leaving the mis-cased duplicates around splits the app's
# permissions across two identities (e.g. Input Monitoring granted to one but read from the other), which
# shows up as a permission that "won't stick". Harmless if the entries don't exist.
for badid in org.keysharp.Keysharp org.keysharp.Keyview; do
  tccutil reset All "${badid}" >/dev/null 2>&1 || true
done

write_shim() {
  local destination="$1"
  local executable="$2"
  local temporary
  temporary="$(mktemp)"
  printf '#!/bin/sh\nexec "%s" "$@"\n' "${executable}" > "${temporary}"
  chmod 0755 "${temporary}"

  if mkdir -p "$(dirname "${destination}")" 2>/dev/null && [[ -w "$(dirname "${destination}")" ]]; then
    install -m 0755 "${temporary}" "${destination}"
  else
    sudo mkdir -p "$(dirname "${destination}")"
    sudo install -m 0755 "${temporary}" "${destination}"
  fi

  rm -f "${temporary}"
}

log ""
if ask_yes_no "Install the 'keysharp' and 'keyview' terminal commands? (requires an administrator password)"; then
  write_shim "/usr/local/bin/keysharp" "/Applications/Keysharp.app/Contents/MacOS/Keysharp"
  write_shim "/usr/local/bin/keyview" "/Applications/Keyview.app/Contents/MacOS/Keyview"
  log "Installed /usr/local/bin/keysharp and /usr/local/bin/keyview"
fi

log ""
if ask_yes_no "Install the VS Code AutoHotkey v2 extension compatibility shim (~/.local/bin/AutoHotkey.exe)?"; then
  destination="${HOME}/.local/bin/AutoHotkey.exe"
  mkdir -p "$(dirname "${destination}")"
  printf '#!/bin/sh\nexec "/Applications/Keysharp.app/Contents/MacOS/Keysharp" "$@"\n' > "${destination}"
  chmod 0755 "${destination}"
  log "Installed ${destination}"
  log "Set \"AutoHotkey2.InterpreterPath\" to \"${destination}\" in VS Code settings.json"
fi

log ""
log "Keysharp installation complete."
