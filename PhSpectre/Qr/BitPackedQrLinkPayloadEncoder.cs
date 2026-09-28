using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PhSpectre.Models;

namespace PhSpectre.Qr;

// Real spec-compliant bit-packer for the phspectre-view URL format — mirrors, byte-for-byte,
// the format comment at the top of that repo's index.html <script> (v3 spec, "root cause"
// section): flags byte, length-prefixed camera/lens strings, a 5-byte exposure block, an
// 8-byte recipe block, all multi-bit fields MSB-first. Replaces PlaceholderQrLinkPayloadEncoder
// (base64url(JSON)), which produced ~27x larger payloads (494 bytes vs ~16-44) purely because
// it never implemented this format — that's what made every QR unnecessarily dense.
//
// PhSpectre's own FilmRecipe/PhotoMetadata vocabularies (FujiRecipeExtractor's decoded strings,
// RecipeFieldOptions' manual-entry lists) don't exactly match the viewer's fixed SIMS/WB/DR/
// GRAIN enums — legacy/rare values (e.g. "Studio Portrait", "Velvia (old)") and finer detail
// PhSpectre doesn't capture at all (grain size, custom-WB Kelvin number) fall back to the
// closest available slot. This is a lossy but deliberate mapping, not a bug — every fallback is
// called out at its own mapping table below.
public sealed class BitPackedQrLinkPayloadEncoder : IQrLinkPayloadEncoder
{
    public string Encode(QrLinkFields fields)
    {
        bool hasRecipe = fields.Recipe != null;
        bool hasNote = !string.IsNullOrWhiteSpace(fields.Note);
        // bit1 (hasPhoto) is never set — photo hosting is out of scope (v3 spec §7).
        byte flags = (byte)((hasRecipe ? 1 : 0) | (hasNote ? 4 : 0));

        using var ms = new MemoryStream();
        ms.WriteByte(flags);
        WriteString(ms, StripFujifilmPrefix(fields.Camera));
        WriteString(ms, fields.Lens);
        var exposure = EncodeExposure(fields);
        ms.Write(exposure, 0, exposure.Length);

        if (hasRecipe)
        {
            var recipeBytes = EncodeRecipe(fields.Recipe!);
            ms.Write(recipeBytes, 0, recipeBytes.Length);
        }

        if (hasNote) WriteString(ms, fields.Note);

        return ToBase64Url(ms.ToArray());
    }

    // ── exposure block: 5 bytes, MSB-first, 34 packed bits + 6 zero pad ─────────────────────
    private static byte[] EncodeExposure(QrLinkFields f)
    {
        var w = new BitWriter(40);
        w.Write(ParseFocalMm(f.Focal) ?? 0, 10);
        w.Write(ParseApertureX10(f.Aperture) ?? 0, 8);
        w.Write(EncodeShutterCode(ParseShutterSeconds(f.Shutter)), 8);
        w.Write(EncodeIsoCode(ParseIso(f.Iso)), 8);
        return w.ToBytes(5);
    }

    // ── recipe block: 8 bytes, MSB-first, 62 packed bits + 2 zero pad ───────────────────────
    private static byte[] EncodeRecipe(FilmRecipe r)
    {
        var w = new BitWriter(64);
        w.Write(SimCode(r.FilmSimulation), 5);
        w.Write(WbCode(r.WhiteBalance), 4);
        w.Write(0, 10); // kelvinEnc: FujiRecipeExtractor never captures the raw Kelvin number
        w.Write(Math.Clamp((r.WhiteBalanceShift?.Red ?? 0) + 9, 0, 18), 5);
        w.Write(Math.Clamp((r.WhiteBalanceShift?.Blue ?? 0) + 9, 0, 18), 5);
        w.Write(DrCode(r.DynamicRange), 2);
        w.Write(HalfStopRaw(r.HighlightTone), 4);
        w.Write(HalfStopRaw(r.ShadowTone), 4);
        w.Write(SignedRaw(r.Color), 4);
        w.Write(SignedRaw(r.Sharpness), 4);
        w.Write(SignedRaw(r.NoiseReduction), 4);
        w.Write(SignedRaw(RoundToInt(r.Clarity)), 4);
        w.Write(GrainCode(r.GrainEffect), 3);
        w.Write(ChromeCode(r.ColorChromeEffect), 2);
        w.Write(ChromeCode(r.ColorChromeFxBlue), 2);
        return w.ToBytes(8);
    }

    private static int SignedRaw(int? v) => Math.Clamp((v ?? 0) + 8, 0, 15);
    private static int HalfStopRaw(decimal? v) => Math.Clamp((int)Math.Round((v ?? 0m) * 2, MidpointRounding.AwayFromZero) + 8, 0, 15);
    private static int? RoundToInt(decimal? v) => v.HasValue ? (int)Math.Round(v.Value, MidpointRounding.AwayFromZero) : null;

    // ── string / byte-stream helpers ─────────────────────────────────────────────────────────

    private static string? StripFujifilmPrefix(string? camera)
    {
        if (string.IsNullOrWhiteSpace(camera)) return camera;
        const string prefix = "FUJIFILM ";
        return camera.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? camera[prefix.Length..].Trim() : camera;
    }

    private static void WriteString(Stream ms, string? s)
    {
        byte[] bytes = string.IsNullOrEmpty(s) ? [] : Encoding.UTF8.GetBytes(s);
        if (bytes.Length > 255) bytes = Encoding.UTF8.GetBytes(TruncateToUtf8ByteLimit(s!, 255));
        ms.WriteByte((byte)bytes.Length);
        ms.Write(bytes, 0, bytes.Length);
    }

    // Character-count trim (not byte-slice) so a multi-byte UTF-8 char never gets split —
    // only matters for the free-text Note field; Camera/Lens are short EXIF strings in practice.
    private static string TruncateToUtf8ByteLimit(string s, int maxBytes)
    {
        int len = s.Length;
        while (len > 0 && Encoding.UTF8.GetByteCount(s[..len]) > maxBytes) len--;
        return s[..len];
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // MSB-first bit accumulator mirroring the viewer's own bitWriter(totalBits)/toBytes(byteLen)
    // — a ulong comfortably holds every field this format ever accumulates (max 62 of 64 bits
    // for the recipe block), so no need for anything wider.
    private sealed class BitWriter(int totalBits)
    {
        private ulong _acc;
        private int _used;

        public void Write(int value, int width)
        {
            ulong mask = (1UL << width) - 1UL;
            _acc = (_acc << width) | ((ulong)value & mask);
            _used += width;
        }

        public byte[] ToBytes(int byteLen)
        {
            ulong v = _acc << (totalBits - _used); // right-pad the unwritten tail with zero bits
            var result = new byte[byteLen];
            for (int i = byteLen - 1; i >= 0; i--)
            {
                result[i] = (byte)(v & 0xFF);
                v >>= 8;
            }
            return result;
        }
    }

    // ── exposure field parsers — reverse PaletteImageRenderer.ReadMetadata's own formatting
    // ("35mm", "f/1.4", "1/500s" or "2s", "ISO 400") back into raw numbers. These strings are
    // also the user-editable metadata text fields, but always committed in this same format. ──

    private static readonly Regex FocalRegex = new(@"^(\d+)\s*mm$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ApertureRegex = new(@"^f/(\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ShutterFractionRegex = new(@"^1/(\d+(?:\.\d+)?)s$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ShutterWholeRegex = new(@"^(\d+(?:\.\d+)?)s$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IsoRegex = new(@"^ISO\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static int? ParseFocalMm(string? focal)
    {
        if (string.IsNullOrWhiteSpace(focal)) return null;
        var m = FocalRegex.Match(focal.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? Math.Clamp(v, 0, 1023) : null;
    }

    private static int? ParseApertureX10(string? aperture)
    {
        if (string.IsNullOrWhiteSpace(aperture)) return null;
        var m = ApertureRegex.Match(aperture.Trim());
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? Math.Clamp((int)Math.Round(v * 10), 0, 254)
            : null;
    }

    private static double? ParseShutterSeconds(string? shutter)
    {
        if (string.IsNullOrWhiteSpace(shutter)) return null;
        string s = shutter.Trim();
        var frac = ShutterFractionRegex.Match(s);
        if (frac.Success && double.TryParse(frac.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double den) && den > 0)
            return 1.0 / den;
        var whole = ShutterWholeRegex.Match(s);
        return whole.Success && double.TryParse(whole.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)
            ? sec
            : null;
    }

    private static int? ParseIso(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        var m = IsoRegex.Match(iso.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : null;
    }

    private static int ClampByte(int n) => Math.Clamp(n, 0, 254); // 255 stays reserved for "unknown"

    private static int EncodeShutterCode(double? seconds) =>
        seconds is > 0 ? ClampByte((int)Math.Round(Math.Log2(1.0 / seconds.Value) * 3) + 64) : 255;

    private static int EncodeIsoCode(int? iso) =>
        iso is > 0 ? ClampByte((int)Math.Round(3 * Math.Log2(iso.Value / 100.0)) + 64) : 255;

    // ── recipe field -> viewer-enum mappings ────────────────────────────────────────────────
    // Viewer arrays (from index.html): SIMS[20], WB[12], DR[4], GRAIN[5], CHROME[3].

    private static int SimCode(string? sim) => sim != null && SimCodes.TryGetValue(sim, out int c) ? c : 0;

    private static readonly Dictionary<string, int> SimCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Provia/Standard"] = 0,
        // Legacy Portrait-mode simulations (pre-modern-body, essentially never seen from
        // FujiRecipeExtractor in practice) — no dedicated viewer slot, fall back to the
        // closest color character.
        ["Studio Portrait"] = 0,
        ["Studio Portrait Increased Sharpness"] = 0,
        ["Studio Portrait Enhanced Saturation"] = 1,
        ["Studio Portrait Ex"] = 1,
        ["Velvia"] = 1,
        ["Velvia (old)"] = 1,
        ["Astia (Soft)"] = 2,
        ["Classic Chrome"] = 3,
        ["Pro Neg. Hi"] = 4,
        ["Pro Neg. Std"] = 5,
        ["Classic Negative"] = 6,
        ["Eterna"] = 7,
        ["Eterna Bleach Bypass"] = 8,
        ["Nostalgic Neg"] = 9,
        ["Reala ACE"] = 10,
        ["Acros"] = 11,
        ["Acros + Ye Filter"] = 12,
        ["Acros + R Filter"] = 13,
        ["Acros + G Filter"] = 14,
        ["Monochrome"] = 15,
        ["Monochrome + Ye Filter"] = 16,
        ["Monochrome + R Filter"] = 17,
        ["Monochrome + G Filter"] = 18,
        ["Sepia"] = 19,
    };

    private static int WbCode(string? wb) => wb != null && WbCodes.TryGetValue(wb, out int c) ? c : 0;

    private static readonly Dictionary<string, int> WbCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Auto"] = 0,
        ["Auto (white priority)"] = 1,
        ["Auto (ambiance priority)"] = 2, // viewer spells this "ambient" — index-only match
        ["Daylight"] = 3,
        ["Cloudy"] = 4, // viewer calls this slot "Shade" — same setting, index-only match
        ["Fluorescent (Daylight)"] = 5,
        ["Fluorescent (Warm White)"] = 6,
        ["Fluorescent (Cool White)"] = 7,
        ["Incandescent"] = 8,
        ["Underwater"] = 9,
        ["Kelvin"] = 10,
        ["Custom"] = 11,
        ["Custom 2"] = 11,
        ["Custom 3"] = 11,
        ["Custom 4"] = 11,
        // "Flash" has no equivalent slot in the viewer's fixed WB enum — falls back to Auto (0).
    };

    private static int DrCode(string? dr) => dr switch
    {
        "DR100" => 1,
        "DR200" => 2,
        "DR400" => 3,
        // Auto, "Off" (manual-entry-only), "Film Simulation", or any other DR{d} value all fall
        // back to Auto — the viewer's DR enum has no slot for any of those.
        _ => 0,
    };

    private static int GrainCode(string? grain) => grain switch
    {
        // FujiRecipeExtractor only reports Off/Weak/Strong (grain SIZE isn't decoded at all),
        // so Weak/Strong assume "· Small" — the viewer's more detailed 5-value enum has no way
        // to distinguish Small from Large from what we have.
        "Weak" => 1,
        "Strong" => 2,
        _ => 0,
    };

    private static int ChromeCode(string? chrome) => chrome switch
    {
        "Weak" => 1,
        "Strong" => 2,
        _ => 0,
    };
}
