using PhSpectre.Models;

namespace PhSpectre.Camera;

public sealed class FujiWriteException : Exception
{
    public FujiWriteException(string message) : base(message) { }
}

public enum FujiWritePhase { Writing, Verifying }

/// <summary>One step of an apply: <see cref="Index"/> of <see cref="Total"/> settings has been written or verified.</summary>
public sealed record FujiWriteProgress(FujiWritePhase Phase, int Index, int Total, string Description);

public sealed record FujiWriteOptions
{
    /// <summary>Plan only: read and validate, send nothing but the bank selector.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Allow replacing a slot that already has a name. Off by default so a user recipe is never overwritten by accident.</summary>
    public bool AllowOverwrite { get; init; }

    /// <summary>Receives a callback after every write and every read-back; null for none.</summary>
    public IProgress<FujiWriteProgress>? Progress { get; init; }
}

public sealed record FujiWriteReport(
    string Model,
    int Slot,
    bool DryRun,
    bool Success,
    IReadOnlyList<string> Planned,
    IReadOnlyList<string> Skipped,
    IReadOnlyDictionary<ushort, byte[]> Backup,
    IReadOnlyList<string> Problems,
    bool RolledBack,
    bool Cancelled = false);

public sealed record FujiRestoreReport(bool Success, IReadOnlyList<string> Problems);

/// <summary>
/// Writes a <see cref="FilmRecipe"/> into one custom-setting slot. Every write is verified by reading it back
/// (an OK response alone does not prove a setting changed), the slot is backed up first, and any failure or
/// cancellation restores the backup. The bank selector is always put back on exit.
/// </summary>
public static class FujiRecipeWriter
{
    private const ushort UsbModeProperty = 0xD16E;
    private const ushort RawConvMode = 6;
    private const ushort BankSelector = 0xD18C;
    private const ushort FirstSlotProperty = 0xD18D;
    private const ushort LastSlotProperty = 0xD1A4;

    public static async Task<FujiWriteReport> WriteAsync(PtpSession session, int slot, FilmRecipe recipe,
        FujiWriteOptions options, CancellationToken ct = default)
    {
        var info = await session.GetDeviceInfoAsync(ct);
        var profile = FujiCameraProfiles.Find(info.Model)
            ?? throw new FujiWriteException($"Unknown camera '{info.Model}': refusing to write.");
        if (profile.Access != FujiBankAccess.Direct)
            throw new FujiWriteException($"{profile.Model} keeps its slots inside the full settings backup; direct slot access is not supported.");
        if (!options.DryRun && !profile.WriteVerified)
            throw new FujiWriteException($"{profile.Model} is read-only until its encodings are verified on a real device.");
        if (slot < 1 || slot > profile.SlotCount)
            throw new FujiWriteException($"{profile.Model} has slots 1-{profile.SlotCount}, not {slot}.");

        await EnsureRawConvModeAsync(session, ct);

        var encoding = FujiSlotEncoder.Encode(recipe, profile);
        var originalSelector = await session.GetPropertyAsync(BankSelector, ct);
        try
        {
            await session.SetPropertyAsync(BankSelector, U16Bytes((ushort)slot), ct);
            var backup = await ReadSlotAsync(session, profile, ct);

            var existingName = FujiSlotCodec.DecodeName(backup);
            if (existingName != null && !options.AllowOverwrite && !options.DryRun)
                throw new FujiWriteException(
                    $"Slot {slot} already holds '{existingName}'. Pass AllowOverwrite to replace it.");

            var planned = encoding.Writes.Select(w => w.Description).ToList();
            if (options.DryRun)
                return new FujiWriteReport(profile.Model, slot, true, true, planned, encoding.Skipped, backup, [], false);

            return await ApplyAsync(session, profile, slot, encoding, backup, planned, options.Progress, ct);
        }
        finally
        {
            await TryRestoreSelectorAsync(session, originalSelector);
        }
    }

    /// <summary>
    /// Puts a slot back to a previously saved backup (undo, or recovery after a failed write). Only properties
    /// that differ are written, and every one is read back.
    /// </summary>
    public static async Task<FujiRestoreReport> RestoreAsync(PtpSession session, int slot,
        IReadOnlyDictionary<ushort, byte[]> backup, CancellationToken ct = default)
    {
        var info = await session.GetDeviceInfoAsync(ct);
        var profile = FujiCameraProfiles.Find(info.Model)
            ?? throw new FujiWriteException($"Unknown camera '{info.Model}': refusing to write.");
        if (profile.Access != FujiBankAccess.Direct)
            throw new FujiWriteException($"{profile.Model} keeps its slots inside the full settings backup; direct slot access is not supported.");
        if (!profile.WriteVerified)
            throw new FujiWriteException($"{profile.Model} is read-only until its encodings are verified on a real device.");
        if (slot < 1 || slot > profile.SlotCount)
            throw new FujiWriteException($"{profile.Model} has slots 1-{profile.SlotCount}, not {slot}.");
        await EnsureRawConvModeAsync(session, ct);

        var originalSelector = await session.GetPropertyAsync(BankSelector, ct);
        var problems = new List<string>();
        try
        {
            await session.SetPropertyAsync(BankSelector, U16Bytes((ushort)slot), ct);
            var current = await ReadSlotAsync(session, profile, ct);

            // Kelvin only means something under the Kelvin WB mode; under others the camera rewrites it itself.
            var kelvinMeaningful = backup.TryGetValue(FujiSlotCodec.PropWhiteBalance, out var wb)
                && wb.Length >= 2 && (ushort)(wb[0] | wb[1] << 8) == FujiSlotCodec.WbKelvin;

            var order = RestoreOrder(backup.Keys)
                .Where(p => p != FujiSlotCodec.PropColorTemperature || kelvinMeaningful)
                .Where(p => current.TryGetValue(p, out var cur) && !cur.AsSpan().SequenceEqual(backup[p]))
                .ToList();

            foreach (var prop in order)
            {
                try
                {
                    // D-Range Priority is written last (see RestoreOrder), but a stored DR is only accepted with it off.
                    foreach (var value in RestoreValues(prop, backup[prop]))
                        await session.SetPropertyAsync(prop, value, CancellationToken.None);
                }
                catch (Exception e) when (e is PtpException or IOException)
                {
                    problems.Add($"0x{prop:X4}: {e.Message}");
                }
            }

            foreach (var prop in order)
            {
                var actual = await session.GetPropertyAsync(prop, CancellationToken.None);
                if (!Matches(prop, backup[prop], actual))
                    problems.Add($"0x{prop:X4}: restored value reads {Convert.ToHexString(actual)}, expected {Convert.ToHexString(backup[prop])}");
            }
        }
        finally
        {
            await TryRestoreSelectorAsync(session, originalSelector);
        }
        return new FujiRestoreReport(problems.Count == 0, problems);
    }

    /// <summary>Reads every slot property of the currently selected bank. The caller owns the bank selector.</summary>
    internal static async Task<Dictionary<ushort, byte[]>> ReadSlotAsync(PtpSession session, FujiCameraProfile profile, CancellationToken ct)
    {
        var slot = new Dictionary<ushort, byte[]>();
        for (var prop = FirstSlotProperty; prop <= LastSlotProperty; prop++)
        {
            if (profile.IsAbsent(prop)) continue;
            try
            {
                slot[prop] = await session.GetPropertyAsync(prop, ct);
            }
            catch (PtpException e)
            {
                // A backup with holes cannot be restored, so do not start writing without a complete one.
                throw new FujiWriteException($"Cannot back up slot: property 0x{prop:X4} unreadable ({e.Message}).");
            }
        }
        return slot;
    }

    internal static async Task EnsureRawConvModeAsync(PtpSession session, CancellationToken ct)
    {
        var mode = ReadU16(await session.GetPropertyAsync(UsbModeProperty, ct));
        if (mode != RawConvMode)
            throw new FujiWriteException($"Camera must be in USB RAW CONV./BACKUP RESTORE mode (reports mode {mode}).");
    }

    internal static async Task TryRestoreSelectorAsync(PtpSession session, byte[] selector)
    {
        try
        {
            await session.SetPropertyAsync(BankSelector, selector, CancellationToken.None);
        }
        catch (Exception e) when (e is PtpException or IOException)
        {
            // The camera is gone; there is no selector left to restore and the caller already has the real failure.
        }
    }

    private static async Task<FujiWriteReport> ApplyAsync(PtpSession session, FujiCameraProfile profile, int slot,
        SlotEncoding encoding, Dictionary<ushort, byte[]> backup, List<string> planned,
        IProgress<FujiWriteProgress>? progress, CancellationToken ct)
    {
        var problems = new List<string>();
        var touched = new List<ushort>();
        var total = encoding.Writes.Count;

        try
        {
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var write = encoding.Writes[i];
                try
                {
                    // Not cancellable mid-transaction: abandoning a PTP transfer can desync the camera.
                    await session.SetPropertyAsync(write.Property, write.Value, CancellationToken.None);
                    touched.Add(write.Property);
                }
                catch (PtpException e)
                {
                    // A refused write changed nothing, so it is not in `touched`.
                    problems.Add($"{write.Description}: {e.Message}");
                    return await RollBackAsync(session, profile, slot, encoding, backup, planned, touched, problems);
                }
                progress?.Report(new FujiWriteProgress(FujiWritePhase.Writing, i + 1, total, write.Description));
            }

            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var write = encoding.Writes[i];
                var actual = await session.GetPropertyAsync(write.Property, CancellationToken.None);
                if (!Matches(write, actual))
                    problems.Add($"{write.Description}: read back {Convert.ToHexString(actual)}, wrote {Convert.ToHexString(write.Value)}");
                progress?.Report(new FujiWriteProgress(FujiWritePhase.Verifying, i + 1, total, write.Description));
            }
        }
        catch (OperationCanceledException)
        {
            problems.Add("Cancelled by the user.");
            var cancelled = await RollBackAsync(session, profile, slot, encoding, backup, planned, touched, problems);
            return cancelled with { Cancelled = true };
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
        {
            // Cable pulled or camera switched off: try to roll back, it will report if it cannot.
            problems.Add($"Connection lost: {e.Message}");
            return await RollBackAsync(session, profile, slot, encoding, backup, planned, touched, problems);
        }

        return problems.Count == 0
            ? new FujiWriteReport(profile.Model, slot, false, true, planned, encoding.Skipped, backup, [], false)
            : await RollBackAsync(session, profile, slot, encoding, backup, planned, touched, problems);
    }

    private static async Task<FujiWriteReport> RollBackAsync(PtpSession session, FujiCameraProfile profile, int slot,
        SlotEncoding encoding, Dictionary<ushort, byte[]> backup, List<string> planned, List<ushort> touched,
        List<string> problems)
    {
        // Restore in the same dependency order the writes used so refusal rules (film sim before Color, etc.) hold.
        var order = encoding.Writes.Select(w => w.Property).Where(touched.Contains).Distinct().ToList();
        var failures = new List<string>();

        foreach (var prop in order)
        {
            try
            {
                foreach (var value in RestoreValues(prop, backup[prop]))
                    await session.SetPropertyAsync(prop, value, CancellationToken.None);
            }
            catch (Exception e) when (e is PtpException or IOException or TimeoutException or InvalidOperationException)
            {
                failures.Add($"0x{prop:X4}: {e.Message}");
            }
        }

        foreach (var prop in order)
        {
            try
            {
                var actual = await session.GetPropertyAsync(prop, CancellationToken.None);
                if (!actual.AsSpan().SequenceEqual(backup[prop]) && !Matches(prop, backup[prop], actual))
                    failures.Add($"0x{prop:X4}: restored value reads {Convert.ToHexString(actual)}, expected {Convert.ToHexString(backup[prop])}");
            }
            catch (Exception e) when (e is PtpException or IOException or TimeoutException or InvalidOperationException)
            {
                failures.Add($"0x{prop:X4}: {e.Message}");
            }
        }

        problems.AddRange(failures.Select(f => $"rollback: {f}"));
        return new FujiWriteReport(profile.Model, slot, false, false, planned, encoding.Skipped, backup, problems, failures.Count == 0);
    }

    /// <summary>
    /// Dependency order for putting a whole slot back: name and film simulation first, DR only with D-Range
    /// Priority off, Kelvin before the WB shifts, and D-Range Priority's own value last.
    /// </summary>
    private static IEnumerable<ushort> RestoreOrder(IEnumerable<ushort> available)
    {
        ushort[] first =
        [
            FujiSlotCodec.PropName, FujiSlotCodec.PropFilmSimulation, FujiSlotCodec.PropMonoWarmCool,
            FujiSlotCodec.PropDynamicRange, FujiSlotCodec.PropGrain, FujiSlotCodec.PropColorChrome,
            FujiSlotCodec.PropColorChromeBlue, FujiSlotCodec.PropSmoothSkin, FujiSlotCodec.PropWhiteBalance,
            FujiSlotCodec.PropColorTemperature, FujiSlotCodec.PropWbShiftRed, FujiSlotCodec.PropWbShiftBlue,
            FujiSlotCodec.PropHighlightTone, FujiSlotCodec.PropShadowTone, FujiSlotCodec.PropColor,
            FujiSlotCodec.PropSharpness, FujiSlotCodec.PropNoiseReduction, FujiSlotCodec.PropClarity,
        ];
        var set = available.ToHashSet();
        foreach (var p in first)
            if (set.Remove(p)) yield return p;
        foreach (var p in set.Where(p => p != FujiSlotCodec.PropDRangePriority).OrderBy(p => p)) yield return p;
        if (set.Contains(FujiSlotCodec.PropDRangePriority)) yield return FujiSlotCodec.PropDRangePriority;
    }

    /// <summary>
    /// Grain reads back 6/7 for "Off, size remembered" but those codes are refused on write; a size followed by
    /// Off reproduces them.
    /// </summary>
    internal static IEnumerable<byte[]> RestoreValues(ushort prop, byte[] original)
    {
        if (prop == FujiSlotCodec.PropGrain && original.Length >= 2 && original[0] is 6 or 7)
        {
            yield return U16Bytes((ushort)(original[0] == 6 ? 2 : 4));
            yield return U16Bytes(1);
            yield break;
        }
        yield return original;
    }

    private static bool Matches(PropertyWrite write, byte[] actual) => Matches(write.Property, write.Value, actual);

    private static bool Matches(ushort property, byte[] expected, byte[] actual)
    {
        // Off with a remembered size reads back as 6/7, never as the 1 that was written.
        if (property == FujiSlotCodec.PropGrain && expected[0] == 1)
            return actual.Length >= 2 && actual[0] is 1 or 6 or 7 && actual[1] == 0;
        return actual.AsSpan().SequenceEqual(expected);
    }

    private static ushort ReadU16(byte[] bytes) => (ushort)(bytes[0] | bytes[1] << 8);

    private static byte[] U16Bytes(ushort value) => [(byte)value, (byte)(value >> 8)];
}
