#!/usr/bin/env bash
if [ -z "${BASH_VERSION:-}" ]; then exec /usr/bin/env bash "$0" "$@"; fi
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ASSETS_DIR="${ROOT}/Keysharp.Install/linux"
CONFIG="${CONFIG:-Release}"
RID="${RID:-linux-x64}"
DIST_DIR="${ROOT}/dist"
ETO_DIR="$(cd "${ROOT}/../Eto" 2>/dev/null && pwd || true)"
PATH_MAP="${ROOT}=/_/keysharp"
if [[ -n "${ETO_DIR}" ]]; then
  PATH_MAP="${PATH_MAP}%2c${ETO_DIR}=/_/Eto"
fi
PUBLISH_DIR="${DIST_DIR}/publish/${RID}"
STAGING_DIR="${DIST_DIR}/staging/${RID}"
PACKAGE_ROOT_DIR="${DIST_DIR}/package-root"
PKG_NAME="keysharp-${RID}"
PKG_DIR="${STAGING_DIR}/${PKG_NAME}"
APP_DIR="${PKG_DIR}/app"
VERSION="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "${ROOT}/Keysharp/Keysharp.csproj" | head -n 1)}"
DEB_PKG_NAME="${DEB_PKG_NAME:-keysharp}"
DEB_TMP_DIR="${PACKAGE_ROOT_DIR}/${PKG_NAME}-deb"
DEB_OUT=""
DEB_ARCH=""
DEB_DEPENDS="dotnet-runtime-10.0, libx11-6, libxtst6, libxinerama1, libxt6, libx11-xcb1, libxkbcommon-x11-0, libxcb-xtest0, libgtk-3-0, libglib2.0-0, libnotify4, libatspi2.0-0, at-spi2-core, pulseaudio-utils, libudev1, libevdev2, systemd, kmod"
DEB_DESCRIPTION="A C# port and enhancement of the AutoHotkey program"
INPUTD_SERVICE_TEMPLATE="${ROOT}/native/keysharp-inputd/systemd/keysharp-inputd.service.in"
INPUTD_SOCKET="${ROOT}/native/keysharp-inputd/systemd/keysharp-inputd.socket"
GNOME_EXT_UUID="keysharp@keysharp.io"
GNOME_EXT_SOURCE="${ASSETS_DIR}/gnome-shell-extension"

if [[ -z "${VERSION}" ]]; then
  echo "Unable to determine package version from Keysharp.csproj. Set VERSION explicitly." >&2
  exit 1
fi

map_rid_to_deb_arch() {
  case "$1" in
    linux-x64) echo "amd64" ;;
    linux-arm64) echo "arm64" ;;
    linux-arm) echo "armhf" ;;
    *)
      echo "Unsupported RID for Debian packaging: $1" >&2
      return 1
      ;;
  esac
}

rewrite_desktop_exec() {
  local src="$1"
  local dest="$2"
  sed -e 's|/usr/local/bin/|/usr/bin/|g' \
      -e 's|/usr/local/lib/keysharp/|/usr/lib/keysharp/|g' \
      "${src}" > "${dest}"
}

build_native_helpers() {
  local inputd_build_dir="${STAGING_DIR}/native-keysharp-inputd-${RID}"
  local kwin_build_dir="${STAGING_DIR}/native-keysharp-screencap-${RID}"

  if [[ "${RID}" != linux-* ]]; then
    return
  fi

  if ! command -v cmake >/dev/null 2>&1; then
    echo "Skipping native helper builds because cmake is not installed." >&2
    return
  fi

  if ! command -v pkg-config >/dev/null 2>&1; then
    echo "Skipping native helper builds because pkg-config is not installed." >&2
    return
  fi

  if pkg-config --exists libudev libevdev; then
    cmake -S "${ROOT}/native/keysharp-inputd" -B "${inputd_build_dir}" \
      -DCMAKE_BUILD_TYPE="${CONFIG}" \
      -DKEYSHARP_INPUTD_BUILD_TOOLS=OFF
    cmake --build "${inputd_build_dir}" --clean-first
    cp "${inputd_build_dir}/keysharp-inputd" "${APP_DIR}/"
  else
    echo "Skipping keysharp-inputd build because libudev or libevdev development files are missing." >&2
  fi

  if ! pkg-config --exists gio-2.0 gio-unix-2.0; then
    echo "Skipping keysharp-screencap build because gio-2.0 development files are missing." >&2
    return
  fi

  cmake -S "${ROOT}/native/keysharp-screencap" -B "${kwin_build_dir}" -DCMAKE_BUILD_TYPE="${CONFIG}"
  cmake --build "${kwin_build_dir}" --clean-first
  cp "${kwin_build_dir}/keysharp-screencap" "${APP_DIR}/"
}

normalize_app_permissions() {
  find "${APP_DIR}" -type d -exec chmod 0755 {} +
  find "${APP_DIR}" -type f -exec chmod 0644 {} +

  for exe in Keysharp Keyview keysharp-inputd keysharp-screencap; do
    if [[ -f "${APP_DIR}/${exe}" ]]; then
      chmod 0755 "${APP_DIR}/${exe}"
    fi
  done
}

verify_no_local_paths() {
  local scan_dir="$1"
  local found=0
  local patterns=("${ROOT}")

  if [[ -n "${HOME:-}" ]]; then
    patterns+=("${HOME}")
  fi

  if [[ -n "${ETO_DIR}" ]]; then
    patterns+=("${ETO_DIR}")
  fi

  echo "Checking packaged files for local absolute paths..."
  for pattern in "${patterns[@]}"; do
    if rg -a -F -n --max-count 20 "${pattern}" "${scan_dir}"; then
      found=1
    fi
  done

  if [[ "${found}" -ne 0 ]]; then
    echo "Package payload contains local absolute paths. Rebuild with path mapping before packaging." >&2
    exit 1
  fi
}

write_deb_control() {
  cat > "$1" <<EOF
Package: ${DEB_PKG_NAME}
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: ${DEB_ARCH}
Maintainer: Descolada
Homepage: https://github.com/Descolada/keysharp
Depends: ${DEB_DEPENDS}
Description: ${DEB_DESCRIPTION}
EOF
}

write_deb_copyright() {
  cat > "$1" <<'EOF'
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: keysharp
Upstream-Contact: Descolada
Source: https://github.com/Descolada/keysharp

Files: *
Copyright: 2020-Present Matt Feemster <matt.feemster@gmail.com>
           2024-Present Descolada
           2010-2015 A. <inspiration3@gmail.com>, Tobias Kappé <tobias@ntlabs.org>
           And other contributors.
License: BSD-2-Clause

Files: PCRE.NET.dll PCRE.NET.Native.so
Copyright: 2014-2022 Lucas Trzesniewski <lucas.trzesniewski@gmail.com>
           1997-2022 University of Cambridge
           2010-2022 Zoltan Herczeg <hzmester@freemail.hu>
           2009-2022 Zoltan Herczeg <hzmester@freemail.hu>
License: BSD-3-Clause

License: BSD-2-Clause
 Redistribution and use in source and binary forms, with or without
 modification, are permitted provided that the following conditions are
 met:
 .
  * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.
  * Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in
    the documentation and/or other materials provided with the
    distribution.
 .
 THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
 A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
 OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
 LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
 DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
 THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

License: BSD-3-Clause
 Redistribution and use in source and binary forms, with or without
 modification, are permitted provided that the following conditions are
 met:
 .
  * Redistributions of source code must retain the above copyright
    notices, this list of conditions and the following disclaimer.
  * Redistributions in binary form must reproduce the above copyright
    notices, this list of conditions and the following disclaimer in
    the documentation and/or other materials provided with the
    distribution.
  * Neither the name of the University of Cambridge nor the names of
    any contributors may be used to endorse or promote products derived
    from this software without specific prior written permission.
 .
 THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
 A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
 OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
 LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
 DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
 THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
EOF
}

write_deb_postinst() {
  cat > "$1" <<'EOF'
#!/bin/sh
set -e

if command -v update-mime-database >/dev/null 2>&1; then
  update-mime-database /usr/share/mime || true
fi

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications || true
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f /usr/share/icons/hicolor || true
fi

# Set keysharp.desktop as the system-wide default handler for .ks and .ahk files.
# Writes to /usr/share/applications/mimeapps.list (freedesktop vendor defaults).
_set_mime_default() {
  local mimeapps="/usr/share/applications/mimeapps.list"
  local mime="$1"
  local app="$2"
  if [ ! -f "$mimeapps" ]; then
    printf '[Default Applications]\n%s=%s\n' "$mime" "$app" > "$mimeapps"
    return
  fi
  if grep -q "^${mime}=" "$mimeapps"; then
    sed -i "s|^${mime}=.*|${mime}=${app}|" "$mimeapps"
  elif grep -q '^\[Default Applications\]' "$mimeapps"; then
    sed -i "/^\[Default Applications\]/a ${mime}=${app}" "$mimeapps"
  else
    printf '\n[Default Applications]\n%s=%s\n' "$mime" "$app" >> "$mimeapps"
  fi
}
_set_mime_default application/x-keysharp  keysharp.desktop
_set_mime_default application/x-autohotkey keysharp.desktop

if [ -f /usr/lib/keysharp/keysharp-screencap ]; then
  echo "Configuring keysharp-screencap for Wayland screen capture (KWin ScreenShot2 serve mode; trust gate for GNOME)."
  chown root:root /usr/lib/keysharp/keysharp-screencap || true
  chmod 4755 /usr/lib/keysharp/keysharp-screencap || true
fi

if [ -f /usr/lib/keysharp/keysharp-inputd ]; then
  echo "Configuring keysharp-inputd for reliable Linux input hooks, input synthesis, and BlockInput."
  if ! /usr/lib/keysharp/keysharp-inputd --install-input-access; then
    cat >&2 <<'WARN'
Warning: keysharp-inputd --install-input-access did not complete successfully.
Linux input hooks, input synthesis, and BlockInput
prompt may be unavailable until this is resolved. Re-run manually as root:
  sudo /usr/lib/keysharp/keysharp-inputd --install-input-access
and check the output for the failing step (modprobe uinput, udevadm, or
systemctl enable --now keysharp-inputd.socket).
WARN
  fi
fi

# GNOME Shell extension: register in the installing user's enabled-extensions.
# The extension files land system-wide at /usr/share/gnome-shell/extensions/
# via dpkg; enabling is always per-user via gsettings.
# /run/user/<uid>/bus is the standard sd-bus socket (systemd/logind).
GNOME_EXT_UUID="keysharp@keysharp.io"
if [ "$1" = "configure" ] && [ -d "/usr/share/gnome-shell/extensions/${GNOME_EXT_UUID}" ]; then
  echo "GNOME Shell extension installed at /usr/share/gnome-shell/extensions/${GNOME_EXT_UUID}"
  _gnome_enabled=false

  if [ -n "${SUDO_USER:-}" ] && [ "${SUDO_USER}" != "root" ]; then
    _sudo_uid=$(id -u "${SUDO_USER}" 2>/dev/null || true)
    if [ -n "${_sudo_uid}" ] && [ -S "/run/user/${_sudo_uid}/bus" ]; then
      sudo -u "${SUDO_USER}" \
        env DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/${_sudo_uid}/bus" \
        python3 << 'PYEOF' 2>/dev/null && _gnome_enabled=true || true
import subprocess, json, sys
uuid = 'keysharp@keysharp.io'
r = subprocess.run(['gsettings', 'get', 'org.gnome.shell', 'enabled-extensions'],
                   capture_output=True, text=True)
val = r.stdout.strip().lstrip('@as ')
try:
    exts = json.loads(val.replace("'", '"'))
except Exception:
    exts = []
if uuid not in exts:
    exts.append(uuid)
    new_val = '[' + ', '.join("'" + e + "'" for e in exts) + ']'
    result = subprocess.run(['gsettings', 'set', 'org.gnome.shell', 'enabled-extensions', new_val])
    sys.exit(result.returncode)
PYEOF
    fi
  fi

  if [ "${_gnome_enabled}" = "true" ]; then
    echo "GNOME Shell extension enabled for ${SUDO_USER}. Log out and back in to activate it."
  else
    cat <<GNOMEMSG
Could not automatically enable the GNOME Shell extension (no active D-Bus
session was detected for the desktop user).  To enable it, run as your
desktop user:
  gnome-extensions enable ${GNOME_EXT_UUID}
or open the GNOME Extensions app and enable 'Keysharp Integration'.
Then log out and back in to activate it.
GNOMEMSG
  fi
fi
EOF
  chmod 0755 "$1"
}

write_deb_prerm() {
  cat > "$1" <<'EOF'
#!/bin/sh
set -e

if [ "$1" = "remove" ] || [ "$1" = "deconfigure" ]; then
  if command -v systemctl >/dev/null 2>&1; then
    systemctl disable --now keysharp-inputd.socket || true
    systemctl stop keysharp-inputd.service || true
  fi

  # Remove system-wide default MIME associations added by postinst.
  _mimeapps="/usr/share/applications/mimeapps.list"
  if [ -f "$_mimeapps" ]; then
    sed -i '/^application\/x-keysharp=keysharp\.desktop$/d'  "$_mimeapps" || true
    sed -i '/^application\/x-autohotkey=keysharp\.desktop$/d' "$_mimeapps" || true
  fi

  # GNOME Shell extension: remove UUID from the user's enabled-extensions list.
  GNOME_EXT_UUID="keysharp@keysharp.io"
  if [ -n "${SUDO_USER:-}" ] && [ "${SUDO_USER}" != "root" ]; then
    _sudo_uid=$(id -u "${SUDO_USER}" 2>/dev/null || true)
    if [ -n "${_sudo_uid}" ] && [ -S "/run/user/${_sudo_uid}/bus" ]; then
      sudo -u "${SUDO_USER}" \
        env DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/${_sudo_uid}/bus" \
        python3 << 'PYEOF' 2>/dev/null || true
import subprocess, json
uuid = 'keysharp@keysharp.io'
r = subprocess.run(['gsettings', 'get', 'org.gnome.shell', 'enabled-extensions'],
                   capture_output=True, text=True)
val = r.stdout.strip().lstrip('@as ')
try:
    exts = json.loads(val.replace("'", '"'))
except Exception:
    exts = []
if uuid in exts:
    exts.remove(uuid)
    new_val = '[' + ', '.join("'" + e + "'" for e in exts) + ']'
    subprocess.run(['gsettings', 'set', 'org.gnome.shell', 'enabled-extensions', new_val])
PYEOF
    fi
  fi
fi

exit 0
EOF
  chmod 0755 "$1"
}

write_deb_postrm() {
  cat > "$1" <<'EOF'
#!/bin/sh
set -e

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications || true
fi

if command -v update-mime-database >/dev/null 2>&1; then
  update-mime-database /usr/share/mime || true
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f /usr/share/icons/hicolor || true
fi

if command -v systemctl >/dev/null 2>&1; then
  systemctl daemon-reload || true
fi
EOF
  chmod 0755 "$1"
}

build_tarball() {
  local tarball="${DIST_DIR}/${PKG_NAME}.tar.gz"
  echo "Creating tarball ${tarball}..."
  tar -czf "${tarball}" -C "${STAGING_DIR}" "${PKG_NAME}"
  echo "Tarball ready at ${tarball}"
}

build_deb() {
  local deb_root="${DEB_TMP_DIR}"
  local debian_dir="${deb_root}/DEBIAN"
  local lib_dir="${deb_root}/usr/lib/keysharp"
  local bin_dir="${deb_root}/usr/bin"
  local applications_dir="${deb_root}/usr/share/applications"
  local mime_dir="${deb_root}/usr/share/mime/packages"
  local icon_dir="${deb_root}/usr/share/icons/hicolor/256x256/apps"
  local systemd_dir="${deb_root}/usr/lib/systemd/system"
  local gnome_ext_dir="${deb_root}/usr/share/gnome-shell/extensions/${GNOME_EXT_UUID}"
  local doc_dir="${deb_root}/usr/share/doc/${DEB_PKG_NAME}"
  local build_cmd=()

  if ! command -v dpkg-deb >/dev/null 2>&1; then
    echo "Skipping Debian package creation because dpkg-deb is not installed."
    return
  fi

  if ! DEB_ARCH="$(map_rid_to_deb_arch "${RID}")"; then
    echo "Skipping Debian package creation for unsupported RID ${RID}."
    return
  fi

  DEB_OUT="${DIST_DIR}/${DEB_PKG_NAME}_${VERSION}_${DEB_ARCH}.deb"
  echo "Creating Debian package ${DEB_OUT}..."
  rm -rf "${deb_root}"
  mkdir -p "${debian_dir}" "${lib_dir}" "${bin_dir}" "${applications_dir}" "${mime_dir}" "${icon_dir}" "${systemd_dir}" "${gnome_ext_dir}" "${doc_dir}"

  rsync -a "${APP_DIR}/" "${lib_dir}/"
  ln -s "../lib/keysharp/Keysharp" "${bin_dir}/keysharp"
  ln -s "../lib/keysharp/Keyview" "${bin_dir}/keyview"
  if [[ -f "${lib_dir}/keysharp-inputd" ]]; then
    ln -s "../lib/keysharp/keysharp-inputd" "${bin_dir}/keysharp-inputd"
  fi
  rewrite_desktop_exec "${ASSETS_DIR}/keysharp.desktop" "${applications_dir}/keysharp.desktop"
  rewrite_desktop_exec "${ASSETS_DIR}/keyview.desktop" "${applications_dir}/keyview.desktop"
  if [[ -f "${lib_dir}/keysharp-screencap" ]]; then
    rewrite_desktop_exec "${ASSETS_DIR}/keysharp-screencap.desktop" "${applications_dir}/keysharp-screencap.desktop"
  fi
  install -Dm644 "${ASSETS_DIR}/keysharp.xml" "${mime_dir}/keysharp.xml"
  install -Dm644 "${ROOT}/Keysharp.png" "${icon_dir}/keysharp.png"

  if [[ -d "${GNOME_EXT_SOURCE}" ]]; then
    cp -r "${GNOME_EXT_SOURCE}/." "${gnome_ext_dir}/"
  fi

  if [[ -f "${lib_dir}/keysharp-inputd" ]]; then
    sed -e 's|@CMAKE_INSTALL_FULL_BINDIR@|/usr/lib/keysharp|g' \
      "${INPUTD_SERVICE_TEMPLATE}" > "${systemd_dir}/keysharp-inputd.service"
    install -m 0644 "${INPUTD_SOCKET}" "${systemd_dir}/keysharp-inputd.socket"
  fi

  if [[ -f "${lib_dir}/keysharp-screencap" ]]; then
    chown 0:0 "${lib_dir}/keysharp-screencap" 2>/dev/null || true
    chmod 4755 "${lib_dir}/keysharp-screencap"
  fi

  write_deb_control "${debian_dir}/control"
  write_deb_postinst "${debian_dir}/postinst"
  write_deb_prerm "${debian_dir}/prerm"
  write_deb_postrm "${debian_dir}/postrm"
  write_deb_copyright "${doc_dir}/copyright"

  find "${deb_root}" -type d -exec chmod 0755 {} +
  find "${deb_root}" -type f -exec chmod 0644 {} +

  for exe in Keysharp Keyview keysharp-inputd; do
    if [[ -f "${lib_dir}/${exe}" ]]; then
      chmod 0755 "${lib_dir}/${exe}"
    fi
  done

  if [[ -f "${lib_dir}/keysharp-screencap" ]]; then
    chmod 4755 "${lib_dir}/keysharp-screencap"
  fi

  chmod 0755 "${debian_dir}/postinst" "${debian_dir}/prerm" "${debian_dir}/postrm"

  if dpkg-deb --help 2>/dev/null | grep -q -- '--root-owner-group'; then
    build_cmd=(dpkg-deb --build --root-owner-group "${deb_root}" "${DEB_OUT}")
  elif command -v fakeroot >/dev/null 2>&1; then
    build_cmd=(fakeroot dpkg-deb --build "${deb_root}" "${DEB_OUT}")
  else
    build_cmd=(dpkg-deb --build "${deb_root}" "${DEB_OUT}")
  fi

  "${build_cmd[@]}"
  echo "Debian package ready at ${DEB_OUT}"
}

echo "Publishing Keysharp and Keyview (CONFIG=${CONFIG}, RID=${RID})..."
mkdir -p "${DIST_DIR}"
rm -rf "${PUBLISH_DIR}/Keysharp" "${PUBLISH_DIR}/Keyview"
dotnet publish "${ROOT}/Keysharp.sln" -c "${CONFIG}" -r "${RID}" \
  -p:Deterministic=true \
  -p:ContinuousIntegrationBuild=true \
  -p:PathMap="${PATH_MAP}"

echo "Staging package at ${PKG_DIR}..."
rm -rf "${PKG_DIR}"
mkdir -p "${APP_DIR}"

rsync -a "${PUBLISH_DIR}/Keyview/" "${APP_DIR}/"
rsync -a "${PUBLISH_DIR}/Keysharp/" "${APP_DIR}/"
build_native_helpers
normalize_app_permissions
verify_no_local_paths "${APP_DIR}"

# Copy installer assets
cp "${ASSETS_DIR}/install.sh" "${ASSETS_DIR}/uninstall.sh" "${PKG_DIR}/"
cp "${ASSETS_DIR}/keyview.desktop" "${ASSETS_DIR}/keysharp.desktop" "${ASSETS_DIR}/keysharp-screencap.desktop" "${ASSETS_DIR}/keysharp.xml" "${PKG_DIR}/"
cp "${ROOT}/Keysharp.png" "${PKG_DIR}/"
cp "${INPUTD_SERVICE_TEMPLATE}" "${PKG_DIR}/keysharp-inputd.service.in"
cp "${INPUTD_SOCKET}" "${PKG_DIR}/keysharp-inputd.socket"
cp -r "${ASSETS_DIR}/gnome-shell-extension" "${PKG_DIR}/gnome-shell-extension"
chmod 0755 "${PKG_DIR}/install.sh" "${PKG_DIR}/uninstall.sh"
chmod 0644 "${PKG_DIR}/keyview.desktop" \
  "${PKG_DIR}/keysharp.desktop" \
  "${PKG_DIR}/keysharp-screencap.desktop" \
  "${PKG_DIR}/keysharp.xml" \
  "${PKG_DIR}/Keysharp.png" \
  "${PKG_DIR}/keysharp-inputd.service.in" \
  "${PKG_DIR}/keysharp-inputd.socket"
find "${PKG_DIR}/gnome-shell-extension" -type d -exec chmod 0755 {} +
find "${PKG_DIR}/gnome-shell-extension" -type f -exec chmod 0644 {} +

build_tarball
build_deb

echo "Done."
