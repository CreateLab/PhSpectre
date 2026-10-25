using System.Text.Json;
using PhSpectre.Camera;
using PhSpectre.Models;
using PhSpectre.Usb;
using PhSpectre.Wpd;

// Read-only probe: dumps DeviceInfo, every readable device property and the custom-setting banks of a
// Fujifilm body in USB RAW CONV./BACKUP RESTORE mode, so the dump can become a test fixture and a
// CameraCapabilities profile. The only write is the bank selector (0xD18C), which is restored afterwards;
// it changes which bank the property block addresses, not which bank the camera shoots with.

const ushort ModeProperty = 0xD16E;
const ushort BankSelector = 0xD18C;
const ushort FirstBankProperty = 0xD18D;
const ushort LastBankProperty = 0xD1A4;
const int MaxBanksToTry = 16;

// Usage: CameraProbe [--usb] [output.json]. Default transport is WPD (works with the stock Windows
// driver); --usb uses libusb, which needs WinUSB bound to the camera (e.g. via Zadig).
var useLibUsb = args.Contains("--usb");
// Write mode: CameraProbe write <slot> <recipe.json> [--apply] [--overwrite]. Without --apply it is a dry run
// that only plans the write and saves a backup of the slot next to the recipe file.
var writeMode = args.Length > 0 && args[0] == "write";
var outputPath = writeMode
    ? string.Empty
    : args.FirstOrDefault(a => !a.StartsWith("--")) ?? $"fuji-probe-{DateTime.Now:yyyyMMdd-HHmmss}.json";

IPtpTransport transport;
try
{
    if (useLibUsb)
    {
        Console.WriteLine("Still Image USB devices:");
        foreach (var line in LibUsbTransport.ListStillImageDevices())
            Console.WriteLine($"  {line}");
        var usb = LibUsbTransport.Open();
        Console.WriteLine($"Opened {usb.Description} via libusb");
        transport = new ContainerPtpTransport(usb);
    }
    else
    {
        Console.WriteLine("WPD devices:");
        foreach (var d in WpdTransport.ListDevices())
            Console.WriteLine($"  {d.FriendlyName}  {d.PnpId}");
        var wpd = WpdTransport.Open();
        Console.WriteLine($"Opened {wpd.Info.FriendlyName} via WPD");
        transport = wpd;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"Cannot open camera: {e.Message}");
    Console.Error.WriteLine("Check: camera on, USB mode = USB RAW CONV./BACKUP RESTORE, no other app holding the device.");
    return 1;
}

await using var session = new PtpSession(transport);
var dump = new Dictionary<string, object?>();

try
{
    await session.OpenAsync();
    if (writeMode)
        return await WriteCommand(session, args);

    var info = await session.GetDeviceInfoAsync();
    Console.WriteLine($"{info.Manufacturer} {info.Model} fw {info.DeviceVersion} serial {info.SerialNumber}");
    Console.WriteLine($"{info.OperationsSupported.Count} operations, {info.DevicePropertiesSupported.Count} properties");

    dump["deviceInfo"] = new
    {
        info.Manufacturer,
        info.Model,
        info.DeviceVersion,
        SerialNumber = "redacted",
        Operations = info.OperationsSupported.Select(Hex).ToArray(),
        Properties = info.DevicePropertiesSupported.Select(Hex).ToArray(),
    };

    var usbMode = await TryReadAsync(session, ModeProperty);
    Console.WriteLine($"USB mode property 0x{ModeProperty:X4}: {usbMode ?? "refused"} (6 = RAW CONV./BACKUP RESTORE)");

    var properties = new SortedDictionary<string, string?>();
    foreach (var code in info.DevicePropertiesSupported)
        properties[Hex(code)] = await TryReadAsync(session, code);
    dump["properties"] = properties;

    var originalSelector = await session.GetPropertyAsync(BankSelector);
    var banks = new SortedDictionary<string, SortedDictionary<string, string?>>();
    try
    {
        for (ushort bank = 1; bank <= MaxBanksToTry; bank++)
        {
            try
            {
                await session.SetPropertyAsync(BankSelector, [(byte)bank, 0]);
            }
            catch (PtpException)
            {
                break; // values above the body's bank count are refused
            }

            var values = new SortedDictionary<string, string?>();
            for (var code = FirstBankProperty; code <= LastBankProperty; code++)
                values[Hex(code)] = await TryReadAsync(session, code);
            banks[$"C{bank}"] = values;
            Console.WriteLine($"C{bank}: {values.Count(v => v.Value != null)}/{values.Count} properties readable");
        }
    }
    finally
    {
        await session.SetPropertyAsync(BankSelector, originalSelector);
    }

    dump["originalBankSelector"] = Convert.ToHexString(originalSelector);
    dump["bankCount"] = banks.Count;
    dump["banks"] = banks;
    Console.WriteLine($"Bank count: {banks.Count}");
}
catch (PtpException e)
{
    Console.Error.WriteLine($"PTP error: {e.Message}");
    return 2;
}
finally
{
    if (dump.Count > 0)
    {
        File.WriteAllText(outputPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Dump written to {Path.GetFullPath(outputPath)}");
    }
}

return 0;

static async Task<int> WriteCommand(PtpSession session, string[] args)
{
    if (args.Length < 3 || !int.TryParse(args[1], out var slot))
    {
        Console.Error.WriteLine("Usage: CameraProbe write <slot> <recipe.json> [--apply] [--overwrite]");
        return 3;
    }

    var recipePath = args[2];
    var recipe = JsonSerializer.Deserialize<FilmRecipe>(File.ReadAllText(recipePath))
        ?? throw new InvalidDataException("Recipe JSON is empty.");
    var apply = args.Contains("--apply");

    try
    {
        // Always plan first: it reads the slot, so the backup file exists before anything is sent.
        var plan = await FujiRecipeWriter.WriteAsync(session, slot, recipe,
            new FujiWriteOptions { DryRun = true });
        var backupPath = Path.ChangeExtension(recipePath, $".slot{slot}-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(backupPath, JsonSerializer.Serialize(
            plan.Backup.ToDictionary(kv => $"0x{kv.Key:X4}", kv => Convert.ToHexString(kv.Value)),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{plan.Model} slot {slot}: backup saved to {backupPath}");
        Console.WriteLine($"Existing slot: {FujiSlotCodec.DecodeName(plan.Backup) ?? "(unnamed)"}");
        Console.WriteLine("Planned writes:");
        foreach (var line in plan.Planned) Console.WriteLine($"  {line}");
        foreach (var line in plan.Skipped) Console.WriteLine($"  SKIPPED: {line}");

        if (!apply)
        {
            Console.WriteLine("Dry run only. Re-run with --apply to write.");
            return 0;
        }

        var report = await FujiRecipeWriter.WriteAsync(session, slot, recipe,
            new FujiWriteOptions { DryRun = false, AllowOverwrite = args.Contains("--overwrite") });
        if (report.Success)
        {
            Console.WriteLine("Written and verified by reading every property back.");
            return 0;
        }

        Console.Error.WriteLine(report.RolledBack
            ? "Write failed; the slot was restored from backup:"
            : "Write failed AND the rollback was incomplete. Restore from the backup file:");
        foreach (var problem in report.Problems) Console.Error.WriteLine($"  {problem}");
        return 4;
    }
    catch (FujiWriteException e)
    {
        Console.Error.WriteLine($"Refused: {e.Message}");
        return 5;
    }
}

static string Hex(ushort code) => $"0x{code:X4}";

static async Task<string?> TryReadAsync(PtpSession session, ushort code)
{
    try
    {
        return Convert.ToHexString(await session.GetPropertyAsync(code));
    }
    catch (PtpException)
    {
        return null;
    }
}
