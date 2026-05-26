#!/usr/bin/env bash
if [ -z "${BASH_VERSION:-}" ]; then exec /usr/bin/env bash "$0" "$@"; fi
set -euo pipefail

ROOT_INSTALL=false
if [[ "${EUID}" -eq 0 ]]; then
  ROOT_INSTALL=true
fi

if [[ -z "${PREFIX:-}" ]]; then
  if [[ "${ROOT_INSTALL}" == "true" ]]; then
    PREFIX="/usr/local"
  else
    PREFIX="${HOME}/.local"
  fi
fi

XDG_DATA_HOME="${XDG_DATA_HOME:-${HOME}/.local/share}"
APP_DIR_TARGET="${PREFIX}/lib/keysharp"
BINDIR="${PREFIX}/bin"
SYSTEMD_DIR="/etc/systemd/system"
if [[ "${ROOT_INSTALL}" == "true" ]]; then
  DESKTOP_DIR="/usr/share/applications"
  MIME_ROOT="/usr/share/mime"
  ICON_ROOT="/usr/share/icons/hicolor"
else
  DESKTOP_DIR="${XDG_DATA_HOME}/applications"
  MIME_ROOT="${XDG_DATA_HOME}/mime"
  ICON_ROOT="${XDG_DATA_HOME}/icons/hicolor"
fi
MIME_DIR="${MIME_ROOT}/packages"
ICON_DIR="${ICON_ROOT}/256x256/apps"
maybe_run() { command -v "$1" >/dev/null 2>&1 && "$@"; }

echo "Uninstalling from ${APP_DIR_TARGET} (prefix=${PREFIX})"

if [[ "${ROOT_INSTALL}" == "true" ]]; then
  maybe_run systemctl disable --now keysharp-inputd.socket || true
  maybe_run systemctl stop keysharp-inputd.service || true
  rm -f "${SYSTEMD_DIR}/keysharp-inputd.service" "${SYSTEMD_DIR}/keysharp-inputd.socket"
  maybe_run systemctl daemon-reload || true
fi

rm -f "${BINDIR}/keysharp" "${BINDIR}/keyview" "${BINDIR}/keysharp-trust" "${BINDIR}/keysharp-inputd"
rm -f "${DESKTOP_DIR}/keyview.desktop" "${DESKTOP_DIR}/keysharp.desktop" "${DESKTOP_DIR}/keysharp-kwin-screencap.desktop" "${MIME_DIR}/keysharp.xml" "${ICON_DIR}/keysharp.png"
rm -rf "${APP_DIR_TARGET}"

maybe_run update-desktop-database "${DESKTOP_DIR}" || true
maybe_run update-mime-database "${MIME_ROOT}" || true
maybe_run gtk-update-icon-cache -f "${ICON_ROOT}" || true

echo "Uninstall complete."
