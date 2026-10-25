using System.Text;
using PhSpectre.Camera;
using PhSpectre.Models;
using Xunit;

namespace PhSpecre.Tests;

public class FujiRecipeWriterTests
{
    /// <summary>In-memory Fuji body: bank selector, per-bank properties and the refusal rules the writer must respect.</summary>
    private sealed class FakeFuji : IPtpTransport
    {
        private readonly string _model;
        private readonly int _slotCount;
        public ushort UsbMode { get; init; } = 6;
        public Dictionary<int, Dictionary<ushort, byte[]>> Banks { get; } = new();
        public int Selector { get; set; } = 4;
        public HashSet<ushort> RefuseWrites { get; } = [];
        public HashSet<ushort> IgnoreWrites { get; } = [];
        public List<(int Slot, ushort Property)> SlotWrites { get; } = [];

        public FakeFuji(string model, int slotCount)
        {
            _model = model;
            _slotCount = slotCount;
            for (var i = 1; i <= slotCount; i++) Banks[i] = EmptyBank();
        }

        public static Dictionary<ushort, byte[]> EmptyBank()
        {
            var bank = new Dictionary<ushort, byte[]>();
            for (ushort p = 0xD18D; p <= 0xD1A4; p++) bank[p] = [0, 0];
            bank[FujiSlotCodec.PropName] = [0x01, 0x00, 0x00];
            bank[FujiSlotCodec.PropDynamicRange] = [100, 0];
            bank[FujiSlotCodec.PropFilmSimulation] = [1, 0];
            bank[FujiSlotCodec.PropGrain] = [6, 0]; // Off, Small remembered
            bank[FujiSlotCodec.PropColorChrome] = [1, 0];
            bank[FujiSlotCodec.PropColorChromeBlue] = [1, 0];
            bank[FujiSlotCodec.PropSmoothSkin] = [1, 0];
            bank[FujiSlotCodec.PropWhiteBalance] = [2, 0];
            bank[FujiSlotCodec.PropNoiseReduction] = [0x00, 0x20];
            return bank;
        }

        public Task<PtpResponse> ExecuteAsync(ushort operation, uint[] parameters, byte[]? dataOut,
            bool expectDataIn = false, CancellationToken ct = default)
        {
            switch (operation)
            {
                case PtpSession.OperationOpenSession or PtpSession.OperationCloseSession:
                    return Ok();
                case PtpSession.OperationGetDeviceInfo:
                    return Ok(DeviceInfo(_model));
                case PtpSession.OperationGetDevicePropValue:
                    return Ok(Read((ushort)parameters[0]));
                case PtpSession.OperationSetDevicePropValue:
                    return Task.FromResult(new PtpResponse(Write((ushort)parameters[0], dataOut!), [], null));
            }
            return Task.FromResult(new PtpResponse(0x2005, [], null));
        }

        private byte[] Read(ushort prop)
        {
            if (prop == 0xD16E) return [(byte)UsbMode, 0];
            if (prop == 0xD18C) return [(byte)Selector, 0];
            return (byte[])Banks[Selector][prop].Clone();
        }

        private ushort Write(ushort prop, byte[] value)
        {
            if (prop == 0xD18C)
            {
                if (value[0] < 1 || value[0] > _slotCount) return 0x201C;
                Selector = value[0];
                return PtpSession.ResponseOk;
            }
            // The selector is the only write that is not a slot write.
            SlotWrites.Add((Selector, prop));
            if (RefuseWrites.Contains(prop)) return 0x201C;
            var bank = Banks[Selector];
            if (prop == FujiSlotCodec.PropGrain)
            {
                if (value[0] is 0 or >= 6) return 0x201C;
                // Off keeps whichever size was last set.
                var size = bank[prop][0] switch { 2 or 3 or 6 => 6, 4 or 5 or 7 => 7, _ => 6 };
                if (value[0] != 1 && !IgnoreWrites.Contains(prop)) bank[prop] = value;
                else if (value[0] == 1 && !IgnoreWrites.Contains(prop)) bank[prop] = [(byte)size, 0];
                return PtpSession.ResponseOk;
            }
            if (prop == FujiSlotCodec.PropColor && FujiSlotCodec.MonochromeFamily.Contains(bank[FujiSlotCodec.PropFilmSimulation][0]))
                return 0x201C;
            if (!IgnoreWrites.Contains(prop)) bank[prop] = value;
            return PtpSession.ResponseOk;
        }

        private static Task<PtpResponse> Ok(byte[]? data = null) =>
            Task.FromResult(new PtpResponse(PtpSession.ResponseOk, [], data));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static byte[] DeviceInfo(string model)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.Unicode);
        w.Write((ushort)100); w.Write(6u); w.Write((ushort)100);
        Str(w, "fujifilm.co.jp: 1.0; ");
        w.Write((ushort)0);
        for (var i = 0; i < 5; i++) w.Write(0u); // operations, events, properties, capture formats, image formats
        Str(w, "FUJIFILM"); Str(w, model); Str(w, "1.0"); Str(w, "SERIAL");
        return ms.ToArray();
    }

    private static void Str(BinaryWriter w, string s)
    {
        w.Write((byte)(s.Length + 1));
        foreach (var c in s) w.Write((ushort)c);
        w.Write((ushort)0);
    }

    private static FilmRecipe Recipe(string? name = "Test Recipe", string film = "Classic Chrome", string grain = "Strong",
        string? grainSize = "Large") =>
        new(RecipeSource.Manual, name, "FUJIFILM", "X-T5", film, "Auto", (2, -4), "DR200", -0.5m, 1.5m, 3, -1, -2, 2m,
            grain, "Strong", "Weak", null, grainSize, "Weak", null);

    private static async Task<FujiWriteReport> Run(FakeFuji camera, int slot, FilmRecipe recipe, FujiWriteOptions options)
    {
        await using var session = new PtpSession(camera);
        await session.OpenAsync();
        return await FujiRecipeWriter.WriteAsync(session, slot, recipe, options);
    }

    private static readonly FujiWriteOptions Apply = new() { DryRun = false };

    [Fact]
    public async Task Write_RoundTripsThroughDecode_AndRestoresSelector()
    {
        var camera = new FakeFuji("X-T5", 7);
        var recipe = Recipe();

        var report = await Run(camera, 7, recipe, Apply);

        Assert.True(report.Success);
        Assert.Equal(4, camera.Selector);
        var decoded = FujiSlotCodec.Decode(camera.Banks[7], "X-T5");
        Assert.Equal("Test Recipe", decoded.Recipe.RecipeName);
        Assert.Equal(recipe.FilmSimulation, decoded.Recipe.FilmSimulation);
        Assert.Equal(recipe.DynamicRange, decoded.Recipe.DynamicRange);
        Assert.Equal(recipe.HighlightTone, decoded.Recipe.HighlightTone);
        Assert.Equal(recipe.ShadowTone, decoded.Recipe.ShadowTone);
        Assert.Equal(recipe.Color, decoded.Recipe.Color);
        Assert.Equal(recipe.NoiseReduction, decoded.Recipe.NoiseReduction);
        Assert.Equal(recipe.Clarity, decoded.Recipe.Clarity);
        Assert.Equal(recipe.WhiteBalanceShift, decoded.Recipe.WhiteBalanceShift);
        Assert.Equal("Strong", decoded.Recipe.GrainEffect);
        Assert.Equal("Large", decoded.Recipe.GrainSize);
        Assert.Equal("Weak", decoded.Recipe.SmoothSkinEffect);
    }

    [Fact]
    public async Task Write_OnlyTouchesTargetSlot()
    {
        var camera = new FakeFuji("X-T5", 7);
        camera.Banks[3][FujiSlotCodec.PropSharpness] = [0xEC, 0xFF];
        var before = camera.Banks[3].ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());

        await Run(camera, 7, Recipe(), Apply);

        Assert.All(camera.SlotWrites, w => Assert.Equal(7, w.Slot));
        Assert.All(before, kv => Assert.Equal(kv.Value, camera.Banks[3][kv.Key]));
    }

    [Fact]
    public async Task DryRun_WritesNothingToTheSlot()
    {
        var camera = new FakeFuji("X-T5", 7);

        var report = await Run(camera, 7, Recipe(), new FujiWriteOptions());

        Assert.True(report.DryRun);
        Assert.Empty(camera.SlotWrites);
        Assert.NotEmpty(report.Planned);
        Assert.Equal(4, camera.Selector);
    }

    [Fact]
    public async Task NamedSlot_IsNotOverwrittenUnlessAllowed()
    {
        var camera = new FakeFuji("X-T5", 7);
        camera.Banks[2][FujiSlotCodec.PropName] = FujiSlotEncoder.EncodeString("Mine");

        await Assert.ThrowsAsync<FujiWriteException>(() => Run(camera, 2, Recipe(), Apply));
        Assert.Empty(camera.SlotWrites);
        Assert.Equal(4, camera.Selector);

        var report = await Run(camera, 2, Recipe(), Apply with { AllowOverwrite = true });
        Assert.True(report.Success);
    }

    [Fact]
    public async Task Refusal_RollsBackEverythingIncludingRememberedGrainSize()
    {
        var camera = new FakeFuji("X-T5", 7);
        var original = camera.Banks[7].ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        camera.RefuseWrites.Add(FujiSlotCodec.PropSharpness);

        var report = await Run(camera, 7, Recipe(), Apply);

        Assert.False(report.Success);
        Assert.True(report.RolledBack);
        Assert.Contains(report.Problems, p => p.Contains("sharpness"));
        Assert.All(original, kv => Assert.Equal(kv.Value, camera.Banks[7][kv.Key]));
        Assert.Equal(4, camera.Selector);
    }

    [Fact]
    public async Task SilentlyIgnoredWrite_IsCaughtByReadBackAndRolledBack()
    {
        var camera = new FakeFuji("X-T5", 7);
        var original = camera.Banks[7].ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        camera.IgnoreWrites.Add(FujiSlotCodec.PropShadowTone);

        var report = await Run(camera, 7, Recipe(), Apply);

        Assert.False(report.Success);
        Assert.Contains(report.Problems, p => p.Contains("read back"));
        Assert.All(original, kv => Assert.Equal(kv.Value, camera.Banks[7][kv.Key]));
    }

    [Fact]
    public async Task UnverifiedBody_AllowsDryRunButNotWrite()
    {
        var camera = new FakeFuji("X-H2S", 7);

        var dry = await Run(camera, 1, Recipe(), new FujiWriteOptions());
        Assert.True(dry.Success);

        await Assert.ThrowsAsync<FujiWriteException>(() => Run(camera, 1, Recipe(), Apply));
        Assert.Empty(camera.SlotWrites);
    }

    [Fact]
    public async Task UnknownBody_IsRefused()
    {
        var camera = new FakeFuji("X-Z99", 7);
        await Assert.ThrowsAsync<FujiWriteException>(() => Run(camera, 1, Recipe(), new FujiWriteOptions()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public async Task SlotOutsideBodyRange_IsRefused(int slot)
    {
        var camera = new FakeFuji("X-T5", 7);
        await Assert.ThrowsAsync<FujiWriteException>(() => Run(camera, slot, Recipe(), Apply));
    }

    [Fact]
    public async Task WrongUsbMode_IsRefused()
    {
        var camera = new FakeFuji("X-T5", 7) { UsbMode = 5 };
        await Assert.ThrowsAsync<FujiWriteException>(() => Run(camera, 7, Recipe(), Apply));
    }

    [Fact]
    public void Encode_SkipsWhatItCannotRepresent_InsteadOfApproximating()
    {
        var recipe = Recipe(film: "Not A Sim") with
        {
            HighlightTone = 0.3m,
            WhiteBalanceShift = (12, 0),
            NoiseReduction = 9,
            ColorTemperature = 5000, // WB is Auto, so Kelvin must not be written
            BwAdjustment = 3,
        };

        var encoding = FujiSlotEncoder.Encode(recipe);

        Assert.Equal(6, encoding.Skipped.Count);
        Assert.DoesNotContain(encoding.Writes, w => w.Property == FujiSlotCodec.PropFilmSimulation);
        Assert.DoesNotContain(encoding.Writes, w => w.Property == FujiSlotCodec.PropHighlightTone);
        Assert.DoesNotContain(encoding.Writes, w => w.Property == FujiSlotCodec.PropColorTemperature);
    }

    [Fact]
    public void Encode_MonochromeSkipsColor_AndOrdersDependenciesCorrectly()
    {
        var recipe = Recipe(film: "Acros") with { WhiteBalance = "Kelvin", ColorTemperature = 5300 };

        var order = FujiSlotEncoder.Encode(recipe).Writes.Select(w => w.Property).ToList();

        Assert.DoesNotContain(FujiSlotCodec.PropColor, order);
        Assert.True(order.IndexOf(FujiSlotCodec.PropDRangePriority) < order.IndexOf(FujiSlotCodec.PropDynamicRange));
        Assert.True(order.IndexOf(FujiSlotCodec.PropWhiteBalance) < order.IndexOf(FujiSlotCodec.PropColorTemperature));
        Assert.True(order.IndexOf(FujiSlotCodec.PropColorTemperature) < order.IndexOf(FujiSlotCodec.PropWbShiftRed));
    }

    [Fact]
    public void Encode_NullFieldsAreLeftAlone()
    {
        var recipe = new FilmRecipe(RecipeSource.Manual, null, "FUJIFILM", "X-T5", "Velvia", null, null, null, null, null,
            null, null, null, null, null, null, null, null);

        var encoding = FujiSlotEncoder.Encode(recipe);

        Assert.Single(encoding.Writes);
        Assert.Equal(FujiSlotCodec.PropFilmSimulation, encoding.Writes[0].Property);
    }

    private sealed class SyncProgress(Action<FujiWriteProgress> onReport) : IProgress<FujiWriteProgress>
    {
        public void Report(FujiWriteProgress value) => onReport(value);
    }

    [Fact]
    public async Task Progress_ReportsEveryWriteAndEveryReadBack()
    {
        var camera = new FakeFuji("X-T5", 7);
        var seen = new List<FujiWriteProgress>();

        var report = await Run(camera, 7, Recipe(), Apply with { Progress = new SyncProgress(seen.Add) });

        Assert.True(report.Success);
        var total = report.Planned.Count;
        Assert.Equal(total, seen.Count(p => p.Phase == FujiWritePhase.Writing));
        Assert.Equal(total, seen.Count(p => p.Phase == FujiWritePhase.Verifying));
        Assert.All(seen, p => Assert.Equal(total, p.Total));
    }

    [Fact]
    public async Task Cancel_MidWrite_RollsTheSlotBack()
    {
        var camera = new FakeFuji("X-T5", 7);
        var original = camera.Banks[7].ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Index == 3) cts.Cancel(); });

        await using var session = new PtpSession(camera);
        await session.OpenAsync();
        var report = await FujiRecipeWriter.WriteAsync(session, 7, Recipe(), Apply with { Progress = progress }, cts.Token);

        Assert.True(report.Cancelled);
        Assert.False(report.Success);
        Assert.True(report.RolledBack);
        Assert.All(original, kv => Assert.Equal(kv.Value, camera.Banks[7][kv.Key]));
        Assert.Equal(4, camera.Selector);
    }

    [Fact]
    public async Task Restore_PutsTheSlotBackToABackup()
    {
        var camera = new FakeFuji("X-T5", 7);
        camera.Banks[7][FujiSlotCodec.PropName] = FujiSlotEncoder.EncodeString("Keep me");
        camera.Banks[7][FujiSlotCodec.PropGrain] = [7, 0];
        var original = camera.Banks[7].ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());

        var written = await Run(camera, 7, Recipe(), Apply with { AllowOverwrite = true });
        Assert.True(written.Success);
        Assert.NotEqual("Keep me", FujiSlotCodec.DecodeName(camera.Banks[7]));

        await using var session = new PtpSession(camera);
        await session.OpenAsync();
        var restored = await FujiRecipeWriter.RestoreAsync(session, 7, written.Backup);

        Assert.True(restored.Success);
        Assert.All(original, kv => Assert.Equal(kv.Value, camera.Banks[7][kv.Key]));
        Assert.Equal(4, camera.Selector);
    }

    [Fact]
    public async Task SlotBackup_RoundTripsThroughJson()
    {
        var camera = new FakeFuji("X-T5", 7);
        var report = await Run(camera, 7, Recipe(), new FujiWriteOptions());

        var backup = FujiSlotBackup.FromJson(FujiSlotBackup.From(report).ToJson());

        Assert.Equal("X-T5", backup.Model);
        Assert.Equal(7, backup.Slot);
        Assert.Equal(report.Backup.Count, backup.Properties.Count);
        Assert.All(report.Backup, kv => Assert.Equal(kv.Value, backup.Properties[kv.Key]));
    }

    [Fact]
    public async Task Reader_ListsEverySlotAndRestoresSelector()
    {
        var camera = new FakeFuji("X-H2S", 7);
        camera.Banks[2][FujiSlotCodec.PropName] = FujiSlotEncoder.EncodeString("Street 400");

        await using var session = new PtpSession(camera);
        await session.OpenAsync();
        var snapshot = await FujiSlotReader.ReadAllAsync(session);

        Assert.Equal(7, snapshot.Slots.Count);
        Assert.False(snapshot.CanWrite); // X-H2S is not verified yet
        Assert.Equal("Street 400", snapshot.Slots[1].Name);
        Assert.True(snapshot.Slots[0].IsEmpty);
        Assert.Equal(4, camera.Selector);
    }

    [Fact]
    public async Task Reader_WrongUsbMode_ReturnsNoSlots()
    {
        var camera = new FakeFuji("X-T5", 7) { UsbMode = 5 };
        await using var session = new PtpSession(camera);
        await session.OpenAsync();

        var snapshot = await FujiSlotReader.ReadAllAsync(session);

        Assert.False(snapshot.RawConvMode);
        Assert.Empty(snapshot.Slots);
        Assert.False(snapshot.CanWrite);
    }

    [Fact]
    public void Diff_ShowsOnlyWhatTheWriteChanges()
    {
        var slot = FakeFuji.EmptyBank();
        var recipe = Recipe() with { BwAdjustment = 3 }; // skipped by the encoder, so it must not show up

        var diff = RecipeDiff.Compute(slot, FujiSlotEncoder.Encode(recipe), "X-T5");

        Assert.Contains(diff.Changes, c => c.Field == "Film Simulation" && c.Was == "Provia/Standard" && c.Will == "Classic Chrome");
        Assert.Contains(diff.Changes, c => c.Field == "WB Shift" && c.Will == "R+2 B-4");
        Assert.DoesNotContain(diff.Changes, c => c.Field == "B&W Adjustment");
        Assert.DoesNotContain(diff.Changes, c => c.Field == "White Balance"); // Auto -> Auto
    }

    [Theory]
    [InlineData("X-T30 II", "XT30II")]
    [InlineData("X-T30 III", "x-t30 iii")]
    [InlineData("X100VI", "X100VI")]
    public void Profiles_AreFoundIgnoringCaseSpacesAndHyphens(string a, string b)
    {
        Assert.NotNull(FujiCameraProfiles.Find(a));
        Assert.Same(FujiCameraProfiles.Find(a), FujiCameraProfiles.Find(b));
    }

    [Fact]
    public void Profiles_OnlyCheckedBodiesAreWriteVerified_AndOlderBodiesAreBackupOnly()
    {
        Assert.Equal(["X-T5", "X-M5"], FujiCameraProfiles.All.Where(p => p.WriteVerified).Select(p => p.Model));
        Assert.NotSame(FujiCameraProfiles.Find("X-T30"), FujiCameraProfiles.Find("X-T30 II"));
        Assert.Equal(FujiBankAccess.BackupOnly, FujiCameraProfiles.Find("X-T3")!.Access);
        Assert.Equal(FujiBankAccess.Direct, FujiCameraProfiles.Find("X-H2S")!.Access);
    }

    [Fact]
    public void Encode_ForABodyWithoutSmoothSkin_SkipsInsteadOfWriting()
    {
        var profile = FujiCameraProfiles.Find("X-S20")!;

        var encoding = FujiSlotEncoder.Encode(Recipe(), profile);

        Assert.DoesNotContain(encoding.Writes, w => w.Property == FujiSlotCodec.PropSmoothSkin);
        Assert.Contains(encoding.Skipped, s => s.Contains("smooth skin"));
    }

    [Fact]
    public async Task BackupOnlyBody_IsNeverReadOrWrittenDirectly()
    {
        var camera = new FakeFuji("X-T3", 7);

        await Assert.ThrowsAsync<FujiWriteException>(() => Run(camera, 1, Recipe(), new FujiWriteOptions()));

        await using var session = new PtpSession(camera);
        await session.OpenAsync();
        var snapshot = await FujiSlotReader.ReadAllAsync(session);
        Assert.False(snapshot.DirectAccess);
        Assert.Empty(snapshot.Slots);
        Assert.Empty(camera.SlotWrites);
    }

    [Fact]
    public async Task CameraReport_HidesSlotNames()
    {
        var camera = new FakeFuji("X-M5", 4);
        camera.Banks[1][FujiSlotCodec.PropName] = FujiSlotEncoder.EncodeString("Private Name");
        await using var session = new PtpSession(camera);
        await session.OpenAsync();

        var report = FujiCameraReport.Build(await FujiSlotReader.ReadAllAsync(session), "test");

        Assert.Contains("X-M5", report);
        Assert.Contains("0xD192", report);
        Assert.DoesNotContain("Private Name", report);
        Assert.DoesNotContain(Convert.ToHexString(FujiSlotEncoder.EncodeString("Private Name")), report);
    }

    [Fact]
    public void EncodeString_MatchesPtpStringLayout()
    {
        Assert.Equal(new byte[] { 3, (byte)'A', 0, (byte)'B', 0, 0, 0 }, FujiSlotEncoder.EncodeString("AB"));
    }
}
