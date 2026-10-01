using System.Buffers.Binary;
using System.Text;
using DwgTranslator.Contracts;

namespace DwgTranslator.Transport.Core;

public static class LengthPrefixedFrame
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length is <= 0 or > ContractV1.MaxFrameBytes)
            throw new InvalidDataException("IPC_FRAME_LENGTH_INVALID");

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var prefix = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, prefix, timeoutSource.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > ContractV1.MaxFrameBytes)
            throw new InvalidDataException("IPC_FRAME_LENGTH_INVALID");

        var payload = GC.AllocateUninitializedArray<byte>(length);
        await ReadExactlyAsync(stream, payload, timeoutSource.Token);
        _ = StrictUtf8.GetString(payload);
        return payload;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> target, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < target.Length)
        {
            var read = await stream.ReadAsync(target[offset..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("IPC_FRAME_TRUNCATED");
            offset += read;
        }
    }
}
