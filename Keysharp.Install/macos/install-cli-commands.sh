#!/usr/bin/env bash
if [ -z "${BASH_VERSION:-}" ]; then exec /usr/bin/env bash "$0" "$@"; fi
set -euo pipefail

BIN_DIR="${KEYSHARP_CLI_BIN_DIR:-/usr/local/bin}"

find_app() {
  local name="$1"
  local bundle_name
  bundle_name="$(printf '%s' "${name}" | tr '[:upper:]' '[:lower:]')"

  for app in "/Applications/${name}.app" "${HOME}/Applications/${name}.app"; do
    if [[ -x "${app}/Contents/MacOS/${name}" ]]; then
      printf '%s\n' "${app}/Contents/MacOS/${name}"
      return 0
    fi
  done

  if command -v mdfind >/dev/null 2>&1; then
    while IFS= read -r app; do
      [[ "${app}" == /Volumes/* ]] && continue
      if [[ -x "${app}/Contents/MacOS/${name}" ]]; then
        printf '%s\n' "${app}/Contents/MacOS/${name}"
        return 0
      fi
    done < <(mdfind "kMDItemCFBundleIdentifier == 'org.keysharp.${bundle_name}'")
  fi

  return 1
}

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

keysharp_executable="$(find_app Keysharp || true)"
keyview_executable="$(find_app Keyview || true)"

if [[ -z "${keysharp_executable}" || -z "${keyview_executable}" ]]; then
  printf '%s\n' "Keysharp.app and Keyview.app must first be copied to /Applications or ~/Applications." >&2
  exit 1
fi

printf '%s\n' "Installing keysharp and keyview commands in ${BIN_DIR}..."
write_shim "${BIN_DIR}/keysharp" "${keysharp_executable}"
write_shim "${BIN_DIR}/keyview" "${keyview_executable}"

printf '\n%s\n' "Installed successfully:"
printf '  %s\n' "${BIN_DIR}/keysharp"
printf '  %s\n' "${BIN_DIR}/keyview"
printf '\n%s\n' "Example: keysharp myscript.ks"
