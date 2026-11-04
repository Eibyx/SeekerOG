#!/usr/bin/env bash
# Verifies SeekerOG release APKs in a directory and writes SHA256SUMS.txt + VERIFY.txt.
#   Misc/seekerog-verify.sh <dir> <version> [versionCode]
# Checks for each SeekerOG-v<version>-<abi>.apk (abi = arm64-v8a, armeabi-v7a):
#   * apksigner verify (v1/v2/v3 schemes) and certificate == pinned SeekerOG certificate
#     (.github/seekerog-signing-cert.sha256, or SEEKEROG_EXPECTED_CERT_SHA256)
#   * package id, versionName, versionCode, minSdk 23 (Android 6.0)
#   * native libraries only for the APK's own ABI
#   * zip integrity, no keystore / private key files inside
#   * both APKs share the same signing certificate
set -euo pipefail
DIR="${1:?dir}"; VERSION="${2:?version}"; VERSION_CODE="${3:-}"
PKG="com.eibyx.seekerog"
: "${ANDROID_SDK_ROOT:=${ANDROID_HOME:-$HOME/android-sdk}}"
BT_DIR="$(ls -d "$ANDROID_SDK_ROOT"/build-tools/* 2>/dev/null | sort -V | tail -n1)"
APKSIGNER="$BT_DIR/apksigner"; AAPT2="$BT_DIR/aapt2"
PIN="${SEEKEROG_EXPECTED_CERT_SHA256:-}"
if [ -z "$PIN" ] && [ -f .github/seekerog-signing-cert.sha256 ]; then
  PIN="$(grep -Eo '[0-9a-fA-F:]{64,}' .github/seekerog-signing-cert.sha256 | head -n1)"
fi
PIN="$(echo "$PIN" | tr -d ': ' | tr 'A-F' 'a-f')"
if [ -z "$PIN" ]; then echo "::warning::no pinned certificate configured (.github/seekerog-signing-cert.sha256 or SEEKEROG_EXPECTED_CERT_SHA256); only checking both APKs share one certificate"; fi

: > "$DIR/VERIFY.txt"
CERTS=()
for ABI in arm64-v8a armeabi-v7a; do
  APK="$DIR/SeekerOG-v$VERSION-$ABI.apk"
  [ -f "$APK" ] || { echo "::error::missing $APK" >&2; exit 1; }
  {
    echo "### $(basename "$APK")"
    "$APKSIGNER" verify --verbose --print-certs "$APK" | grep -E "^Verifie[sd]|Signer #1 certificate (DN|SHA-256)|Number of signers"
    "$AAPT2" dump badging "$APK" | grep -E "^package:|^minSdkVersion:|^targetSdkVersion:|^native-code:|^application-label:"
  } | tee -a "$DIR/VERIFY.txt"
  "$APKSIGNER" verify "$APK" >/dev/null || { echo "::error::signature invalid: $APK" >&2; exit 1; }
  CERT="$("$APKSIGNER" verify --print-certs "$APK" | sed -n 's/^Signer #1 certificate SHA-256 digest: //p' | tr -d ':' | tr 'A-F' 'a-f')"
  if [ -n "$PIN" ] && [ "$CERT" != "$PIN" ]; then echo "::error::$APK signed by $CERT, expected pinned SeekerOG certificate $PIN" >&2; exit 1; fi
  CERTS+=("$CERT")
  BADGING="$("$AAPT2" dump badging "$APK")"
  echo "$BADGING" | grep -q "^package: name='$PKG' versionCode='${VERSION_CODE:-[0-9]*}' versionName='$VERSION'" \
    || echo "$BADGING" | grep -Eq "^package: name='$PKG' versionCode='[0-9]+' versionName='$VERSION'" \
    || { echo "::error::package/version mismatch in $APK" >&2; exit 1; }
  if [ -n "$VERSION_CODE" ]; then
    echo "$BADGING" | grep -q "versionCode='$VERSION_CODE'" || { echo "::error::versionCode mismatch in $APK" >&2; exit 1; }
  fi
  echo "$BADGING" | grep -q "^minSdkVersion:'23'" || { echo "::error::minSdk is not 23 in $APK" >&2; exit 1; }
  LIB_ABIS="$(unzip -Z1 "$APK" | grep '^lib/' | cut -d/ -f2 | sort -u | tr '\n' ' ')"
  [ "$LIB_ABIS" = "$ABI " ] || { echo "::error::$APK has native libs for: $LIB_ABIS (expected only $ABI)" >&2; exit 1; }
  if unzip -Z1 "$APK" | grep -Eiq '\.(jks|keystore|p12|pfx|pem|key)$'; then
    echo "::error::$APK contains key material" >&2; exit 1
  fi
  unzip -tq "$APK" >/dev/null || { echo "::error::zip integrity check failed for $APK" >&2; exit 1; }
  { echo "native libs: $LIB_ABIS"; [ -n "$PIN" ] && echo "certificate matches pinned SeekerOG fingerprint"; echo; } | tee -a "$DIR/VERIFY.txt"
done
[ "${CERTS[0]}" = "${CERTS[1]}" ] || { echo "::error::APKs are signed with different certificates" >&2; exit 1; }
( cd "$DIR" && sha256sum SeekerOG-v"$VERSION"-*.apk > SHA256SUMS.txt && cat SHA256SUMS.txt )
echo "All SeekerOG release checks passed."
