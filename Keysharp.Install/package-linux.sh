#!/usr/bin/env bash
if [ -z "${BASH_VERSION:-}" ]; then exec /usr/bin/env bash "$0" "$@"; fi
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ASSETS_DIR="${ROOT}/Keysharp.Install/linux"
CONFIG="${CONFIG:-Release}"
RID="${RID:-linux-x64}"
TFM="${TFM:-net10.0}"
DIST_DIR="${ROOT}/dist"
PKG_NAME="keysharp-${RID}"
PKG_DIR="${DIST_DIR}/${PKG_NAME}"
APP_DIR="${PKG_DIR}/app"
VERSION="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "${ROOT}/Keysharp/Keysharp.csproj" | head -n 1)}"
DEB_PKG_NAME="${DEB_PKG_NAME:-keysharp}"
DEB_TMP_DIR="${DIST_DIR}/${PKG_NAME}-deb"
DEB_OUT=""
DEB_ARCH=""
DEB_DEPENDS="dotnet-runtime-10.0, libx11-6, libxtst6, libxinerama1, libxt6, libx11-xcb1, libxkbcommon-x11-0, libxcb-xtest0, libgtk-3-0, libnotify4, libatspi2.0-0, at-spi2-core"
DEB_DESCRIPTION="A C# port and enhancement of the AutoHotkey program"

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
  sed 's|/usr/local/bin/|/usr/bin/|g' "${src}" > "${dest}"
}

write_deb_control() {
  cat > "$1" <<EOF
Package: ${DEB_PKG_NAME}
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: ${DEB_ARCH}
Maintainer: Descolada
Depends: ${DEB_DEPENDS}
Description: ${DEB_DESCRIPTION}
EOF
}

write_deb_postinst() {
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
EOF
  chmod 0755 "$1"
}

write_deb_prerm() {
  cat > "$1" <<'EOF'
#!/bin/sh
set -e
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
EOF
  chmod 0755 "$1"
}

build_tarball() {
  local tarball="${DIST_DIR}/${PKG_NAME}.tar.gz"
  echo "Creating tarball ${tarball}..."
  tar -czf "${tarball}" -C "${DIST_DIR}" "${PKG_NAME}"
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
  mkdir -p "${debian_dir}" "${lib_dir}" "${bin_dir}" "${applications_dir}" "${mime_dir}" "${icon_dir}"

  rsync -a "${APP_DIR}/" "${lib_dir}/"
  ln -s "../lib/keysharp/Keysharp" "${bin_dir}/keysharp"
  ln -s "../lib/keysharp/Keyview" "${bin_dir}/keyview"
  rewrite_desktop_exec "${ASSETS_DIR}/keysharp.desktop" "${applications_dir}/keysharp.desktop"
  rewrite_desktop_exec "${ASSETS_DIR}/keyview.desktop" "${applications_dir}/keyview.desktop"
  install -Dm644 "${ASSETS_DIR}/keysharp.xml" "${mime_dir}/keysharp.xml"
  install -Dm644 "${ROOT}/Keysharp.png" "${icon_dir}/keysharp.png"

  write_deb_control "${debian_dir}/control"
  write_deb_postinst "${debian_dir}/postinst"
  write_deb_prerm "${debian_dir}/prerm"
  write_deb_postrm "${debian_dir}/postrm"

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
dotnet publish "${ROOT}/Keysharp/Keysharp.csproj" -c "${CONFIG}" -r "${RID}"
dotnet publish "${ROOT}/Keyview/Keyview.csproj" -c "${CONFIG}" -r "${RID}"

echo "Staging package at ${PKG_DIR}..."
rm -rf "${PKG_DIR}"
mkdir -p "${APP_DIR}"

rsync -a "${ROOT}/bin/${CONFIG}/${TFM}/${RID}/publish/" "${APP_DIR}/"

# Copy installer assets
cp "${ASSETS_DIR}/install.sh" "${ASSETS_DIR}/uninstall.sh" "${PKG_DIR}/"
cp "${ASSETS_DIR}/keyview.desktop" "${ASSETS_DIR}/keysharp.desktop" "${ASSETS_DIR}/keysharp.xml" "${PKG_DIR}/"
cp "${ROOT}/Keysharp.png" "${PKG_DIR}/"

mkdir -p "${DIST_DIR}"
build_tarball
build_deb

echo "Done."
