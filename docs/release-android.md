# Android release (Google Play)

Package: `com.createlab.phspectre` (fixed in Play Console, do not change). Target API 36, min API 29, ABIs
`arm64-v8a` + `x86_64`.

## Build the signed AAB

```powershell
.\scripts\build-play-aab.ps1 -VersionCode 20
```

The script runs `dotnet publish -c Release -p:PlayStoreBuild=true -p:AndroidPackageFormat=aab -p:ApplicationVersion=<code>`
with the upload keystore and prints the path of `com.createlab.phspectre-Signed.aab`
(under `PhSpectre.Android\bin\Release\net10.0-android36.0\`).

`PlayStoreBuild=true` defines `PLAY_STORE`: no `INTERNET` permission and the GitHub update check is off. The GitHub APK
build (CI, `AndroidPackageFormat=apk`) is unchanged.

## Signing

Keystore settings are read from env vars `PHSPECTRE_KEYSTORE`, `PHSPECTRE_KEYSTORE_PASS`, `PHSPECTRE_KEY_ALIAS`,
`PHSPECTRE_KEY_PASS`, or from `scripts/play-signing.local.ps1` (git-ignored; template:
`scripts/play-signing.local.example.ps1`). Create the upload key once with `keytool -genkeypair -v -keystore
phspectre-upload.jks -alias upload -keyalg RSA -keysize 2048 -validity 10000`, keep it outside the repo with backups.
Never commit `*.jks`, `*.keystore` or passwords.

## Versions

- `ApplicationVersion` (versionCode) is passed with `-VersionCode`; every Play upload must be strictly higher than
  the previous one. Play tracks its own codes, so reusing numbers from GitHub releases is fine.
- `ApplicationDisplayVersion` comes from the csproj (currently 3.0.2).

## Checks before uploading

Verified for 3.0.0 / code 18 (Play build):

- Merged manifest: no `INTERNET`, no storage permissions; `android.hardware.usb.host` has `required="false"`;
  `targetSdkVersion=36`, `minSdkVersion=29`.
- All 216 native `.so` in the AAB have LOAD segment alignment `0x4000` (16 KB page size ready).
- Only `UpdateChecker` uses the network and it is disabled under `PLAY_STORE`.

Re-check after dependency updates: unzip the AAB, parse ELF program headers of `base/lib/*/*.so` (Align must be
`>= 0x4000`) and read `obj/Release/net10.0-android36.0/android/AndroidManifest.xml`.

## Rollout

1. Upload the AAB to **Internal testing**, install from Play on a real phone, run: start, pick photo, Look/Frame,
   export to gallery, collage, (optional) USB camera.
2. **Closed testing**: at least 12 testers opted in for 14 continuous days (check the current rule in Play Console).
3. Apply for production and answer the test questionnaire.

Store texts and graphics: `docs/store-listing/`. Console answers: `docs/play-console-answers.md`.
