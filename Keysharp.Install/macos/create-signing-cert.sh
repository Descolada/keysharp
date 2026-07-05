#!/usr/bin/env bash
#
# Create a stable, self-signed code-signing identity for the macOS Keysharp app.
#
# Why: macOS TCC keys permission grants (Accessibility, Input Monitoring, …) to a code signature's
# designated requirement. Ad-hoc signing has no stable identity — its cdhash changes on every build —
# so granted permissions are lost after every rebuild/update. Signing every build with the SAME
# self-signed identity gives a stable requirement, so you grant permissions once and they persist
# across rebuilds and .pkg/.dmg updates. (This is free; it is not notarized, so a quarantined download
# still shows Gatekeeper's "unidentified developer" prompt once — right-click → Open.)
#
# Usage:
#   ./Keysharp.Install/macos/create-signing-cert.sh        # creates identity "Keysharp" (idempotent)
#   IDENTITY="My Name" ./Keysharp.Install/macos/create-signing-cert.sh
#
# Then build a signed package/app with:
#   APP_CERT="Keysharp" ./Keysharp.Install/package-macos.sh
#
set -euo pipefail

IDENTITY="${IDENTITY:-Keysharp}"
KEYCHAIN="${KEYCHAIN:-$HOME/Library/Keychains/login.keychain-db}"
# macOS's built-in LibreSSL produces a PKCS#12 that `security import` can read. Homebrew's OpenSSL 3.x
# defaults to algorithms macOS can't verify (the "MAC verification failed" error), so pin the system one.
OPENSSL="${OPENSSL:-/usr/bin/openssl}"
BACKUP_DIR="${BACKUP_DIR:-$HOME/.keysharp}"
BACKUP_P12="$BACKUP_DIR/${IDENTITY// /_}-codesign.p12"

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m warning:\033[0m %s\n' "$*" >&2; }

[[ "$(uname)" == "Darwin" ]] || { echo "This script is macOS-only." >&2; exit 1; }
[[ -x "$OPENSSL" ]] || { echo "openssl not found at $OPENSSL (set OPENSSL=…)." >&2; exit 1; }

# Idempotent: reuse the identity if it already exists (recreating it would change the requirement and
# invalidate the permissions you've already granted).
if security find-identity -v -p codesigning 2>/dev/null | grep -qF "\"$IDENTITY\""; then
  log "Code-signing identity \"$IDENTITY\" already exists — leaving it in place."
  log "Package with:  APP_CERT=\"$IDENTITY\" ./Keysharp.Install/package-macos.sh"
  exit 0
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cnf="$work/codesign.cnf"; key="$work/key.pem"; crt="$work/cert.pem"; p12="$work/identity.p12"
# A real (random) transport password sidesteps the empty-password PKCS#12 MAC ambiguity between tools.
p12pass="$("$OPENSSL" rand -base64 18)"

cat > "$cnf" <<EOF
[ req ]
distinguished_name = dn
x509_extensions    = v3
prompt             = no
[ dn ]
CN = $IDENTITY
[ v3 ]
basicConstraints     = critical,CA:FALSE
keyUsage             = critical,digitalSignature
extendedKeyUsage     = critical,codeSigning
subjectKeyIdentifier = hash
EOF

log "Generating self-signed code-signing certificate \"$IDENTITY\" (RSA 2048, valid 10 years)…"
"$OPENSSL" req -x509 -newkey rsa:2048 -sha256 -nodes -days 3650 -keyout "$key" -out "$crt" -config "$cnf" >/dev/null 2>&1
"$OPENSSL" pkcs12 -export -inkey "$key" -in "$crt" -name "$IDENTITY" -out "$p12" -passout "pass:$p12pass" >/dev/null 2>&1

log "Importing the identity into $KEYCHAIN …"
security import "$p12" -k "$KEYCHAIN" -P "$p12pass" -T /usr/bin/codesign >/dev/null

log "Trusting \"$IDENTITY\" for code signing (may prompt for your password)…"
security add-trusted-cert -r trustRoot -p codeSign -k "$KEYCHAIN" "$crt" >/dev/null 2>&1 \
  || warn "trust step skipped — signing still works, but codesign --verify / Gatekeeper may not trust it."

# Let codesign use the private key without a prompt on every build. Needs the keychain password; if we
# can't set it non-interactively, a one-time "Always Allow" click on the first sign does the same thing.
log "Authorizing codesign to use the key…"
if ! security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "" "$KEYCHAIN" >/dev/null 2>&1; then
  printf '   login keychain password (blank to skip): '; read -rs kcpass || kcpass=""; echo
  if [[ -n "$kcpass" ]]; then
    security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$kcpass" "$KEYCHAIN" >/dev/null 2>&1 \
      || warn "could not pre-authorize — you'll get a one-time keychain prompt; click \"Always Allow\"."
  else
    warn "skipped — you'll get a one-time keychain prompt on first sign; click \"Always Allow\"."
  fi
fi

# Back up the identity so you can re-import the SAME cert after a keychain reset or on another machine.
mkdir -p "$BACKUP_DIR"; chmod 700 "$BACKUP_DIR"
cp "$p12" "$BACKUP_P12"; chmod 600 "$BACKUP_P12"
printf '%s' "$p12pass" > "$BACKUP_P12.pass"; chmod 600 "$BACKUP_P12.pass"

log "Done. Code-signing identities now available:"
security find-identity -v -p codesigning | grep -F "$IDENTITY" || true

cat <<EOF

  Backup (KEEP THIS — the same cert is what makes permissions persist across updates):
    $BACKUP_P12
    re-import later with:
      security import "$BACKUP_P12" -k "$KEYCHAIN" -P "\$(cat '$BACKUP_P12.pass')" -T /usr/bin/codesign

  Build a signed package:
    APP_CERT="$IDENTITY" ./Keysharp.Install/package-macos.sh
EOF
