#!/usr/bin/env bash
if [ -z "${BASH_VERSION:-}" ]; then exec /usr/bin/env bash "$0" "$@"; fi
set -euo pipefail

keysharp_executable=""
for app in "/Applications/Keysharp.app" "${HOME}/Applications/Keysharp.app"; do
  if [[ -x "${app}/Contents/MacOS/Keysharp" ]]; then
    keysharp_executable="${app}/Contents/MacOS/Keysharp"
    break
  fi
done

if [[ -z "${keysharp_executable}" ]] && command -v mdfind >/dev/null 2>&1; then
  while IFS= read -r app; do
    [[ "${app}" == /Volumes/* ]] && continue
    if [[ -x "${app}/Contents/MacOS/Keysharp" ]]; then
      keysharp_executable="${app}/Contents/MacOS/Keysharp"
      break
    fi
  done < <(mdfind "kMDItemCFBundleIdentifier == 'org.keysharp.keysharp'")
fi

if [[ -z "${keysharp_executable}" ]]; then
  printf '%s\n' "Keysharp.app must first be copied to /Applications or ~/Applications." >&2
  exit 1
fi

destination="${HOME}/.local/bin/AutoHotkey.exe"
mkdir -p "$(dirname "${destination}")"
printf '#!/bin/sh\nexec "%s" "$@"\n' "${keysharp_executable}" > "${destination}"
chmod 0755 "${destination}"

printf '%s\n' "Installed AutoHotkey v2 extension compatibility wrapper:"
printf '  %s\n' "${destination}"
printf '\n%s\n' "Add this to VS Code settings.json:"
printf '  "AutoHotkey2.InterpreterPath": "%s"\n' "${destination}"
printf '\n%s\n' "Optional association for Keysharp source files:"
printf '  "files.associations": { "*.ks": "ahk2" }\n'
