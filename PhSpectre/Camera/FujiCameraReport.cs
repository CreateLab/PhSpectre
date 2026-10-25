using System.Text;

namespace PhSpectre.Camera;

/// <summary>
/// A text dump of what a body reports (model, firmware, raw slot properties) that a user can send along so an
/// unverified model can be checked and enabled. Slot names are replaced by their length and the serial number is
/// never read, so it carries nothing personal.
/// </summary>
public static class FujiCameraReport
{
    public static string Build(FujiCameraSnapshot snapshot, string appVersion)
    {
        var text = new StringBuilder();
        text.AppendLine($"PhSpectre camera report ({appVersion})");
        text.AppendLine($"Model: {snapshot.Model}");
        text.AppendLine($"Firmware: {snapshot.Firmware}");
        text.AppendLine($"Known profile: {(snapshot.Profile == null ? "no" : $"yes, {snapshot.Profile.SlotCount} slots, access {snapshot.Profile.Access}, write verified {snapshot.Profile.WriteVerified}")}");
        text.AppendLine($"USB RAW CONV. mode: {snapshot.RawConvMode}");
        if (snapshot.ReadError != null) text.AppendLine($"Slot read error: {snapshot.ReadError}");

        foreach (var slot in snapshot.Slots)
        {
            text.AppendLine();
            text.AppendLine($"[C{slot.Slot}] {(slot.IsEmpty ? "empty" : "named")}");
            foreach (var (prop, value) in slot.Raw.OrderBy(kv => kv.Key))
            {
                var hex = prop == FujiSlotCodec.PropName ? $"<name, {value.Length} bytes>" : Convert.ToHexString(value);
                text.AppendLine($"  0x{prop:X4} = {hex}");
            }
            if (slot.Unmapped.Count > 0) text.AppendLine($"  unmapped: {string.Join(", ", slot.Unmapped)}");
        }
        return text.ToString();
    }
}
