using System.Buffers.Binary;
using System.Text;
using PhSpectre.Camera;
using Xunit;

namespace PhSpecre.Tests;

public class PtpSessionTests
{
    private sealed class FakeTransport : ICameraTransport
    {
        public List<PtpContainer> Sent { get; } = [];
        public Queue<Func<PtpContainer?, byte[]>> Replies { get; } = new();
        public bool Disposed { get; private set; }

        private PtpContainer? _lastSent;

        public Task SendAsync(byte[] container, CancellationToken ct = default)
        {
            _lastSent = PtpContainer.Decode(container);
            Sent.Add(_lastSent);
            return Task.CompletedTask;
        }

        public Task<byte[]> ReceiveAsync(CancellationToken ct = default) =>
            Task.FromResult(Replies.Dequeue()(_lastSent));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static byte[] Response(uint tid, ushort code = PtpSession.ResponseOk) =>
        new PtpContainer(PtpContainerType.Response, code, tid, []).Encode();

    private static byte[] Data(uint tid, ushort op, byte[] payload) =>
        new PtpContainer(PtpContainerType.Data, op, tid, payload).Encode();

    [Fact]
    public void Container_RoundTrips()
    {
        var original = new PtpContainer(PtpContainerType.Command, 0x1015, 7, [1, 2, 3, 4]);
        var decoded = PtpContainer.Decode(original.Encode());

        Assert.Equal(original.Type, decoded.Type);
        Assert.Equal(original.Code, decoded.Code);
        Assert.Equal(original.TransactionId, decoded.TransactionId);
        Assert.Equal(original.Payload, decoded.Payload);
    }

    [Fact]
    public void Container_Decode_TruncatedThrows()
    {
        Assert.Throws<PtpException>(() => PtpContainer.Decode(new byte[5]));
    }

    [Fact]
    public void Container_Decode_LengthBeyondBufferThrows()
    {
        var bytes = new PtpContainer(PtpContainerType.Data, 1, 1, [1, 2, 3]).Encode();
        Assert.Throws<PtpException>(() => PtpContainer.Decode(bytes.AsSpan(0, bytes.Length - 1)));
    }

    [Fact]
    public async Task Open_UsesTransactionZeroAndSessionIdParameter()
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(_ => Response(0));
        await using var session = new PtpSession(transport);

        await session.OpenAsync(sessionId: 5);

        var command = Assert.Single(transport.Sent);
        Assert.Equal(PtpSession.OperationOpenSession, command.Code);
        Assert.Equal(0u, command.TransactionId);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(command.Payload));
    }

    [Fact]
    public async Task GetProperty_ReturnsDataPhasePayload()
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(_ => Response(0));
        transport.Replies.Enqueue(sent => Data(sent!.TransactionId, sent.Code, [0x34, 0x12]));
        transport.Replies.Enqueue(sent => Response(sent!.TransactionId));
        await using var session = new PtpSession(transport);
        await session.OpenAsync();

        var value = await session.GetPropertyAsync(0xD18C);

        Assert.Equal(new byte[] { 0x34, 0x12 }, value);
        var command = transport.Sent[1];
        Assert.Equal(PtpSession.OperationGetDevicePropValue, command.Code);
        Assert.Equal(1u, command.TransactionId);
        Assert.Equal(0xD18Cu, BinaryPrimitives.ReadUInt32LittleEndian(command.Payload));
    }

    [Fact]
    public async Task SetProperty_SendsCommandThenData()
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(_ => Response(0));
        transport.Replies.Enqueue(sent => Response(sent!.TransactionId));
        await using var session = new PtpSession(transport);
        await session.OpenAsync();

        await session.SetPropertyAsync(0xD18C, [3, 0]);

        Assert.Equal(3, transport.Sent.Count);
        Assert.Equal(PtpContainerType.Command, transport.Sent[1].Type);
        Assert.Equal(PtpContainerType.Data, transport.Sent[2].Type);
        Assert.Equal(transport.Sent[1].TransactionId, transport.Sent[2].TransactionId);
        Assert.Equal(new byte[] { 3, 0 }, transport.Sent[2].Payload);
    }

    [Fact]
    public async Task ErrorResponse_ThrowsWithCode()
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(_ => Response(0));
        transport.Replies.Enqueue(sent => Response(sent!.TransactionId, 0x200A));
        await using var session = new PtpSession(transport);
        await session.OpenAsync();

        var ex = await Assert.ThrowsAsync<PtpException>(() => session.SetPropertyAsync(0xD207, [1]));

        Assert.Equal((ushort)0x200A, ex.ResponseCode);
    }

    [Fact]
    public async Task TransactionIdMismatch_Throws()
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(_ => Response(0));
        transport.Replies.Enqueue(_ => Response(99));
        await using var session = new PtpSession(transport);
        await session.OpenAsync();

        await Assert.ThrowsAsync<PtpException>(() => session.SetPropertyAsync(0xD18C, [1]));
    }

    [Fact]
    public async Task Events_AreSkippedWhileWaitingForResponse()
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(_ => Response(0));
        transport.Replies.Enqueue(_ => new PtpContainer(PtpContainerType.Event, 0x4006, 0, []).Encode());
        transport.Replies.Enqueue(sent => Response(sent!.TransactionId));
        await using var session = new PtpSession(transport);
        await session.OpenAsync();

        await session.SetPropertyAsync(0xD18C, [1]);
    }

    [Fact]
    public async Task Dispose_ClosesSessionAndTransport()
    {
        var transport = new FakeTransport();
        transport.Replies.Enqueue(_ => Response(0));
        transport.Replies.Enqueue(sent => Response(sent!.TransactionId));
        var session = new PtpSession(transport);
        await session.OpenAsync();

        await session.DisposeAsync();

        Assert.Equal(PtpSession.OperationCloseSession, transport.Sent[^1].Code);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public void DeviceInfo_Parse_ReadsModelAndProperties()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.Unicode);
        w.Write((ushort)100); w.Write(0x0Eu); w.Write((ushort)100); // version, ext id, ext version
        WriteString(w, "fujifilm.co.jp: 1.0;");
        w.Write((ushort)0);                                          // functional mode
        WriteArray(w, 0x1001, 0x1002);                               // operations
        WriteArray(w);                                               // events
        WriteArray(w, 0xD18C, 0xD18E);                               // device properties
        WriteArray(w);                                               // capture formats
        WriteArray(w);                                               // image formats
        WriteString(w, "FUJIFILM");
        WriteString(w, "X-T5");
        WriteString(w, "1.10");
        WriteString(w, "ABC123");

        var info = PtpDeviceInfo.Parse(ms.ToArray());

        Assert.Equal("FUJIFILM", info.Manufacturer);
        Assert.Equal("X-T5", info.Model);
        Assert.Equal("1.10", info.DeviceVersion);
        Assert.Equal("ABC123", info.SerialNumber);
        Assert.Equal(new ushort[] { 0x1001, 0x1002 }, info.OperationsSupported);
        Assert.Equal(new ushort[] { 0xD18C, 0xD18E }, info.DevicePropertiesSupported);
    }

    [Fact]
    public void DeviceInfo_Parse_TruncatedThrowsPtpException()
    {
        Assert.Throws<PtpException>(() => PtpDeviceInfo.Parse(new byte[4]));
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        if (s.Length == 0) { w.Write((byte)0); return; }
        w.Write((byte)(s.Length + 1));
        foreach (var c in s) w.Write((ushort)c);
        w.Write((ushort)0);
    }

    private static void WriteArray(BinaryWriter w, params ushort[] values)
    {
        w.Write((uint)values.Length);
        foreach (var v in values) w.Write(v);
    }
}
