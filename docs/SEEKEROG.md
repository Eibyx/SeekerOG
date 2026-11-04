# SeekerOG

Fork of [Seeker](https://github.com/jackBonadies/SeekerAndroid) by Jack Bonadies (GPL-3.0).
Package `com.eibyx.seekerog`, installs next to Seeker. Minimum Android 6.0 (API 23), target 36.
Distributed as two per-ABI APKs: `arm64-v8a` and `armeabi-v7a`.

## What changed compared to Seeker

| Area | Seeker | SeekerOG |
|---|---|---|
| Network | Official Soulseek server via Soulseek.NET | **Unchanged** (same server, accounts, protocol; no proxies, no extra services) |
| Search query | Words, `-exclusions`, a few flags (`mbr:`, `mfs:`, `isvbr`…) | Full advanced syntax (below). Only words/`-exclusions` reach the server; the rest filters locally while results arrive |
| Result filtering | Text filter, smart chips, format / min bitrate controls | + graphical builder, profiles, favourites, hide duplicates, CSV export, regex with timeout |
| UI | Material themes, 4-tab bar | Glass light/dark/AMOLED themes, floating 5-item bar, Home dashboard, appearance settings (accent, intensity, blur, text size, reduce effects) |
| Music | — | Library (songs/albums/artists/folders/playlists/favourites/downloads) and player with media session, notification, Bluetooth, equalizer, quality/output info |
| Credentials | Password in SharedPreferences (plaintext) | AES-GCM encrypted with an Android Keystore key (auto-migration) |
| Telemetry | Firebase Crashlytics in Play flavour | Built from the FOSS flavour only (no Firebase) |
| Transfers, rooms, messages, user list, browse, wishlist, sharing, UPnP | ✓ | ✓ unchanged |

## Search syntax

Typed in the **search box**: plain words and `-exclusions` go to the Soulseek server (the server only understands these), everything else is applied locally to the received results. Typed in the **filter box**: everything filters the current results.

| Filter | Examples |
|---|---|
| Format | `format:flac`, `formato:flac,mp3`, `ext:m4a`, `format:dsd`, `lossless`, `lossy`, `hires` |
| Bitrate (kbps) | `bitrate:320` (minimum), `bitrate>=256`, `bitrate:192-256`, `bitrate=320` |
| Sample rate | `samplerate:96k`, `frecuencia:96000`, `sr>=88.2` |
| Bit depth | `bits:24`, `profundidad>=24` |
| Size | `size<100MB`, `tamaño>=1.5GB`, `size:10-50` (MB) |
| Duration | `dur:3:30` (±5 s), `dur:3:30~10`, `duracion>=2m`, `dur:3:00-4:00` |
| Availability | `free:yes`, `queue<=10`, `speed>=500kb`, `files>=8`, `fav:yes`, `downloaded:no` |
| Text | `"exact phrase"`, `-word`, `-"phrase"`, `word*`, `user:name`, `folder:name`, `file:name`, `artist:"Daft Punk"`, `album:Discovery` |
| Regex | `re:/\(19[89]\d\)/` (case-insensitive; `/c` suffix = case-sensitive). Max 256 chars, 25 ms timeout per match |

Notes: peers may not send bitrate/sample rate/depth/duration; those files count as *unknown* and do not match quality filters. `artist/album/title` match folder and file names because search results contain no tags. The engine is `Common/Search/AdvancedQuery.cs` with unit tests in `UnitTestCommon/AdvancedQueryTests.cs`.

## Building

```bash
dotnet workload restore Seeker/Seeker.csproj
dotnet test UnitTestCommon/UnitTestCommon.csproj -c Release --filter "TestCategory!=RealData"
# per-ABI release APKs + verification + SHA-256 (see the script header for signing variables)
Misc/seekerog-build.sh 0.1.0-alpha
```

Versions live in `Seeker/Seeker.csproj` (`ApplicationDisplayVersion`, `ApplicationVersion`) and `Seeker/Properties/AndroidManifest.xml`. Increase `ApplicationVersion` (versionCode) by 1 for every release.

## Release signing (one-time setup, done by the repository owner)

The release key must be created and kept by the owner; CI only reads it from encrypted repository secrets.

1. Create the key on your own computer (keep the file and password in a password manager and an offline backup — losing them means existing installs can no longer be updated):
   ```bash
   keytool -genkeypair -keystore seekerog-release.p12 -storetype PKCS12 -alias seekerog \
     -keyalg RSA -keysize 4096 -validity 18250 -dname "CN=SeekerOG Release, O=Eibyx"
   ```
2. In GitHub: **Settings → Secrets and variables → Actions → New repository secret**:
   - `SEEKEROG_KEYSTORE_BASE64` = output of `base64 -w0 seekerog-release.p12`
   - `SEEKEROG_STORE_PASSWORD` = the keystore password
   - `SEEKEROG_KEY_ALIAS` = `seekerog`
3. Optional but recommended: pin the certificate. Run
   `keytool -list -v -keystore seekerog-release.p12 -storetype PKCS12 | grep SHA256:` and put the fingerprint in `.github/seekerog-signing-cert.sha256`. Builds signed with another key will then fail.

## Publishing a release

1. Bump the version in `Seeker/Seeker.csproj` and the manifest, add `.github/release-notes/v<version>.md`.
2. Push a tag `v<version>` (or run **Actions → SeekerOG build & release → Run workflow** with the tag).
3. The workflow runs the tests, builds `arm64-v8a` and `armeabi-v7a`, signs with the secrets, verifies (signature, same certificate, package, version, minSdk 23, single ABI, no key material, zip integrity), computes SHA-256 and publishes a GitHub pre-release with the APKs, `SHA256SUMS.txt`, `VERIFY.txt` and the notes. Without the secrets it refuses to publish.

The upstream maintainer workflows (`release.yml`, `fdroid.yml`, Crowdin) only run in `jackBonadies/SeekerAndroid`.
