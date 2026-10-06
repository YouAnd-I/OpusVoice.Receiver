# OpusVoice.Receiver

Test sink for the [OpusVoice](https://github.com/IAFahim/OpusVoice) Android app.
It receives the app's RTP audio stream and writes it to disk so a capture can
be inspected, measured, and played back:

- `capture-*.opus` — a directly playable Ogg/Opus file (VLC, ffplay, …).
- `capture-*.adpcm` — sidecar holding the app's fallback frames (`OP` magic,
  length-prefixed), so they never corrupt the Opus stream.
- `capture-*.txt` — session summary: ssrc, packet/frame counts, losses,
  duplicates, duration, payload bitrate.

Connection modes:

- **`pinhole` (default)** — binds a [Pinhole.Net](https://github.com/IAFahim/Pinhole.Net)
  node, prints its **connection string** (`pinhole1:…`), and accepts one peer.
  Datagrams tunneled through Pinhole are exactly the app's RTP packets, so the
  stream rides NAT traversal + end-to-end encryption with relay fallback
  instead of raw UDP.
- **`iroh`** — the same encrypted Pinhole session, with native iroh discovery and
  relay connectivity. Publishes the signed endpoint ID → Pinhole public-key
  binding and prints an ID and native endpoint QR ticket for the Android app.
  Requires the updated Pinhole.Net and OpusVoice source builds. The default
  identity is fresh on each run; persist `PinholeOptions.IdentityKeySeed` for
  a stable ID.
- **`udp`** — plain UDP RTP listener on a port (default 5004). This is what the
  released APK speaks today; use it for immediate testing.

## Usage

```bash
# Pinhole mode: run this, then paste the printed connection string into the sender
dotnet run --project src/OpusVoice.Receiver

# Native iroh ID/ticket: publish signed discovery, then scan the QR in OpusVoice
dotnet run --project src/OpusVoice.Receiver -- iroh

# Plain UDP mode (works with the current OpusVoice APK: point the app at this PC's IP)
dotnet run --project src/OpusVoice.Receiver -- udp 5004

# Options
#   udp | pinhole | iroh | ws   mode (default pinhole)
#   5004 | --port N    UDP port in udp mode
#   --out DIR          output directory (default: current directory)
```

`Ctrl-C` closes the capture cleanly (EOS page + summary file).

## Requirements

- .NET 10 SDK
- A checkout of [Pinhole.Net](https://github.com/IAFahim/Pinhole.Net) as a
  sibling directory (`../Pinhole.Net`), or point the build at yours:

```bash
dotnet build -p:PinholeRoot=/path/to/Pinhole.Net
```

## Stream contract

RFC 3550 RTP, payload type 111 (Opus, RFC 7587), 48 kHz mono, 20 ms frames.
Ogg granule positions are derived from RTP timestamps (one-packet lookahead),
so packet loss and non-20 ms frames mux correctly.
