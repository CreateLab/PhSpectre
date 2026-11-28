using PhSpectre.Localization;
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
    /// <summary>Dry run: only the bank selector is written.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Allow replacing a named slot.</summary>
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
/// Writes a <see cref="FilmRecipe"/> into a slot: backup first, every write read back, restore on failure or
/// cancellation, bank selector put back.
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
            ?? throw new FujiWriteException(Loc.F("Unknown camera '{0}': refusing to write.", info.Model));
        if (profile.Access != FujiBankAccess.Direct)
            throw new FujiWriteException(Loc.F("{0} keeps its slots inside the full settings backup; direct slot access is not supported.", profile.Model));
        if (!options.DryRun && !profile.WriteVerified)
            throw new FujiWriteException(Loc.F("{0} is read-only until its encodings are verified on a real device.", profile.Model));
        if (slot < 1 || slot > profile.SlotCount)
            throw new FujiWriteException(Loc.F("{0} has slots 1-{1}, not {2}.", profile.Model, profile.SlotCount, slot));

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

    /// <summary>Restores a slot from a backup, writing only what differs and reading it back.</summary>
    public static async Task<FujiRestoreReport> RestoreAsync(PtpSession session, int slot,
        IReadOnlyDictionary<ushort, byte[]> backup, CancellationToken ct = default)
    {
        var info = await session.GetDeviceInfoAsync(ct);
        var profile = FujiCameraProfiles.Find(info.Model)
            ?? throw new FujiWriteException(Loc.F("Unknown camera '{0}': refusing to write.", info.Model));
        if (profile.Access != FujiBankAccess.Direct)
            throw new FujiWriteException(Loc.F("{0} keeps its slots inside the full settings backup; direct slot access is not supported.", profile.Model));
        if (!profile.WriteVerified)
            throw new FujiWriteException(Loc.F("{0} is read-only until its encodings are verified on a real device.", profile.Model));
        if (slot < 1 || slot > profile.SlotCount)
            throw new FujiWriteException(Loc.F("{0} has slots 1-{1}, not {2}.", profile.Model, profile.SlotCount, slot));
        await EnsureRawConvModeAsync(session, ct);

        var originalSelector = await session.GetPropertyAsync(BankSelector, ct);
        var problems = new List<string>();
        try
        {
            await session.SetPropertyAsync(BankSelector, U16Bytes((ushort)slot), ct);
            var current = await ReadSlotAsync(session, profile, ct);

            // Under other WB modes the camera rewrites Kelvin itself.
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
                    // A stored DR is accepted only with D-Range Priority off.
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
                    problems.Add(Loc.F("0x{0:X4}: restored value reads {1}, expected {2}", prop, Convert.ToHexString(actual), Convert.ToHexString(backup[prop])));
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
                // An incomplete backup cannot be restored.
                throw new FujiWriteException(Loc.F("Cannot back up slot: property 0x{0:X4} unreadable ({1}).", prop, e.Message));
            }
        }
        return slot;
    }

    internal static async Task EnsureRawConvModeAsync(PtpSession session, CancellationToken ct)
    {
        var mode = ReadU16(await session.GetPropertyAsync(UsbModeProperty, ct));
        if (mode != RawConvMode)
            throw new FujiWriteException(Loc.F("Camera must be in USB RAW CONV./BACKUP RESTORE mode (reports mode {0}).", mode));
    }

    internal static async Task TryRestoreSelectorAsync(PtpSession session, byte[] selector)
    {
        try
        {
            await session.SetPropertyAsync(BankSelector, selector, CancellationToken.None);
        }
        catch (Exception e) when (e is PtpException or IOException)
        {
            // The camera is gone; the caller already has the failure.
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
                    // Not cancellable: an abandoned PTP transfer can desync the camera.
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
                    problems.Add(Loc.F("{0}: read back {1}, wrote {2}", write.Description, Convert.ToHexString(actual), Convert.ToHexString(write.Value)));
                progress?.Report(new FujiWriteProgress(FujiWritePhase.Verifying, i + 1, total, write.Description));
            }
        }
        catch (OperationCanceledException)
        {
            problems.Add(Loc.T("Cancelled by the user."));
            var cancelled = await RollBackAsync(session, profile, slot, encoding, backup, planned, touched, problems);
            return cancelled with { Cancelled = true };
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
        {
            // Cable pulled or camera off: try to roll back.
            problems.Add(Loc.F("Connection lost: {0}", e.Message));
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
        // Same dependency order as writing.
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
                    failures.Add(Loc.F("0x{0:X4}: restored value reads {1}, expected {2}", prop, Convert.ToHexString(actual), Convert.ToHexString(backup[prop])));
            }
            catch (Exception e) when (e is PtpException or IOException or TimeoutException or InvalidOperationException)
            {
                failures.Add($"0x{prop:X4}: {e.Message}");
            }
        }

        problems.AddRange(failures.Select(f => Loc.F("rollback: {0}", f)));
        return new FujiWriteReport(profile.Model, slot, false, false, planned, encoding.Skipped, backup, problems, failures.Count == 0);
    }

    /// <summary>Restore order: name and film simulation first, Kelvin before the shifts, D-Range Priority last.</summary>
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

    /// <summary>Grain 6/7 ("Off, size remembered") is refused on write; writing the size, then Off, reproduces it.</summary>
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
