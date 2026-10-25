using System.Buffers.Binary;

namespace PhSpectre.Camera;

public enum PtpContainerType : ushort
{
    Command = 1,
    Data = 2,
    Response = 3,
    Event = 4,
}

/// <summary>One PTP container: 12-byte little-endian header (length, type, code, transaction id) plus payload.</summary>
public sealed record PtpContainer(PtpContainerType Type, ushort Code, uint TransactionId, byte[] Payload)
{
    public const int HeaderSize = 12;

    public byte[] Encode()
    {
        var buffer = new byte[HeaderSize + Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)buffer.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)Type);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), Code);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), TransactionId);
        Payload.CopyTo(buffer, HeaderSize);
        return buffer;
    }

    public static PtpContainer Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new PtpException($"PTP container too short: {data.Length} bytes.");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (length < HeaderSize || length > data.Length)
            throw new PtpException($"PTP container length {length} does not match received {data.Length} bytes.");

        var type = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (type is < 1 or > 4)
            throw new PtpException($"Unknown PTP container type {type}.");

        return new PtpContainer(
            (PtpContainerType)type,
            BinaryPrimitives.ReadUInt16LittleEndian(data[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            data[HeaderSize..(int)length].ToArray());
    }
}
