using PhSpectre.Models;

namespace PhSpectre.Camera;

/// <summary>One custom-setting slot as read from the body. <see cref="Name"/> is null for an empty slot.</summary>
public sealed record FujiSlotInfo(int Slot, string? Name, FilmRecipe Recipe, IReadOnlyList<string> Unmapped,
    IReadOnlyDictionary<ushort, byte[]> Raw)
{
    public bool IsEmpty => Name == null;
}

/// <summary>
/// Everything the UI needs to offer a camera: model, whether writing is allowed, and every slot.
/// <see cref="Profile"/> is null for a body outside the table, in which case <see cref="Slots"/> is empty.
/// </summary>
public sealed record FujiCameraSnapshot(string Model, string Firmware, FujiCameraProfile? Profile,
    bool RawConvMode, IReadOnlyList<FujiSlotInfo> Slots, string? ReadError = null)
{
    public bool CanWrite => Profile is { WriteVerified: true, Access: FujiBankAccess.Direct } && RawConvMode;

    /// <summary>True when slots can be read property by property (see <see cref="FujiBankAccess"/>).</summary>
    public bool DirectAccess => Profile is { Access: FujiBankAccess.Direct };
}

public static class FujiSlotReader
{
    private const ushort UsbModeProperty = 0xD16E;
    private const ushort BankSelector = 0xD18C;

    /// <summary>Reads all slots. Read-only apart from the bank selector, which is put back.</summary>
    public static async Task<FujiCameraSnapshot> ReadAllAsync(PtpSession session, CancellationToken ct = default)
    {
        var info = await session.GetDeviceInfoAsync(ct);
        var profile = FujiCameraProfiles.Find(info.Model);
        if (profile == null)
            return new FujiCameraSnapshot(info.Model, info.DeviceVersion, null, false, []);

        if (profile.Access != FujiBankAccess.Direct)
            return new FujiCameraSnapshot(info.Model, info.DeviceVersion, profile, false, []);

        bool rawConv;
        try
        {
            var mode = await session.GetPropertyAsync(UsbModeProperty, ct);
            rawConv = mode.Length >= 2 && (mode[0] | mode[1] << 8) == 6;
        }
        catch (PtpException)
        {
            rawConv = false;
        }
        if (!rawConv)
            return new FujiCameraSnapshot(info.Model, info.DeviceVersion, profile, false, []);

        var slots = new List<FujiSlotInfo>();
        byte[] originalSelector;
        try
        {
            originalSelector = await session.GetPropertyAsync(BankSelector, ct);
        }
        catch (PtpException e)
        {
            return new FujiCameraSnapshot(info.Model, info.DeviceVersion, profile, true, [], $"Bank selector unreadable: {e.Message}");
        }
        string? readError = null;
        try
        {
            for (var slot = 1; slot <= profile.SlotCount; slot++)
            {
                await session.SetPropertyAsync(BankSelector, [(byte)slot, 0], ct);
                var raw = await FujiRecipeWriter.ReadSlotAsync(session, profile, ct);
                var decoded = FujiSlotCodec.Decode(raw, info.Model);
                slots.Add(new FujiSlotInfo(slot, decoded.Recipe.RecipeName, decoded.Recipe, decoded.Unmapped, raw));
            }
        }
        catch (Exception e) when (e is PtpException or FujiWriteException)
        {
            // An unverified body may not follow the X-T5's layout; report it instead of failing the whole dialog.
            slots.Clear();
            readError = e.Message;
        }
        finally
        {
            await FujiRecipeWriter.TryRestoreSelectorAsync(session, originalSelector);
        }
        return new FujiCameraSnapshot(info.Model, info.DeviceVersion, profile, true, slots, readError);
    }
}
