using System.Buffers.Binary;

namespace PhSpectre.Camera;

/// <summary>Frames operations as PTP containers over an <see cref="ICameraTransport"/> and tracks transaction ids.</summary>
public sealed class ContainerPtpTransport : IPtpTransport
{
    private readonly ICameraTransport _transport;
    private uint _transactionId;

    public ContainerPtpTransport(ICameraTransport transport)
    {
        _transport = transport;
    }

    public async Task<PtpResponse> ExecuteAsync(ushort operation, uint[] parameters, byte[]? dataOut, bool expectDataIn = false, CancellationToken ct = default)
    {
        // OpenSession is the only operation sent with transaction id 0 (PTP 1.1, 9.3.1); the counter restarts after it.
        uint transactionId;
        if (operation == PtpSession.OperationOpenSession)
            transactionId = _transactionId = 0;
        else
            transactionId = ++_transactionId;

        var payload = new byte[parameters.Length * 4];
        for (var i = 0; i < parameters.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(i * 4), parameters[i]);
        await _transport.SendAsync(new PtpContainer(PtpContainerType.Command, operation, transactionId, payload).Encode(), ct);

        if (dataOut != null)
            await _transport.SendAsync(new PtpContainer(PtpContainerType.Data, operation, transactionId, dataOut).Encode(), ct);

        byte[]? data = null;
        while (true)
        {
            var container = PtpContainer.Decode(await _transport.ReceiveAsync(ct));
            if (container.Type == PtpContainerType.Event)
                continue;

            if (container.TransactionId != transactionId)
                throw new PtpException(
                    $"Transaction id mismatch for operation 0x{operation:X4}: expected {transactionId}, got {container.TransactionId}.");

            switch (container.Type)
            {
                case PtpContainerType.Data:
                    data = container.Payload;
                    break;
                case PtpContainerType.Response:
                    var responseParams = new uint[container.Payload.Length / 4];
                    for (var i = 0; i < responseParams.Length; i++)
                        responseParams[i] = BinaryPrimitives.ReadUInt32LittleEndian(container.Payload.AsSpan(i * 4));
                    return new PtpResponse(container.Code, responseParams, data);
                default:
                    throw new PtpException($"Unexpected {container.Type} container while waiting for operation 0x{operation:X4}.");
            }
        }
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
