using System.Buffers.Binary;
using System.Text;

namespace OpusVoice.Receiver;

/// Captures one RTP session to disk. Opus frames are muxed into a playable
/// .opus (Ogg) file; the app's ADPCM fallback frames ("OP" magic) go to a
/// sidecar file so they never corrupt the Opus stream. Granule positions come
/// from RTP timestamps via one-packet lookahead, so lost packets and varying
/// frame durations mux correctly. Prints live stats; writes a .txt summary.
public sealed class CaptureSession : IDisposable
{
    private readonly object _gate = new();
    private readonly string _basePath;
    private readonly DateTime _started = DateTime.Now;
    private readonly OggOpusWriter _ogg;
    private readonly FileStream _adpcm;

    private uint _ssrc;
    private int _lastSeq = -1;
    private uint _firstTimestamp;
    private bool _sawFirstTimestamp;
    private DateTime _lastAccept = DateTime.UtcNow;
    private byte[]? _pendingPayload; // held back until the next timestamp fixes its granule end
    private long _pendingSpan;

    private long _packets, _opusFrames, _adpcmFrames, _bytes, _lost, _dupes, _foreignSsrc;
    private long _maxTimestampSpan;
    private DateTime _lastReport = DateTime.UtcNow;
    private bool _warnedAdpcm;

    public CaptureSession(string basePath)
    {
        _basePath = basePath;
        _ogg = new OggOpusWriter(basePath + ".opus");
        _adpcm = new FileStream(basePath + ".adpcm", FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    public void OnDatagram(ReadOnlySpan<byte> datagram)
    {
        if (!RtpHeader.TryParse(datagram, out RtpHeader header) || header.PayloadLength <= 0)
        {
            return;
        }

        OnRtp(header, datagram.Slice(header.PayloadOffset, header.PayloadLength));
    }

    private void OnRtp(in RtpHeader header, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (_ssrc == 0)
            {
                _ssrc = header.Ssrc;
                Console.WriteLine($"session: ssrc 0x{_ssrc:X8}, payload type {header.PayloadType}");
            }
            else if (header.Ssrc != _ssrc)
            {
                // One capture, one sender — but a stopped and restarted sender mints a new
                // SSRC, so re-latch once the current sender has been silent for a while.
                if ((DateTime.UtcNow - _lastAccept).TotalSeconds > 3)
                {
                    FlushPending(_maxTimestampSpan + 960);
                    _ssrc = header.Ssrc;
                    _lastSeq = -1;
                    _sawFirstTimestamp = false;
                    _foreignSsrc = 0;
                    Console.WriteLine($"\nprevious sender went silent — capturing new sender ssrc 0x{_ssrc:X8}");
                }
                else
                {
                    _foreignSsrc++;
                    if (_foreignSsrc == 1)
                    {
                        Console.WriteLine($"\nwarning: ignoring packets from ssrc 0x{header.Ssrc:X8} — capture is locked to ssrc 0x{_ssrc:X8} (concurrent sender?)");
                    }
                    return;
                }
            }

            _lastAccept = DateTime.UtcNow;

            if (_lastSeq >= 0)
            {
                if (header.Sequence == _lastSeq)
                {
                    _dupes++;
                }
                else
                {
                    int ahead = (header.Sequence - _lastSeq) & 0xFFFF;
                    if (ahead is > 0 and < 0x8000)
                    {
                        _lost += ahead - 1;
                    }
                }
            }

            _lastSeq = header.Sequence;
            _packets++;
            _bytes += payload.Length;

            if (!_sawFirstTimestamp)
            {
                _firstTimestamp = header.Timestamp;
                _sawFirstTimestamp = true;
            }

            long span = unchecked(header.Timestamp - _firstTimestamp); // 48 kHz samples since stream start
            if (span < 0 || span > int.MaxValue)
            {
                span = _maxTimestampSpan; // clock discontinuity: hold last known position
            }

            _maxTimestampSpan = Math.Max(_maxTimestampSpan, span);

            if (payload.Length >= 2 && payload[0] == (byte)'O' && payload[1] == (byte)'P')
            {
                // OpusVoice fallback frame (IMA-ADPCM, 'OP' magic): sidecar only.
                Span<byte> len = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
                _adpcm.Write(len);
                _adpcm.Write(payload);
                _adpcmFrames++;
                if (!_warnedAdpcm)
                {
                    _warnedAdpcm = true;
                    Console.WriteLine("note: sender is using its ADPCM fallback codec (no MediaCodec Opus encoder); frames go to the .adpcm sidecar");
                }
            }
            else
            {
                FlushPending(span);
                _pendingPayload = payload.ToArray();
                _pendingSpan = span;
                _opusFrames++;
            }

            if ((DateTime.UtcNow - _lastReport).TotalSeconds >= 2)
            {
                Report();
            }
        }
    }

    private void FlushPending(long nextSpan)
    {
        if (_pendingPayload is null)
        {
            return;
        }

        _ogg.WriteOpusPacket(_pendingPayload, nextSpan);
        _pendingPayload = null;
    }

    private void Report()
    {
        double seconds = _maxTimestampSpan / 48_000.0;
        double kbps = seconds > 0 ? _bytes * 8 / 1000.0 / seconds : 0;
        Console.Write($"\r  {seconds,6:0.0}s  {_packets} pkts  {kbps,6:0} kbps  lost {_lost}  dup {_dupes}   ");
        _lastReport = DateTime.UtcNow;
        try
        {
            // Bound the damage of a hard kill: at most the tail since the last report is lost.
            _ogg.Flush();
            _adpcm.Flush();
        }
        catch
        {
            // reporting must never kill the capture
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Report();
            Console.WriteLine();
            FlushPending(_maxTimestampSpan + 960); // last packet: assume one 20 ms frame
            _ogg.Dispose();
            _adpcm.Dispose();

            var meta = new StringBuilder();
            meta.AppendLine($"base         : {Path.GetFileName(_basePath)}");
            meta.AppendLine($"started      : {_started:yyyy-MM-dd HH:mm:ss}");
            meta.AppendLine($"ended        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            meta.AppendLine($"ssrc         : 0x{_ssrc:X8}");
            meta.AppendLine($"packets      : {_packets}");
            meta.AppendLine($"payload bytes: {_bytes}");
            meta.AppendLine($"opus frames  : {_opusFrames}");
            meta.AppendLine($"adpcm frames : {_adpcmFrames}");
            meta.AppendLine($"losses       : {_lost}");
            meta.AppendLine($"duplicates   : {_dupes}");
            meta.AppendLine($"foreign ssrc : {_foreignSsrc}");
            meta.AppendLine($"duration     : {_maxTimestampSpan / 48_000.0:0.00} s (RTP clock)");
            File.WriteAllText(_basePath + ".txt", meta.ToString());
        }
    }
}
