namespace PhSpectre.Camera;

/// <summary>How a body exposes its custom-setting slots over USB.</summary>
public enum FujiBankAccess
{
    /// <summary>Slots are read and written property by property (bank selector 0xD18C), as on the X-T5.</summary>
    Direct,
    /// <summary>
    /// Older bodies: no working per-slot path was found in public reports; slots live only inside the full
    /// settings backup file. Not supported here, the UI shows manual steps instead.
    /// </summary>
    BackupOnly,
}

/// <summary>
/// What is known about one camera body. Slot counts differ per body. <see cref="WriteVerified"/> stays false
/// until a body's encodings have been checked against JPEGs shot from its own slots; unverified bodies are
/// read-only. <see cref="AbsentProperties"/> are slot properties the body does not have (reads fail), so a
/// backup may skip them and the encoder must not write them.
/// </summary>
public sealed record FujiCameraProfile(
    string Model,
    int SlotCount,
    bool WriteVerified,
    FujiBankAccess Access = FujiBankAccess.Direct,
    IReadOnlySet<ushort>? AbsentProperties = null)
{
    public bool IsAbsent(ushort property) => AbsentProperties?.Contains(property) ?? false;
}

public static class FujiCameraProfiles
{
    private static readonly IReadOnlySet<ushort> NoSmoothSkin = new HashSet<ushort> { FujiSlotCodec.PropSmoothSkin };

    // Slot counts: X-T5 and X-M5 from live probes here; X-T50 measured by fujifilm-ptp-reference; the rest from
    // that project's body list (docs/07-cameras.md) and the cameras' manuals (C1-C7 unless noted). Everything but
    // the X-T5 is read-only until someone checks it against a real body.
    private static readonly IReadOnlyList<FujiCameraProfile> Known =
    [
        // X-Processor 5 (same generation as the verified X-T5)
        new("X-T5", SlotCount: 7, WriteVerified: true),
        new("X-M5", SlotCount: 4, WriteVerified: true),
        new("X-H2", SlotCount: 7, WriteVerified: false),
        new("X-H2S", SlotCount: 7, WriteVerified: false),
        new("X-T50", SlotCount: 7, WriteVerified: false),
        new("X-T30 III", SlotCount: 7, WriteVerified: false),
        new("X-E5", SlotCount: 7, WriteVerified: false),
        new("X100VI", SlotCount: 7, WriteVerified: false),
        new("X-S20", SlotCount: 4, WriteVerified: false, AbsentProperties: NoSmoothSkin),

        // X-Processor 4: per-slot access reported working on X-T30 II and X100V
        new("X-T4", SlotCount: 7, WriteVerified: false),
        new("X-T30 II", SlotCount: 7, WriteVerified: false),
        new("X-E4", SlotCount: 7, WriteVerified: false),
        new("X100V", SlotCount: 7, WriteVerified: false),
        new("X-S10", SlotCount: 4, WriteVerified: false, AbsentProperties: NoSmoothSkin),

        // Older: only reachable through the full settings backup
        new("X-T3", SlotCount: 7, WriteVerified: false, FujiBankAccess.BackupOnly),
        new("X-T30", SlotCount: 7, WriteVerified: false, FujiBankAccess.BackupOnly),
        new("X100F", SlotCount: 7, WriteVerified: false, FujiBankAccess.BackupOnly),
    ];

    public static IReadOnlyList<FujiCameraProfile> All => Known;

    /// <summary>Looks a body up by the model string it reports, ignoring case, spaces and hyphens ("X-T30 II" = "XT30II").</summary>
    public static FujiCameraProfile? Find(string model)
    {
        var key = Normalize(model);
        return Known.FirstOrDefault(p => Normalize(p.Model) == key);
    }

    private static string Normalize(string model) =>
        new(model.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
