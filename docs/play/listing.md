# Google Play listing (English — the app UI is English only)

## App name (max 30)
PhSpectre

## Short description (max 80)
Film looks, frames and palette cards for photos. Camera recipes over USB.

## Full description (max 4000)
PhSpectre grades a photo with a look, wraps it in a frame and exports it. Everything happens on your device — no account, no ads, no network.

LOOK
• 46 built-in looks in categories: Historic, Alt process, Film, Cinema, B&W, Toy camera, Creative
• Every tile is previewed on your own photo; one Amount slider blends the look in
• Import your own Lightroom presets (.xmp), .cube LUTs or a .zip of them; favourites are one tap away

FRAME
• Plain border, a Card with colour palette swatches and camera info, a Recipe card, or a Film edge with sprocket holes (135, half-frame, 120)
• Frames are built on the graded photo, so your look carries through to the export

COLLAGE
• Combine several photos into one card with a single palette pooled across all of them

CAMERA RECIPES (optional)
• Reads the in-camera film simulation recipe from a Fujifilm JPEG and renders it as a card
• Browse a library of community recipes (credited to their authors) and write them into your camera over a USB (OTG) cable
• Needs a supported camera; not required for everything else

EXPORT
• JPEG or PNG, original size, half size or social presets (square, post, story, landscape)
• Saved straight to Pictures/PhSpectre

PRIVATE BY DESIGN
• Your photos never leave your device
• No permissions required — photos are picked with the system picker

PhSpectre is an independent app and is not affiliated with, endorsed by or sponsored by Fujifilm. Fujifilm and film simulation names are trademarks of their respective owners and are used only to describe compatibility. Writing recipes to a camera with third-party software may void the manufacturer's warranty — use at your own risk.

## Release notes (max 500) — first release
First release on Google Play: film looks, frames (border, card, recipe, film edge), collage, palette and QR cards, and optional camera recipe writing over USB.

## Categories / contact
- App category: Photography
- Tags: Photo editor
- Contact email: (Play Console requires a public email — use one you are happy to show)
- Privacy policy URL: https://github.com/CreateLab/PhSpectre/blob/master/docs/privacy-policy.md  (works once this file is on `master`)

## Play Console answers
- **App access:** all functionality available without login.
- **Ads:** No.
- **Data safety:** no data collected, no data shared; no data-deletion mechanism needed. (Play build has no INTERNET permission.)
- **Permissions declarations:** none required (no photo/video permission, no all-files access).
- **Content rating (IARC):** utility/photo editor, no user-generated content, no violence — expect "Everyone".
- **Target audience:** 13+/18+ (not designed for children — avoids Families policy).
- **News app / Government / Health / Financial:** No.
- **Hardware:** `android.hardware.usb.host` is declared `required=false`, so devices without USB host can still install.

## Store graphics checklist
- [x] App icon 512×512 PNG: `docs/design/icon/store-icon-512.png`
- [ ] Feature graphic 1024×500 (JPEG/PNG, no transparency)
- [ ] Phone screenshots (2–8, 16:9 or 9:16, min 320 px, max 3840 px) — capture on a phone, not the desktop screens in `screens/`
- [ ] 7-inch / 10-inch tablet screenshots (recommended, used for tablet ranking)
- [ ] Do not show Fujifilm logos or camera brand artwork in graphics.

## Before the first production release
- New personal developer accounts must run a closed test with at least 12 testers for 14 days before production access (check the current requirement in Play Console).
