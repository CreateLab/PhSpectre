namespace PhSpectre.Camera;

/// <summary>How a body exposes its custom-setting slots over USB.</summary>
public enum FujiBankAccess
{
    /// <summary>Slots are read and written property by property (bank selector 0xD18C), as on the X-T5.</summary>
    Direct,
    /// <summary>Older bodies: slots exist only inside the settings backup file. Not supported; the UI shows manual steps.</summary>
    BackupOnly,
}

/// <summary>
/// What is known about a camera body. <see cref="WriteVerified"/> stays false until its encodings were checked
/// against JPEGs from its own slots. <see cref="AbsentProperties"/> are slot properties the body does not have.
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

    // Slot counts: X-T5 and X-M5 probed here, X-T50 and the rest from fujifilm-ptp-reference and manuals. Only the
    // X-T5 is writable.
    private static readonly IReadOnlyList<FujiCameraProfile> Known =
    [
        // X-Processor 5
        new("X-T5", SlotCount: 7, WriteVerified: true),
        new("X-M5", SlotCount: 4, WriteVerified: true),
        new("X-H2", SlotCount: 7, WriteVerified: false),
        new("X-H2S", SlotCount: 7, WriteVerified: false),
        new("X-T50", SlotCount: 7, WriteVerified: false),
        new("X-T30 III", SlotCount: 7, WriteVerified: false),
        new("X-E5", SlotCount: 7, WriteVerified: false),
        new("X100VI", SlotCount: 7, WriteVerified: false),
        new("X-S20", SlotCount: 4, WriteVerified: false, AbsentProperties: NoSmoothSkin),

        // X-Processor 4: per-slot access reported on X-T30 II and X100V
        new("X-T4", SlotCount: 7, WriteVerified: false),
        new("X-T30 II", SlotCount: 7, WriteVerified: false),
        new("X-E4", SlotCount: 7, WriteVerified: false),
        new("X100V", SlotCount: 7, WriteVerified: false),
        new("X-S10", SlotCount: 4, WriteVerified: false, AbsentProperties: NoSmoothSkin),

        // Older: settings backup only
        new("X-T3", SlotCount: 7, WriteVerified: false, FujiBankAccess.BackupOnly),
        new("X-T30", SlotCount: 7, WriteVerified: false, FujiBankAccess.BackupOnly),
        new("X100F", SlotCount: 7, WriteVerified: false, FujiBankAccess.BackupOnly),
    ];

    public static IReadOnlyList<FujiCameraProfile> All => Known;

    /// <summary>Finds a body by model name, ignoring case, spaces and hyphens.</summary>
    public static FujiCameraProfile? Find(string model)
    {
        var key = Normalize(model);
        return Known.FirstOrDefault(p => Normalize(p.Model) == key);
    }

    private static string Normalize(string model) =>
        new(model.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
