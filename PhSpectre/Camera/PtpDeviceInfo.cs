using System.Buffers.Binary;
using System.Text;

namespace PhSpectre.Camera;

public sealed record PtpDeviceInfo(
    string Manufacturer,
    string Model,
    string DeviceVersion,
    string SerialNumber,
    IReadOnlyList<ushort> OperationsSupported,
    IReadOnlyList<ushort> DevicePropertiesSupported)
{
    /// <summary>Parses the GetDeviceInfo data phase (PTP 1.1, section 5.5.1).</summary>
    public static PtpDeviceInfo Parse(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        try
        {
            pos += 2 + 4 + 2; // StandardVersion, VendorExtensionID, VendorExtensionVersion
            ReadString(data, ref pos);
            pos += 2; // FunctionalMode
            var operations = ReadUInt16Array(data, ref pos);
            ReadUInt16Array(data, ref pos); // EventsSupported
            var properties = ReadUInt16Array(data, ref pos);
            ReadUInt16Array(data, ref pos); // CaptureFormats
            ReadUInt16Array(data, ref pos); // ImageFormats
            var manufacturer = ReadString(data, ref pos);
            var model = ReadString(data, ref pos);
            var version = ReadString(data, ref pos);
            var serial = ReadString(data, ref pos);
            return new PtpDeviceInfo(manufacturer, model, version, serial, operations, properties);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new PtpException("Truncated DeviceInfo dataset.");
        }
    }

    internal static string ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        int chars = data[pos++];
        if (chars == 0) return string.Empty;

        var bytes = data.Slice(pos, chars * 2);
        pos += chars * 2;
        // Length includes the terminating NUL.
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static ushort[] ReadUInt16Array(ReadOnlySpan<byte> data, ref int pos)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
        pos += 4;
        if (count > (uint)(data.Length - pos) / 2)
            throw new PtpException("DeviceInfo array length exceeds dataset.");

        var result = new ushort[count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2));
            pos += 2;
        }
        return result;
    }
}
