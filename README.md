# PhSpectre

Point it at a photo and get back its color story: a dominant palette, EXIF, or — for
Fujifilm shooters — the exact in-camera film recipe that made it look that way, all
rendered as a shareable card. Add a QR code and the card can stay clean on the surface
while carrying the full data underneath, one scan away.

**[Download the latest release](https://github.com/CreateLab/PhSpectre/releases/latest)** — Windows x64, Linux x64, macOS arm64, Android APK.

[![Build — Windows](https://github.com/CreateLab/PhSpectre/actions/workflows/build-windows.yml/badge.svg)](https://github.com/CreateLab/PhSpectre/actions/workflows/build-windows.yml)
[![Build — Linux](https://github.com/CreateLab/PhSpectre/actions/workflows/build-linux.yml/badge.svg)](https://github.com/CreateLab/PhSpectre/actions/workflows/build-linux.yml)
[![Build — macOS](https://github.com/CreateLab/PhSpectre/actions/workflows/build-macos.yml/badge.svg)](https://github.com/CreateLab/PhSpectre/actions/workflows/build-macos.yml)
[![Test](https://github.com/CreateLab/PhSpectre/actions/workflows/test.yml/badge.svg)](https://github.com/CreateLab/PhSpectre/actions/workflows/test.yml)

## What it does

- **Palette** — dominant colors from any JPEG or HEIF/HEIC photo, k-means++ in HSL space, auto or fixed color count.
- **Recipe** — auto-detects a Fujifilm JPEG/HEIF's in-camera film simulation recipe straight from the MakerNote (white balance + shift, dynamic range, tone curve, sharpness, grain, color chrome, all of it) and renders it as its own card.
- **Collage** — combine several photos into one card with a single palette pooled across all of them, weighted by area.
- **QR code** — drop a compact QR on any export linking to a page with the shot's camera, lens, exposure and recipe. The card's own text (camera name, lens, or nothing at all) and what the QR encodes are independent — keep the card minimal and let the scan carry the detail.
- One rendering engine, four ways in: Desktop (Windows/Linux/macOS), Android, CLI, and a REST API.

## Recipe card, minimal — with a QR carrying the rest

Recipe mode with the text plates turned off and just a QR code below the photo: camera
and lens as a two-line caption, the full recipe one scan away.

<p>
  <img src="screens/DSCF6601_recipe.jpg" width="49%" alt="Recipe export, text plates off, QR code with camera and lens caption" />
  <img src="screens/DSCF6964_recipe.jpg" width="49%" alt="Recipe export, text plates off, QR code with camera and lens caption, second example" />
</p>

## Screenshots

Desktop app — folder browser, palette settings, and live preview:

![Desktop app](screens/desktop-app.jpg)

Collage mode — check several photos into a tray, generate one palette pooled across all of
them (weighted by displayed area), with EXIF taken from the first photo:

<p>
  <img src="screens/collage-desktop.jpg" width="59%" alt="Desktop collage mode with a photo tray and gutter settings" />
  <img src="screens/collage-mobile.jpg" width="39%" alt="Mobile collage output — five photos, pooled palette, EXIF strip" />
</p>

Film Recipe mode with the full text card — parameter grid alongside the auto-detected recipe:

<p>
  <img src="screens/recepiet_ui.jpg" width="59%" alt="Desktop Recipe export mode — auto-detected Fujifilm recipe panel" />
  <img src="screens/photo_2026-09-23_23-17-17.jpg" width="39%" alt="Exported recipe card for a Fujifilm Provia/Standard shot" />
</p>

Generated palettes — portrait and landscape layouts, with and without hex labels:

<p>
  <img src="screens/palette-portrait.jpg" width="49%" alt="Portrait palette with hex labels" />
  <img src="screens/palette-vivid.jpg" width="49%" alt="Portrait palette, vivid colors" />
</p>
<p>
  <img src="screens/palette-landscape.jpg" width="49%" alt="Landscape palette with hex labels" />
  <img src="screens/palette-no-hex.jpg" width="49%" alt="Landscape palette without hex labels" />
</p>
<p>
  <img src="screens/palette-seagulls.jpg" width="49%" alt="Landscape palette, three-color auto count" />
</p>

## Releases

Releases are tagged `vX.Y.Z` (semantic versioning) and each one bundles all four platforms —
`PhSpectre-X.Y.Z-windows-x64.zip`, `PhSpectre-X.Y.Z-linux-x64.tar.gz`,
`PhSpectre-X.Y.Z-macos-arm64.zip` (a proper `.app` bundle), `PhSpectre-X.Y.Z-android.apk` — with an auto-generated
changelog. Grab the latest from the
[Releases page](https://github.com/CreateLab/PhSpectre/releases/latest).

The apps check for a newer release on startup (once a day at most) and show a small,
dismissible notice when one's available — no auto-download/auto-install, it just links to
the release page.

Every commit to `master` still gets a plain build-and-test check on all four platforms
(no version, no release) via the Build/Test workflows above — those aren't installable
releases, just CI signal.

**Cutting a release** (maintainers): `git tag vX.Y.Z && git push origin vX.Y.Z` — CI builds,
versions, and publishes the release automatically.

## Desktop App

Cross-platform GUI built with Avalonia. Open a folder, browse photos, preview the generated
palette side-by-side and save it as PNG or JPEG.

**Collage mode** (desktop and Android) checks several photos into a tray and renders one
card from all of them: photos are laid out into a single image with a configurable gutter,
and the palette is pooled across the source photos — weighted by how much area each one
occupies — so the gutter itself never affects the result. EXIF metadata is read from the
first photo in the tray.

**Export Mode** (desktop and Android) picks what a photo (or collage) is rendered as: `Card`
(photo + palette swatches), `InfoOnly` (photo + EXIF plate, no swatches), `Recipe` (single
photo only — see below), or the collage equivalents `Collage`/`CollageInfoOnly`. Modes that
don't need a color palette skip the k-means clustering step entirely, so `InfoOnly` and
`CollageInfoOnly` render instantly even on large collages.

**Film Recipe mode** auto-detects the in-camera film simulation recipe from a Fujifilm
JPEG or HEIF/HEIC photo's MakerNote — film simulation, white balance (+ R/B shift), dynamic
range, highlight/shadow tone, color, sharpness, noise reduction, clarity, grain effect, and
color chrome/color chrome blue — and renders it as a standalone shareable card, badged
`Auto · Fuji`. Manual entry (and a `Custom` badge) is available for non-Fuji photos or to
override a detected recipe. Recipe detection currently supports Fujifilm cameras only. The
recipe card itself can be hidden (`Show recipe card`) independently of the QR code below —
useful once the QR is doing the job of carrying the data instead.

**QR code** (desktop and Android) adds a QR block right under the photo, before any text
plates, on any export mode. Three settings, each independent of the others:

- **Caption** — what's printed beside the QR itself: nothing, camera name only, or camera
  + lens as two lines. Doesn't change what's encoded inside the code.
- **Content source** — a generated link to a data-viewer page carrying the shot's info at
  one of five presets (from `Recipe only` up to `Camera + Lens + Photo info + Recipe`), or
  a custom URL of your own (a Telegram post, a portfolio page, anything).
- **Include note** — layers a short free-text note onto whichever preset is selected.

The payload is bit-packed (not JSON) so even the fullest preset stays a version 6–7 QR at
error-correction level M — legible at normal export sizes without dominating the card.

Self-contained — no .NET runtime required on the target machine.

### Keyboard shortcuts

| Key | Action |
|---|---|
| `↑` / `↓` | Navigate file list |
| `Ctrl+S` | Save palette PNG |

## CLI

```bash
dotnet run --project PhSpectre.CLI -- <image.jpg> [options]
```

### Options

| Flag | Default | Description |
|---|---|---|
| `--colors <n>` | auto | Number of palette colors (1–32). Auto uses elbow method (k=3–8). |
| `--no-hex` | — | Hide hex labels on swatches |
| `--theme dark\|light` | `dark` | Dark (`#111111`) or light (`#F5F5F5`) panel background |
| `--no-meta` | — | Suppress EXIF metadata strip |
| `--meta-short` | — | One line: camera · focal · aperture · shutter · ISO |
| `--meta-detail` | — | Two lines: camera+lens / focal+params+date |
| `--meta-full` | — | Three lines: detail + S/N, WB, exposure program, EV |
| `--meta-overlay` | — | Overlay style (semi-transparent) instead of film-strip bar |

Default metadata verbosity (no flag): camera + lens name.

### Examples

```bash
# Auto palette, dark theme, default metadata
dotnet run --project PhSpectre.CLI -- photo.jpg

# 6 colors, light theme, full metadata
dotnet run --project PhSpectre.CLI -- photo.jpg --colors 6 --theme light --meta-full

# Swatches only, no metadata, no hex labels
dotnet run --project PhSpectre.CLI -- photo.jpg --no-meta --no-hex
```

Output is saved as `<original-name>_palette.png` next to the source file. Hex codes and percentages are printed to stdout.

## API

### Run locally

```bash
dotnet run --project PhSpectre.API
# Swagger UI: http://localhost:5000/swagger
```

### Run with Docker

```bash
docker compose up --build
# Swagger UI: http://localhost:8080/swagger
```

### POST /api/palette

```
POST /api/palette
Content-Type: multipart/form-data
```

| Field | Type | Required | Description |
|---|---|---|---|
| `file` | JPEG or HEIF/HEIC file | yes | Source photo (`.jpg` / `.jpeg` / `.heif` / `.heic` / `.hif`) |
| `colors` | int | no | Palette size 1–32. Omit for auto. |
| `theme` | `dark` \| `light` | no | Panel background. Default: `dark`. |

Returns the palette PNG as `application/octet-stream`.

```bash
curl -X POST http://localhost:8080/api/palette \
  -F "file=@photo.jpg" \
  -F "colors=6" \
  -F "theme=light" \
  --output palette.png
```

**Rate limit:** 10 requests per minute per IP. Exceeding returns `429 Too Many Requests`.

## Layout

- **Portrait** (height > width): swatches panel on the right, metadata strip below the photo
- **Landscape** (width ≥ height): metadata strip between photo and swatch panel at the bottom

## How it works

1. **Sampling** — resizes the image to 150×150 for fast processing
2. **Clustering** — k-means++ in HSL space with circular hue distance; automatic k via the elbow method if `--colors` is not set
3. **Rendering** — composites the original photo (full resolution, auto-oriented) with the palette panel, recipe card, and/or QR block using SixLabors.ImageSharp

## Requirements

- .NET 8
- Docker (optional, for containerised API)
- Supports JPEG (`.jpg` / `.jpeg`) and HEIF/HEIC (`.heif` / `.heic` / `.hif`) input on Desktop,
  CLI, and API. Android currently accepts JPEG only — on-device HEIF decoding wasn't reliable
  enough to enable yet.
