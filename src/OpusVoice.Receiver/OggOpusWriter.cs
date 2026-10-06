using System.Buffers.Binary;
using System.Text;

namespace OpusVoice.Receiver;

/// Streams Opus packets into a playable Ogg/Opus file (RFC 7845). Granule
/// positions are supplied by the caller — the capture session derives them
/// from RTP timestamps so any frame duration muxes correctly.
public sealed class OggOpusWriter : IDisposable
{
    private const int Preskip = 312; // RFC 7845 recommended default
    private const int InputSampleRate = 48_000;
    private const int MaxSegmentsPerPage = 255;
    private const int MaxBodyBytesPerPage = 60_000;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private readonly FileStream _file;
    private readonly uint _serial = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
    private uint _pageSequence;
    private long _granuleEnd;
    private readonly List<byte> _lacing = [];
    private readonly MemoryStream _body = new();

    public OggOpusWriter(string path)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteIdPage();
        WriteTagsPage();
    }

    /// Buffers one Opus packet; pages flush automatically at capacity.
    public void WriteOpusPacket(ReadOnlySpan<byte> opus, long granuleEnd)
    {
        int count = opus.Length / 255 + 1; // lacing values this packet needs
        if (_lacing.Count + count > MaxSegmentsPerPage || _body.Length + opus.Length > MaxBodyBytesPerPage)
        {
            FlushAudioPage(eos: false);
        }

        int full = opus.Length / 255, rem = opus.Length % 255;
        for (int i = 0; i < full; i++)
        {
            _lacing.Add(255);
        }

        _lacing.Add((byte)rem);
        _body.Write(opus);
        _granuleEnd = granuleEnd;
    }

    private void WriteIdPage()
    {
        Span<byte> head = stackalloc byte[19];
        Encoding.ASCII.GetBytes("OpusHead", head);
        head[8] = 1;                       // version
        head[9] = 1;                       // mono
        BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(10, 2), Preskip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(12, 4), InputSampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(16, 2), 0); // output gain
        head[18] = 0;                      // channel mapping family
        Span<byte> seg = [(byte)head.Length];
        WritePage(head, 0, headerType: 0x02, seg); // 0x02 = beginning of stream
    }

    private void WriteTagsPage()
    {
        byte[] vendor = Encoding.ASCII.GetBytes("OpusVoice.Receiver");
        using var tags = new MemoryStream(8 + 4 + vendor.Length + 4);
        tags.Write("OpusTags"u8);
        tags.Write([(byte)vendor.Length, 0, 0, 0]);
        tags.Write(vendor);
        tags.Write([(byte)0, 0, 0, 0]); // zero user comments
        byte[] body = tags.ToArray();
        Span<byte> seg = [(byte)body.Length];
        WritePage(body, 0, headerType: 0, seg);
    }

    private void FlushAudioPage(bool eos)
    {
        byte[] body = _body.ToArray();
        WritePage(body, _granuleEnd, headerType: eos ? (byte)0x04 : (byte)0, _lacing.ToArray());
        _lacing.Clear();
        _body.SetLength(0);
    }

    private void WritePage(ReadOnlySpan<byte> body, long granule, byte headerType, ReadOnlySpan<byte> segments)
    {
        Span<byte> head = stackalloc byte[27];
        Encoding.ASCII.GetBytes("OggS", head);
        head[4] = 0; // stream structure version
        head[5] = headerType;
        BinaryPrimitives.WriteInt64LittleEndian(head.Slice(6, 8), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(14, 4), _serial);
        BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(18, 4), _pageSequence++);
        BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(22, 4), 0); // CRC patched below
        head[26] = (byte)segments.Length;

        uint crc = Crc(head);
        crc = Crc(segments, crc);
        crc = Crc(body, crc);
        BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(22, 4), crc);

        _file.Write(head);
        _file.Write(segments);
        _file.Write(body);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FlushAudioPage(eos: true); // 0x04 = end of stream
        _file.Flush();
        _file.Dispose();
        _body.Dispose();
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            uint c = (uint)i << 24;
            for (int bit = 0; bit < 8; bit++)
            {
                c = (c & 0x8000_0000) != 0 ? (c << 1) ^ 0x04c1_1db7 : c << 1;
            }

            table[i] = c;
        }

        return table;
    }

    private static uint Crc(ReadOnlySpan<byte> data, uint crc = 0)
    {
        foreach (byte b in data)
        {
            crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ b];
        }

        return crc;
    }
}
