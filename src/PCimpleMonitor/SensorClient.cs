using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using PCimple.Monitoring;

namespace PCimple;

internal static class SensorClient
{
    public static async Task<Snapshot> ReadAsync(CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var pipe = new NamedPipeClientStream(".", Snapshot.PipeName, PipeDirection.In,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        byte[] header = new byte[4];
        await pipe.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > Snapshot.MaxBytes)
            throw new InvalidDataException("Invalid sensor snapshot length.");

        byte[] payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
        var snapshot = JsonSerializer.Deserialize<Snapshot>(payload);
        if (snapshot?.Version != Snapshot.ProtocolVersion)
            throw new InvalidDataException("The sensor service protocol is incompatible.");
        return snapshot;
    }
}
