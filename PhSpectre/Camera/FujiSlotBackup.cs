using System.Text.Json;

namespace PhSpectre.Camera;

/// <summary>A restore point: the raw slot properties of one slot, saved before anything is written to it.</summary>
public sealed record FujiSlotBackup(string Model, int Slot, DateTimeOffset Created, string? SlotName,
    IReadOnlyDictionary<ushort, byte[]> Properties)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private sealed record Dto(string Model, int Slot, DateTimeOffset Created, string? SlotName,
        Dictionary<string, string> Properties);

    public string ToJson() => JsonSerializer.Serialize(new Dto(Model, Slot, Created, SlotName,
        Properties.ToDictionary(kv => $"0x{kv.Key:X4}", kv => Convert.ToHexString(kv.Value))), Options);

    public static FujiSlotBackup FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, Options)
            ?? throw new InvalidDataException("Restore point is empty.");
        var props = dto.Properties.ToDictionary(
            kv => Convert.ToUInt16(kv.Key.Replace("0x", ""), 16), kv => Convert.FromHexString(kv.Value));
        return new FujiSlotBackup(dto.Model, dto.Slot, dto.Created, dto.SlotName, props);
    }

    public static FujiSlotBackup From(FujiWriteReport report) =>
        new(report.Model, report.Slot, DateTimeOffset.Now, FujiSlotCodec.DecodeName(report.Backup), report.Backup);
}
