# PhSpectre

Extracts a dominant color palette from a JPEG photo and renders it as a PNG — original image alongside color swatches and optional EXIF metadata.

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
| `file` | JPEG file | yes | Source photo (`.jpg` / `.jpeg`) |
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
3. **Rendering** — composites the original photo (full resolution, auto-oriented) with the palette panel using SixLabors.ImageSharp

## Requirements

- .NET 8
- Docker (optional, for containerised API)
- Supports JPEG input only (`.jpg` / `.jpeg`)
