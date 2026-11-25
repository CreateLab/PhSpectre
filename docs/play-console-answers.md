# Play Console cheat sheet

Facts below were verified on the Play build 3.0.0 (code 18): no `INTERNET`, no storage/other permissions,
no ads/analytics SDKs, only network code (GitHub update check) is compiled out under `PLAY_STORE`.

## Store settings

- **App name:** PhSpectre. **Default language:** en-US. **Type:** App. **Price:** Free.
- **Category:** Photography. **Tags:** Photo editor.
- **Contact email:** a public address of your choice (required by Play). **Website:** https://github.com/CreateLab/PhSpectre
- **Privacy policy URL:** https://createlab.github.io/PhSpectre/privacy-policy (after GitHub Pages is enabled)
- Texts and graphics: `docs/store-listing/` (en-US, ru-RU, icon, feature graphic, screenshots folder).

## App content

| Question | Answer |
|---|---|
| App access | All functionality available without login or special access |
| Ads | No ads |
| Content rating (IARC) | Category "Utility / Photo editor"; no violence, sexual content, profanity, gambling, drugs; no user-generated content; no user interaction; no location sharing; no purchases. Expect "Everyone" |
| Target audience | 13+ (or 18+); not designed for children, no child appeal |
| News app | No |
| COVID-19 / Government / Health / Financial features | No |
| Data safety: collect or share data | **No** data collected, **no** data shared |
| Data safety: encryption in transit / deletion | Not applicable (no data leaves the device) |
| Advertising ID | Not used |
| Permissions declarations | None (no photo/video, all-files, location, etc.) |
| Foreground services / exact alarms | None |

## Declarations

- Developer Program Policies and US export laws: accept (app uses no non-standard encryption).
- Hardware: `android.hardware.usb.host` is `required="false"`, so devices without USB host can install.
- Fujifilm: not affiliated; listing says so. Do not show Fujifilm logos in graphics.

## Release plan

1. Upload `com.createlab.phspectre-Signed.aab` to **Internal testing**; install from Play, run the smoke tests
   (start, pick photo, Look, Frame, export to gallery, collage, USB camera if available, device without Google
   Play Services if possible).
2. **Closed testing:** ≥ 12 testers continuously opted in for 14 days (check the current rule in Console).
3. **Apply for production** and answer the questionnaire about the test.

See `docs/release-android.md` for the build command.
