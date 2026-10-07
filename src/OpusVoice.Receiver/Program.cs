// OpusVoice.Receiver — test sink for the OpusVoice Android app.
// Receives the app's RTP stream and writes it to disk for inspection:
// a playable .opus (Ogg Opus) file, a .adpcm sidecar for the app's fallback
// frames, and a .txt session summary.
//
//   dotnet run --project src/OpusVoice.Receiver                    # Pinhole mode (default)
////   dotnet run --project src/OpusVoice.Receiver -- udp 5004        # plain UDP RTP (works with the APK today)
//   dotnet run --project src/OpusVoice.Receiver -- ws 8080         # WebSocket bridge for the web console
//   --port N / a bare number overrides the port; --out DIR changes the output directory.
using OpusVoice.Receiver;
using Pinhole;
using QRCoder;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

string mode = args.FirstOrDefault(a => a is "pinhole" or "udp" or "ws") ?? "pinhole";
int port = 5004;
string outDir = ".";
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length)
    {
        if (!int.TryParse(args[++i], out port) || port is < 1 or > 65535)
        {
            Console.WriteLine("error: --port expects a port number between 1 and 65535");
            return;
        }
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
    else if (mode == "ws")
    {
        await RunWs(port, basePath, session, cts.Token);
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

/// <summary>
/// WebSocket bridge for the web console (web/ in the OpusVoice repo): browsers
/// cannot send raw UDP, so the console streams the exact same RTP+Opus packets
/// as binary WebSocket messages. Each message is one datagram — one 20 ms frame
/// in a 12-byte RTP header — and feeds the same sink as the UDP path. Several
/// consoles may connect at once; the capture follows one sender at a time and
/// re-latches to a new SSRC after the previous sender goes silent for a few
/// seconds (a stopped and restarted console mints a new SSRC). IPv4 only (the
/// managed HttpListener cannot bind IPv6 wildcards).
/// </summary>
static async Task RunWs(int port, string basePath, CaptureSession session, CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://+:{port}/");
    try
    {
        listener.Start();
    }
    catch (HttpListenerException ex)
    {
        Console.WriteLine($"error: could not listen on http://+:{port}/ — {ex.Message}");
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("       Windows requires the prefix reserved once (admin) or an elevated run:");
            Console.WriteLine($"       netsh http add urlacl url=http://+:{port}/ user=%USERDOMAIN%\\%USERNAME%");
        }
        return;
    }
    string lanIp = BestLocalIpv4();
    Console.WriteLine($"listening: ws://{lanIp}:{port}/stream  (web console: scan or paste this; IPv4 only)");
    Console.WriteLine($"writing:   {basePath}.opus (+ .adpcm sidecar, .txt summary)");
    PrintQr($"ws://{lanIp}:{port}/stream", $"scan in the web console (Voice mode): ws://{lanIp}:{port}/stream");
    while (!ct.IsCancellationRequested)
    {
        HttpListenerContext http = await listener.GetContextAsync().WaitAsync(ct);
        if (!http.Request.IsWebSocketRequest)
        {
            http.Response.StatusCode = 426; // Upgrade Required
            http.Response.Headers["Sec-WebSocket-Version"] = "13";
            http.Response.Close();
            continue;
        }
        _ = HandleSocket(http, session); // fire-and-forget: several consoles may stream at once
    }
}

static async Task HandleSocket(HttpListenerContext http, CaptureSession session)
{
    string peer = http.Request.RemoteEndPoint?.ToString() ?? "?";
    try
    {
        if (http.Request.Url?.AbsolutePath != "/stream")
        {
            http.Response.StatusCode = 404;
            http.Response.Close();
            return;
        }

        using WebSocket ws = (await http.AcceptWebSocketAsync(null)).WebSocket;
        Console.WriteLine($"web console connected ({peer})");
        var buffer = new byte[64 * 1024];
        bool discarding = false; // a started-but-unfinished message: its tail must never pose as a datagram
        bool warnedOversize = false;
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Complete the close handshake so well-behaved clients exit cleanly.
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                break;
            }
            if (!result.EndOfMessage)
            {
                discarding = true; // fragmented or larger than our buffer: drop the whole message
                continue;
            }
            if (discarding)
            {
                discarding = false;
                if (!warnedOversize)
                {
                    warnedOversize = true;
                    Console.WriteLine($"\nwarning: dropped an oversized or fragmented message from {peer} (captures stay valid)");
                }
                continue;
            }
            if (result.MessageType == WebSocketMessageType.Binary && result.Count >= 12)
            {
                session.OnDatagram(buffer.AsSpan(0, result.Count));
            }
        }
        Console.WriteLine($"web console disconnected ({peer})");
    }
    catch (Exception ex)
    {
        // Broad catch in a fire-and-forget handler: a sink I/O failure or an aborted
        // socket must be logged, never silently swallowed or unobserved.
        Console.WriteLine($"web console error ({peer}): {ex.Message}");
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
