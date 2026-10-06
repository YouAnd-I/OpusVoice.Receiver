using System.Buffers.Binary;

namespace OpusVoice.Receiver;

/// One parsed RTP header (RFC 3550) — only the fields the capture path needs.
public readonly record struct RtpHeader(
    int PayloadType,
    ushort Sequence,
    uint Timestamp,
    uint Ssrc,
    int PayloadOffset,
    int PayloadLength)
{
    public static bool TryParse(ReadOnlySpan<byte> datagram, out RtpHeader header)
    {
        header = default;
        if (datagram.Length < 12)
        {
            return false;
        }

        if (datagram[0] >> 6 != 2)
        {
            return false; // RTP version must be 2
        }

        bool hasExtension = (datagram[0] & 0x10) != 0;
        int offset = 12 + (datagram[0] & 0x0F) * 4; // skip CSRC list
        if (datagram.Length < offset)
        {
            return false;
        }

        if (hasExtension)
        {
            if (datagram.Length < offset + 4)
            {
                return false;
            }

            offset += 4 + BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(offset + 2, 2)) * 4;
            if (datagram.Length < offset)
            {
                return false;
            }
        }

        header = new RtpHeader(
            PayloadType: datagram[1] & 0x7F,
            Sequence: BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(2, 2)),
            Timestamp: BinaryPrimitives.ReadUInt32BigEndian(datagram.Slice(4, 4)),
            Ssrc: BinaryPrimitives.ReadUInt32BigEndian(datagram.Slice(8, 4)),
            PayloadOffset: offset,
            PayloadLength: datagram.Length - offset);
        return true;
    }
}
