#!/usr/bin/env bash
# SeekerOG release build: per-ABI APKs (arm64-v8a, armeabi-v7a), signed with the persistent
# SeekerOG release key, then verified (signature, certificate, package, version, minSdk, ABIs,
# no private key material) and hashed.
#
# Usage:  Misc/seekerog-build.sh <version>      e.g. Misc/seekerog-build.sh 0.1.0-alpha
#
# Signing (all optional; without them the APKs are left UNSIGNED-for-release and marked as such):
#   SEEKEROG_KEYSTORE        path to the release keystore (PKCS12 / JKS)
#   SEEKEROG_KEY_ALIAS       key alias (default: seekerog)
#   SEEKEROG_STORE_PASS      keystore password   (read through env:, never placed on the command line)
#   SEEKEROG_KEY_PASS        key password        (defaults to SEEKEROG_STORE_PASS)
#   SEEKEROG_EXPECTED_CERT_SHA256  if set, the signing certificate must match this fingerprint
#
# Output: release-apks/SeekerOG-v<version>-<abi>.apk, release-apks/SHA256SUMS.txt, release-apks/VERIFY.txt
# Verification is done by Misc/seekerog-verify.sh (also used for prebuilt APKs).
set -euo pipefail

VERSION="${1:?version required, e.g. 0.1.0-alpha}"
CSPROJ="Seeker/Seeker.csproj"
CONFIG="Release IzzySoft"     # FOSS flavour: no Firebase / Crashlytics, no telemetry
PKG="com.eibyx.seekerog"
OUT="release-apks"
ABIS=("android-arm64:arm64-v8a" "android-arm:armeabi-v7a")

: "${ANDROID_SDK_ROOT:=${ANDROID_HOME:-$HOME/android-sdk}}"
BT_DIR="$(ls -d "$ANDROID_SDK_ROOT"/build-tools/* 2>/dev/null | sort -V | tail -n1)"
APKSIGNER="$BT_DIR/apksigner"
AAPT2="$BT_DIR/aapt2"
[ -x "$APKSIGNER" ] || { echo "apksigner not found under $ANDROID_SDK_ROOT/build-tools" >&2; exit 1; }

TFM="$(dotnet msbuild "$CSPROJ" -getProperty:TargetFramework -v:q | tr -d '[:space:]')"
DISPLAY_VERSION="$(dotnet msbuild "$CSPROJ" -getProperty:ApplicationDisplayVersion -v:q | tr -d '[:space:]')"
VERSION_CODE="$(dotnet msbuild "$CSPROJ" -getProperty:ApplicationVersion -v:q | tr -d '[:space:]')"
if [ "$DISPLAY_VERSION" != "$VERSION" ]; then
  echo "::error::Requested version $VERSION but $CSPROJ has ApplicationDisplayVersion=$DISPLAY_VERSION" >&2
  exit 1
fi
echo "TFM=$TFM version=$VERSION versionCode=$VERSION_CODE"

SIGN_ARGS=()
SIGNED=0
if [ -n "${SEEKEROG_KEYSTORE:-}" ] && [ -f "${SEEKEROG_KEYSTORE}" ] && [ -n "${SEEKEROG_STORE_PASS:-}" ]; then
  export SEEKEROG_KEY_PASS="${SEEKEROG_KEY_PASS:-$SEEKEROG_STORE_PASS}"
  SIGN_ARGS=(
    "-p:AndroidKeyStore=true"
    "-p:AndroidSigningKeyStore=${SEEKEROG_KEYSTORE}"
    "-p:AndroidSigningKeyAlias=${SEEKEROG_KEY_ALIAS:-seekerog}"
    "-p:AndroidSigningStorePass=env:SEEKEROG_STORE_PASS"
    "-p:AndroidSigningKeyPass=env:SEEKEROG_KEY_PASS"
  )
  SIGNED=1
  echo "Signing with the SeekerOG release key (alias ${SEEKEROG_KEY_ALIAS:-seekerog})"
else
  # Debug-key signature only so the build completes; these files are NOT release artifacts.
  SIGN_ARGS=("-p:AndroidKeyStore=false")
  echo "::warning::No release keystore provided - APKs will be debug-signed and must not be published."
fi

GIT_SHA="$(git rev-parse HEAD 2>/dev/null || echo unknown)"
export SOURCE_DATE_EPOCH="$(git log -1 --pretty=%ct 2>/dev/null || date +%s)"

rm -rf "$OUT"
mkdir -p "$OUT"

for pair in "${ABIS[@]}"; do
  RID="${pair%%:*}"
  ABI="${pair##*:}"
  echo "==> Publishing $RID ($ABI)"
  rm -rf "Seeker/bin/$CONFIG/$TFM/$RID" "Seeker/obj/$CONFIG/$TFM/$RID"
  # -p:RuntimeIdentifiers= clears the multi-RID list from the csproj so the APK only contains $ABI
  dotnet publish "$CSPROJ" -c "$CONFIG" -f "$TFM" -r "$RID" -p:RuntimeIdentifiers= \
    -p:Optimize=true -p:ContinuousIntegrationBuild=true -p:Deterministic=true \
    -p:SourceRevisionId="$GIT_SHA" "${SIGN_ARGS[@]}"
  SRC="Seeker/bin/$CONFIG/$TFM/$RID/publish/$PKG-Signed.apk"
  [ -f "$SRC" ] || { echo "::error::APK not found: $SRC" >&2; exit 1; }
  DEST="$OUT/SeekerOG-v$VERSION-$ABI.apk"
  if [ "$SIGNED" != "1" ]; then
    DEST="$OUT/SeekerOG-v$VERSION-$ABI-DEBUG-SIGNED-DO-NOT-PUBLISH.apk"
  fi
  cp "$SRC" "$DEST"

done

if [ "$SIGNED" = "1" ]; then
  Misc/seekerog-verify.sh "$OUT" "$VERSION" "$VERSION_CODE"
else
  ( cd "$OUT" && sha256sum *.apk > SHA256SUMS.txt && cat SHA256SUMS.txt )
fi
echo "Done. signed=$SIGNED"
