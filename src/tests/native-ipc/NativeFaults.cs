using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using PKHeX.GbaBridge;

internal static class NativeFaults
{
    internal static async Task Run(BridgeEndpoint endpoint)
    {
        uint initial = Money(Read(endpoint));
        string proxyPath = Path.Combine(Path.GetDirectoryName(endpoint.SocketPath)!,Guid.NewGuid().ToString("N")[..16] + ".sock");
        try
        {
            SetMoney(endpoint,1000);
            Snapshot snapshot = Read(endpoint);
            var edit = new PendingEdit([],snapshot.Raw,snapshot.Session,snapshot.Epoch);
            var patches = Plan(snapshot,1001);
            SetMoney(endpoint,744);
            bool rejected = false;
            try
            {
                using var direct = new EmulatorClient(endpoint);
                direct.Apply(snapshot,patches,edit);
            }
            catch (BridgeException) { rejected = true; }
            Require(rejected && Money(Read(endpoint)) == 744,"native complete-field conflict preserved 744");

            SetMoney(endpoint,1000);
            snapshot = Read(endpoint);
            edit = new PendingEdit([],snapshot.Raw,snapshot.Session,snapshot.Epoch);
            patches = Plan(snapshot,1001);
            using var listener = new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(proxyPath));
            listener.Listen(1);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task forward = DropReceipt(listener,endpoint.SocketPath!,deadline.Token);
            bool lost = false;
            try
            {
                using var proxied = new EmulatorClient(endpoint with { SocketPath = proxyPath });
                proxied.Apply(snapshot,patches,edit);
            }
            catch (IOException) { lost = true; }
            await forward;
            Require(lost && Money(Read(endpoint)) == 1001,"native write succeeded while its reply was dropped");
            SetMoney(endpoint,1000);
            using var reconnected = new EmulatorClient(endpoint);
            reconnected.Apply(snapshot,patches,edit);
            Require(Money(Read(endpoint)) == 1000,"native reconnect recovered receipt and preserved later spend");
            reconnected.Acknowledge(edit);
        }
        finally
        {
            SetMoney(endpoint,initial);
            File.Delete(proxyPath);
        }
    }

    private static async Task DropReceipt(Socket listener, string endpoint, CancellationToken cancellation)
    {
        using var peer = await listener.AcceptAsync(cancellation);
        using var server = new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);
        await server.ConnectAsync(new UnixDomainSocketEndPoint(endpoint),cancellation);
        using var input = new NetworkStream(peer);
        using var output = new NetworkStream(server);
        while (true)
        {
            byte[] request = await ReadFrame(input,cancellation);
            using var document = JsonDocument.Parse(request.AsMemory(4));
            bool committing = document.RootElement.GetProperty("commands").GetString()!.Contains("\nCOMMIT ",StringComparison.Ordinal);
            await output.WriteAsync(request,cancellation);
            byte[] reply = await ReadFrame(output,cancellation);
            if (committing)
            {
                using var result = JsonDocument.Parse(reply.AsMemory(4));
                string data = result.RootElement.GetProperty("data").GetString()!;
                Require(data.StartsWith("APPLIED ",StringComparison.Ordinal),
                    "native proxy observed committed transaction " + (data.StartsWith("APPLIED ",StringComparison.Ordinal) ? string.Empty : data));
                return;
            }
            await input.WriteAsync(reply,cancellation);
        }
    }

    private static async Task<byte[]> ReadFrame(Stream stream, CancellationToken cancellation)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header,cancellation);
        int size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is <= 0 or > 2000000) throw new IOException("Invalid test frame length");
        byte[] frame = new byte[4 + size];
        header.CopyTo(frame,0);
        await stream.ReadExactlyAsync(frame.AsMemory(4),cancellation);
        return frame;
    }

    private static uint Money(Snapshot snapshot) => SaveCodec.U32(SaveCodec.Canonical(snapshot.Raw).Large,0x490);

    private static List<Patch> Plan(Snapshot snapshot, uint money)
    {
        SaveData plain = SaveCodec.Canonical(snapshot.Raw);
        SaveCodec.Put32(plain.Large,0x490,money);
        return SaveCodec.Plan(snapshot.Raw,plain,snapshot);
    }

    private static Snapshot Read(BridgeEndpoint endpoint)
    {
        using var client = new EmulatorClient(endpoint);
        return client.Snapshot();
    }

    private static void SetMoney(BridgeEndpoint endpoint, uint money)
    {
        using var client = new EmulatorClient(endpoint);
        Snapshot snapshot = client.Snapshot();
        client.Apply(snapshot,Plan(snapshot,money),new PendingEdit([],snapshot.Raw,snapshot.Session,snapshot.Epoch));
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
}
