// OpusVoice.Receiver — test sink for the OpusVoice Android app.
// Receives the app's RTP stream and writes it to disk for inspection:
// a playable .opus (Ogg Opus) file, a .adpcm sidecar for the app's fallback
// frames, and a .txt session summary.
//
//   dotnet run --project src/OpusVoice.Receiver                    # Pinhole mode (default)
//   dotnet run --project src/OpusVoice.Receiver -- udp 5004        # plain UDP RTP (works with the APK today)
//   --port N / a bare number overrides the UDP port; --out DIR changes the output directory.
using OpusVoice.Receiver;
using Pinhole;
using System.Net;
using System.Net.Sockets;

string mode = args.FirstOrDefault(a => a is "pinhole" or "udp") ?? "pinhole";
int port = 5004;
string outDir = ".";
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length)
    {
        port = int.Parse(args[++i]);
    }
    else if (args[i] == "--out" && i + 1 < args.Length)
    {
        outDir = args[++i];
    }
    else if (int.TryParse(args[i], out int parsed) && parsed is > 0 and < 65536)
    {
        port = parsed;
    }
}

Directory.CreateDirectory(outDir);
string basePath = Path.Combine(outDir, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}");
using var session = new CaptureSession(basePath);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"OpusVoice.Receiver — mode: {mode}");
try
{
    if (mode == "udp")
    {
        await RunUdp(port, basePath, session, cts.Token);
    }
    else
    {
        await RunPinhole(basePath, session, cts.Token);
    }
}
catch (OperationCanceledException)
{
}

Console.WriteLine("capture closed.");

static async Task RunUdp(int port, string basePath, CaptureSession session, CancellationToken ct)
{
    using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
    Console.WriteLine($"listening: udp://0.0.0.0:{port}  (point the app at this machine's IP, port {port})");
    Console.WriteLine($"writing:   {basePath}.opus (+ .adpcm sidecar, .txt summary)");
    while (!ct.IsCancellationRequested)
    {
        UdpReceiveResult result = await udp.ReceiveAsync(ct);
        session.OnDatagram(result.Buffer);
    }
}

static async Task RunPinhole(string basePath, CaptureSession session, CancellationToken ct)
{
    // ReceiveBufferCapacity enables the buffered ReadAllAsync loop; 64 KiB is
    // several seconds of 128 kbps audio, so datagrams never drop off-thread.
    await using PinholeNode node = await PinholeNode.BindAsync(
        new PinholeOptions { ReceiveBufferCapacity = 64 * 1024 }, ct);
    Console.WriteLine("connection string — give this to the sender:");
    Console.WriteLine("  " + node.ConnectionString);
    Console.WriteLine("waiting for a peer to connect…");
    await using PinholeConnection conn = await node.AcceptAsync(ct);
    Console.WriteLine($"connected ({conn.Path.Kind} path, remote {conn.Path.Remote?.ToString() ?? "?"})");
    Console.WriteLine($"writing:   {basePath}.opus (+ .adpcm sidecar, .txt summary)");
    conn.StateChanged += state => Console.WriteLine($"connection state: {state}");
    await foreach (ReadOnlyMemory<byte> datagram in conn.ReadAllAsync(ct))
    {
        session.OnDatagram(datagram.Span);
    }
    Console.WriteLine("peer disconnected");
}
