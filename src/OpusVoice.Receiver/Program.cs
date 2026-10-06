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
using QRCoder;
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
    string lanIp = BestLocalIpv4();
    Console.WriteLine($"listening: udp://0.0.0.0:{port}  (point the app at this machine's IP, port {port})");
    Console.WriteLine($"writing:   {basePath}.opus (+ .adpcm sidecar, .txt summary)");
    PrintQr($"udp://{lanIp}:{port}", $"scan in the app (UDP mode): udp://{lanIp}:{port}");
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
    Console.WriteLine("…or scan this QR code from the app (Pinhole mode):");
    PrintQr(node.ConnectionString, caption: null);
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

/// <summary>The machine's best-guess LAN IPv4: what a phone on the same network would dial.</summary>
static string BestLocalIpv4()
{
    try
    {
        using var probe = new UdpClient();
        probe.Client.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53)); // no packet leaves; routing picks the NIC
        return (probe.Client.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
    }
    catch
    {
        return "127.0.0.1";
    }
}

/// <summary>Terminal QR via QRCoder's half-block ASCII renderer; ECC L keeps the
/// module count (and the on-screen code) small for long connection strings.</summary>
static void PrintQr(string payload, string? caption)
{
    try
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.L);
        string ascii = new AsciiQRCode(data).GetGraphic(1);
        Console.WriteLine();
        foreach (string line in ascii.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Console.WriteLine("  " + line);
        }
        if (caption != null)
        {
            Console.WriteLine("  " + caption);
        }
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"(QR rendering failed: {ex.Message})");
    }
}
