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

# Stop a running compile daemon ("Keysharp --daemon") so the freshly-installed
# build is used instead of an older-build daemon that may still be running.
pkill -f '/Keysharp.app/Contents/MacOS/Keysharp --daemon' 2>/dev/null || true

log "Installing Keysharp..."
for app in Keysharp Keyview; do
  if [[ -d "${SRC_DIR}/${app}.app" ]]; then
    rm -rf "/Applications/${app}.app"
    cp -R "${SRC_DIR}/${app}.app" "/Applications/"
    log "  installed /Applications/${app}.app"
  else
    log "  warning: ${SRC_DIR}/${app}.app not found, skipping" >&2
  fi
done

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
