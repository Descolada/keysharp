#!/usr/bin/env bash
if [ -z "${BASH_VERSION:-}" ]; then exec /usr/bin/env bash "$0" "$@"; fi
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
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
APP_DIR_SOURCE="${SCRIPT_DIR}/app"
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
INSTALL_DEPS="${INSTALL_DEPS:-true}"
DOTNET_PACKAGE="${DOTNET_PACKAGE:-dotnet-runtime-10.0}"
GNOME_EXT_UUID="keysharp@keysharp.io"
GNOME_EXT_SOURCE="${SCRIPT_DIR}/gnome-shell-extension"
maybe_run() { command -v "$1" >/dev/null 2>&1 && "$@"; }
have_pkg() { command -v "$1" >/dev/null 2>&1; }
rewrite_desktop_exec() {
  local src="$1"
  local dest="$2"
  sed -e "s|/usr/local/bin/|${BINDIR}/|g" \
      -e "s|/usr/local/lib/keysharp/|${APP_DIR_TARGET}/|g" \
      "${src}" > "${dest}"
}
rewrite_systemd_service() {
  local src="$1"
  local dest="$2"
  sed -e "s|@CMAKE_INSTALL_FULL_BINDIR@|${APP_DIR_TARGET}|g" \
      "${src}" > "${dest}"
}
normalize_root_app_permissions() {
  find "${APP_DIR_TARGET}" -type d -exec chmod 0755 {} +
  find "${APP_DIR_TARGET}" -type f -exec chmod 0644 {} +

  for exe in Keysharp Keyview keysharp-inputd keysharp-trust keysharp-screencap; do
    if [[ -f "${APP_DIR_TARGET}/${exe}" ]]; then
      chmod 0755 "${APP_DIR_TARGET}/${exe}"
    fi
  done
}
# Install the GNOME Shell extension. This is always a per-user operation
# because GNOME extensions live in ~/.local/share/gnome-shell/extensions/.
# When running as root via sudo, we target the invoking user ($SUDO_USER).
install_gnome_extension() {
  if [[ ! -d "${GNOME_EXT_SOURCE}" ]]; then
    echo "Warning: GNOME Shell extension source not found at ${GNOME_EXT_SOURCE}; skipping." >&2
    return 0
  fi

  local target_user=""
  local target_home="${HOME}"

  if [[ "${ROOT_INSTALL}" == "true" ]]; then
    if [[ -n "${SUDO_USER:-}" && "${SUDO_USER}" != "root" ]]; then
      target_user="${SUDO_USER}"
      target_home="$(getent passwd "${target_user}" | cut -d: -f6 2>/dev/null || echo "")"
      if [[ -z "${target_home}" ]]; then
        echo "Warning: could not determine home directory for ${target_user}; skipping GNOME extension install." >&2
        return 0
      fi
    else
      # Running as root without sudo (e.g. in a root shell). The GNOME
      # extension must be installed as the desktop user, but we have no way
      # to determine who that is. Print instructions instead.
      cat >&2 <<EOF
Warning: running as root without SUDO_USER set.
To install the GNOME Shell extension for your desktop user, run as that user:
  cp -r "${GNOME_EXT_SOURCE}/." "\${HOME}/.local/share/gnome-shell/extensions/${GNOME_EXT_UUID}/"
  gnome-extensions enable "${GNOME_EXT_UUID}"
  (then log out and back in)
EOF
      return 0
    fi
  fi

  local ext_dir="${target_home}/.local/share/gnome-shell/extensions/${GNOME_EXT_UUID}"
  echo "Installing GNOME Shell extension to ${ext_dir}..."

  local ext_registered=false
  if [[ -n "${target_user}" ]]; then
    sudo -u "${target_user}" mkdir -p "${ext_dir}"
    sudo -u "${target_user}" cp -r "${GNOME_EXT_SOURCE}/." "${ext_dir}/"
    if gnome_preregister_extension "${target_user}"; then
      ext_registered=true
    fi
  else
    mkdir -p "${ext_dir}"
    cp -r "${GNOME_EXT_SOURCE}/." "${ext_dir}/"
    if gnome_preregister_extension ""; then
      ext_registered=true
    fi
  fi

  echo "GNOME Shell extension installed (uuid: ${GNOME_EXT_UUID})."
  if [[ "${ext_registered}" == "true" ]]; then
    echo "Log out and back in to activate it (Wayland requires a session restart for new extensions)."
  else
    cat >&2 <<EOF
Could not automatically enable the GNOME Shell extension (no active D-Bus
session was detected for the desktop user). To enable it, run as your
desktop user:
  gnome-extensions enable "${GNOME_EXT_UUID}"
or open the GNOME Extensions app and enable 'Keysharp Integration'.
Then log out and back in.
EOF
  fi
}

# Add the extension UUID to org.gnome.shell enabled-extensions via gsettings.
# Returns 0 if the UUID was successfully registered (or was already present),
# 1 if enabling could not be attempted (python3/gsettings missing, or no
# active D-Bus session for the target user).
gnome_preregister_extension() {
  local as_user="${1}"
  command -v python3 >/dev/null 2>&1 || return 1
  command -v gsettings >/dev/null 2>&1 || return 1

  local script
  script=$(cat <<'PYEOF'
import subprocess, json, sys
uuid = sys.argv[1]
r = subprocess.run(['gsettings', 'get', 'org.gnome.shell', 'enabled-extensions'],
                   capture_output=True, text=True)
val = r.stdout.strip().lstrip('@as ')
try:
    exts = json.loads(val.replace("'", '"'))
except Exception:
    exts = []
if uuid in exts:
    sys.exit(0)
exts.append(uuid)
new_val = '[' + ', '.join(f"'{e}'" for e in exts) + ']'
result = subprocess.run(['gsettings', 'set', 'org.gnome.shell', 'enabled-extensions', new_val])
sys.exit(result.returncode)
PYEOF
)

  if [[ -n "${as_user}" ]]; then
    local uid
    uid=$(id -u "${as_user}" 2>/dev/null) || return 1
    # /run/user/<uid>/bus is the standard sd-bus socket (systemd/logind).
    # If it doesn't exist the user has no live D-Bus session and gsettings
    # won't work — return 1 so the caller shows manual instructions.
    [[ -S "/run/user/${uid}/bus" ]] || return 1
    sudo -u "${as_user}" \
      env DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/${uid}/bus" \
      python3 -c "${script}" "${GNOME_EXT_UUID}" 2>/dev/null
  else
    python3 -c "${script}" "${GNOME_EXT_UUID}" 2>/dev/null
  fi
}

show_install_mode() {
  if [[ "${ROOT_INSTALL}" == "true" ]]; then
    cat <<EOF
Installing with root privileges.
Optional Linux helpers will be enabled when present:
  - keysharp-inputd: systemd socket service for more reliable input hooks, input synthesis, and BlockInput support.
  - keysharp-trust: permission records and reset/list tooling for privileged helper decisions.
  - keysharp-screencap: Wayland screen capture helper (KWin ScreenShot2 serve mode; trust gate for GNOME).

This install may add systemd units, enable the keysharp-inputd socket, load uinput, and mark the KDE helper root-owned setuid.
EOF
  else
    cat <<EOF
Installing without root privileges.
Keysharp will be installed under ${PREFIX}. Optional privileged Linux helpers will be skipped, so Linux input hooks/synthesis and Wayland screen capture may be unavailable until a root install is performed.
EOF
  fi
}

has_dotnet10() {
  command -v dotnet >/dev/null 2>&1 && dotnet --list-runtimes | grep -q 'Microsoft.NETCore.App 10\.'
}

install_deps() {
  # Eto.Forms Gtk backend requires GTK3; libnotify is used for notifications; AT-SPI2 supports accessibility hooks.
  local packages_apt=(libx11-6 libxtst6 libxinerama1 libxt6 libx11-xcb1 libxkbcommon-x11-0 libxcb-xtest0 libgtk-3-0 libglib2.0-0 libnotify4 libatspi2.0-0 at-spi2-core pulseaudio-utils libudev1 libevdev2 systemd kmod)
  local packages_dnf=(libX11 libXtst libXinerama libXt libxkbcommon-x11 libxcb libX11-xcb gtk3 glib2 libnotify at-spi2-core systemd-libs libevdev systemd kmod)
  local packages_yum=(libX11 libXtst libXinerama libXt libxcb xorg-x11-xkb-utils gtk3 glib2 libnotify at-spi2-core systemd-libs libevdev systemd kmod)
  local packages_zypper=(libX11-6 libXtst6 libXinerama1 libXt6 libxkbcommon-x11-0 libxcb1 gtk3 glib2 libnotify4 at-spi2-core libudev1 libevdev2 systemd kmod)
  local packages_pacman=(libx11 libxtst libxinerama libxt libxkbcommon-x11 libxcb gtk3 glib2 libnotify at-spi2-core systemd libevdev kmod)

  if ! has_dotnet10; then
    packages_apt+=("${DOTNET_PACKAGE}")
    packages_dnf+=("${DOTNET_PACKAGE}")
    packages_yum+=("${DOTNET_PACKAGE}")
    packages_zypper+=("${DOTNET_PACKAGE}")
    packages_pacman+=(dotnet-runtime)
  fi

  if have_pkg apt-get; then
    echo "Installing runtime deps via apt-get..."
    if ! apt-get update; then
      echo "Warning: apt-get update failed; trying dependency install with the existing package indexes." >&2
    fi
    if ! DEBIAN_FRONTEND=noninteractive apt-get install -y "${packages_apt[@]}"; then
      echo "Warning: apt-get install exited non-zero (an unrelated package may have failed to configure)." >&2
      echo "Continuing — check_dotnet will verify the critical .NET 10 runtime is present." >&2
    fi
    return
  fi

  if have_pkg dnf; then
    echo "Installing runtime deps via dnf..."
    dnf install -y "${packages_dnf[@]}"
    return
  fi

  if have_pkg yum; then
    echo "Installing runtime deps via yum..."
    yum install -y "${packages_yum[@]}"
    return
  fi

  if have_pkg zypper; then
    echo "Installing runtime deps via zypper..."
    zypper install -y "${packages_zypper[@]}"
    return
  fi

  if have_pkg pacman; then
    echo "Installing runtime deps via pacman..."
    pacman -Sy --noconfirm "${packages_pacman[@]}"
    return
  fi

  echo "Package manager not detected; please ensure .NET 10, X11 libs, GTK3, libnotify, and optionally AT-SPI2 are installed." >&2
}

check_dotnet() {
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet runtime not found. Install ${DOTNET_PACKAGE} or rebuild self-contained." >&2
    exit 1
  fi

  if ! dotnet --list-runtimes | grep -q 'Microsoft.NETCore.App 10\.'; then
    echo ".NET 10 runtime missing. Install ${DOTNET_PACKAGE} or rebuild self-contained." >&2
    exit 1
  fi
}

if [[ ! -d "${APP_DIR_SOURCE}" ]]; then
  echo "Expected app payload at ${APP_DIR_SOURCE}; aborting." >&2
  exit 1
fi

show_install_mode

if [[ "${INSTALL_DEPS}" == "true" ]]; then
  if [[ "${ROOT_INSTALL}" == "true" ]]; then
    install_deps
  else
    echo "Skipping dependency install because this is a user install. Ensure .NET 10 and runtime libraries are present."
  fi
else
  echo "Skipping dependency install (INSTALL_DEPS=false). Ensure X11 libs are present."
fi

check_dotnet

echo "Installing to ${APP_DIR_TARGET} (prefix=${PREFIX})"
mkdir -p "${APP_DIR_TARGET}" "${BINDIR}"
cp -a "${APP_DIR_SOURCE}/." "${APP_DIR_TARGET}/"

if [[ "${ROOT_INSTALL}" == "true" ]]; then
  chown -R root:root "${APP_DIR_TARGET}"
  normalize_root_app_permissions
fi

ln -sf "${APP_DIR_TARGET}/Keysharp" "${BINDIR}/keysharp"
ln -sf "${APP_DIR_TARGET}/Keyview" "${BINDIR}/keyview"

if [[ "${ROOT_INSTALL}" == "true" ]]; then
  if [[ -f "${APP_DIR_TARGET}/keysharp-trust" ]]; then
    ln -sf "${APP_DIR_TARGET}/keysharp-trust" "${BINDIR}/keysharp-trust"
  fi

  if [[ -f "${APP_DIR_TARGET}/keysharp-inputd" ]]; then
    ln -sf "${APP_DIR_TARGET}/keysharp-inputd" "${BINDIR}/keysharp-inputd"

    if [[ -f "${SCRIPT_DIR}/keysharp-inputd.service.in" && -f "${SCRIPT_DIR}/keysharp-inputd.socket" ]]; then
      install -d "${SYSTEMD_DIR}"
      rewrite_systemd_service "${SCRIPT_DIR}/keysharp-inputd.service.in" "${SYSTEMD_DIR}/keysharp-inputd.service"
      install -m 0644 "${SCRIPT_DIR}/keysharp-inputd.socket" "${SYSTEMD_DIR}/keysharp-inputd.socket"
      maybe_run systemctl daemon-reload || true

      if ! "${APP_DIR_TARGET}/keysharp-inputd" --install-input-access; then
        echo "Warning: keysharp-inputd service setup did not complete. Input automation helper may be unavailable." >&2
      fi
    else
      echo "Warning: keysharp-inputd systemd unit files were not found in the installer payload." >&2
    fi
  fi

  if [[ -f "${APP_DIR_TARGET}/keysharp-screencap" ]]; then
    chown root:root "${APP_DIR_TARGET}/keysharp-screencap"
    chmod 4755 "${APP_DIR_TARGET}/keysharp-screencap"
  fi
else
  rm -f "${APP_DIR_TARGET}/keysharp-inputd" \
        "${APP_DIR_TARGET}/keysharp-trust" \
        "${APP_DIR_TARGET}/keysharp-screencap"
  echo "Installed in user mode; privileged Linux helpers were skipped."
fi

install -d "${DESKTOP_DIR}"
rewrite_desktop_exec "${SCRIPT_DIR}/keyview.desktop" "${DESKTOP_DIR}/keyview.desktop"
rewrite_desktop_exec "${SCRIPT_DIR}/keysharp.desktop" "${DESKTOP_DIR}/keysharp.desktop"
if [[ "${ROOT_INSTALL}" == "true" && -f "${APP_DIR_TARGET}/keysharp-screencap" ]]; then
  rewrite_desktop_exec "${SCRIPT_DIR}/keysharp-screencap.desktop" "${DESKTOP_DIR}/keysharp-screencap.desktop"
fi
install -Dm644 "${SCRIPT_DIR}/keysharp.xml" "${MIME_DIR}/keysharp.xml"
install -Dm644 "${SCRIPT_DIR}/Keysharp.png" "${ICON_DIR}/keysharp.png"

maybe_run update-desktop-database "${DESKTOP_DIR}" || true
maybe_run update-mime-database "${MIME_ROOT}" || true
maybe_run gtk-update-icon-cache -f "${ICON_ROOT}" || true

install_gnome_extension

echo "Install complete."
