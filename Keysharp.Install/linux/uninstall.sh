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
GNOME_EXT_UUID="keysharp@keysharp.io"
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

rm -f "${BINDIR}/keysharp" "${BINDIR}/keyview" "${BINDIR}/keysharp-inputd"
rm -f "${DESKTOP_DIR}/keyview.desktop" "${DESKTOP_DIR}/keysharp.desktop" "${DESKTOP_DIR}/keysharp-screencap.desktop" "${MIME_DIR}/keysharp.xml" "${ICON_DIR}/keysharp.png"
rm -rf "${APP_DIR_TARGET}"

# Remove the GNOME Shell extension. When uninstalling as root we check
# SUDO_USER so the extension is removed from the correct user's home.
remove_gnome_extension() {
  local target_user=""
  local target_home="${HOME}"

  if [[ "${ROOT_INSTALL}" == "true" && -n "${SUDO_USER:-}" && "${SUDO_USER}" != "root" ]]; then
    target_user="${SUDO_USER}"
    target_home="$(getent passwd "${target_user}" | cut -d: -f6 2>/dev/null || echo "${HOME}")"
  fi

  local ext_dir="${target_home}/.local/share/gnome-shell/extensions/${GNOME_EXT_UUID}"

  [[ -d "${ext_dir}" ]] || return 0

  if [[ -n "${target_user}" ]]; then
    sudo -u "${target_user}" gnome-extensions disable "${GNOME_EXT_UUID}" 2>/dev/null || true
    rm -rf "${ext_dir}"
  else
    maybe_run gnome-extensions disable "${GNOME_EXT_UUID}" 2>/dev/null || true
    rm -rf "${ext_dir}"
  fi

  echo "GNOME Shell extension removed."
}

remove_gnome_extension

maybe_run update-desktop-database "${DESKTOP_DIR}" || true
maybe_run update-mime-database "${MIME_ROOT}" || true
maybe_run gtk-update-icon-cache -f "${ICON_ROOT}" || true

echo "Uninstall complete."
