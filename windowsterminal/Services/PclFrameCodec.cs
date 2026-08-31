using System;
using System.Collections.Generic;
using System.Text;

namespace MerchantTerminal.Services;

/// <summary>
/// PCL (POS Control Link) frame codec — the wire format JPxSerialServer
/// accepts on its raw TCP socket (serverSocket.port, default 7001) and relays
/// to the terminal over USB serial. Mirror of the Kotlin codec in
/// winkpos/.../link/PclFrameCodec.kt — keep the two in sync.
///
/// Framing (verified against the worked example in
/// JPxSerialServiceUserGuide-V2.8, "TCP Service" section):
///   STX(0x02) | u16be payloadLen | TLVs | ETX(0x03) | CRC32be
/// where payloadLen counts TLV bytes only, the CRC-32 (standard zlib
/// polynomial) covers STX..ETX inclusive, and each TLV is
/// u16be tag | u16be length | value. Observed tags: 0xE000 source app,
/// 0xE001 destination, 0xE900 command id.
///
/// TODO(PAX): confirm ACK/NAK bytes+timing and a reserved tag for opaque
/// application payloads (0xE100 below is our placeholder) against the PCL
/// Messaging Protocol Specification.
/// </summary>
public static class PclFrameCodec
{
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;
    public const byte Ack = 0x06; // TODO(PAX): confirm
    public const byte Nak = 0x15; // TODO(PAX): confirm

    public const int TagSource = 0xE000;
    public const int TagDest = 0xE001;
    public const int TagCommand = 0xE900;
    public const int TagJson = 0xE100; // WinkPos payload — placeholder

    public const string SourcePos = "POS";
    public const string DestTerminal = "WINKPOS";

    public readonly record struct Tlv(int Tag, byte[] Value);

    public static byte[] Encode(IReadOnlyList<Tlv> tlvs)
    {
        var payloadLen = 0;
        foreach (var t in tlvs) payloadLen += 4 + t.Value.Length;

        var frame = new byte[3 + payloadLen + 1 + 4];
        var i = 0;
        frame[i++] = Stx;
        frame[i++] = (byte)(payloadLen >> 8);
        frame[i++] = (byte)payloadLen;
        foreach (var t in tlvs)
        {
            frame[i++] = (byte)(t.Tag >> 8);
            frame[i++] = (byte)t.Tag;
            frame[i++] = (byte)(t.Value.Length >> 8);
            frame[i++] = (byte)t.Value.Length;
            t.Value.CopyTo(frame, i);
            i += t.Value.Length;
        }

        frame[i++] = Etx;
        var crc = Crc32(frame.AsSpan(0, i));
        frame[i++] = (byte)(crc >> 24);
        frame[i++] = (byte)(crc >> 16);
        frame[i++] = (byte)(crc >> 8);
        frame[i] = (byte)crc;
        return frame;
    }

    /// <summary>Wraps one register protocol message in a PCL frame.</summary>
    public static byte[] EncodeMessage(Models.PosMessage message) => Encode(new[]
    {
        new Tlv(TagSource, Encoding.ASCII.GetBytes(SourcePos)),
        new Tlv(TagDest, Encoding.ASCII.GetBytes(DestTerminal)),
        new Tlv(TagJson, Encoding.UTF8.GetBytes(Models.PosJson.Serialize(message))),
    });

    public static List<Tlv> ParseTlvs(ReadOnlySpan<byte> payload)
    {
        var result = new List<Tlv>();
        var i = 0;
        while (i + 4 <= payload.Length)
        {
            var tag = (payload[i] << 8) | payload[i + 1];
            var len = (payload[i + 2] << 8) | payload[i + 3];
            i += 4;
            if (i + len > payload.Length) break;
            result.Add(new Tlv(tag, payload.Slice(i, len).ToArray()));
            i += len;
        }

        return result;
    }

    /// <summary>
    /// Incremental frame extractor: feed raw socket bytes, receive complete
    /// CRC-valid TLV payloads. Resynchronizes on the next STX after a corrupt
    /// frame; lone ACK/NAK bytes between frames are skipped.
    /// </summary>
    public sealed class Decoder
    {
        private readonly List<byte> _buf = new();

        public void Feed(ReadOnlySpan<byte> bytes, Action<List<Tlv>> onFrame)
        {
            foreach (var b in bytes) _buf.Add(b);
            while (true)
            {
                while (_buf.Count > 0 && _buf[0] != Stx) _buf.RemoveAt(0);
                if (_buf.Count < 3) return;

                var len = (_buf[1] << 8) | _buf[2];
                var total = 3 + len + 1 + 4;
                if (_buf.Count < total) return;

                var frame = _buf.GetRange(0, total).ToArray();
                var crcExpected = Crc32(frame.AsSpan(0, 3 + len + 1));
                var crcActual = ((uint)frame[total - 4] << 24) | ((uint)frame[total - 3] << 16)
                    | ((uint)frame[total - 2] << 8) | frame[total - 1];

                if (frame[3 + len] == Etx && crcExpected == crcActual)
                {
                    _buf.RemoveRange(0, total);
                    onFrame(ParseTlvs(frame.AsSpan(3, len)));
                }
                else
                {
                    _buf.RemoveAt(0); // corrupt: drop this STX, hunt for the next
                }
            }
        }
    }

    // Standard CRC-32 (ISO-HDLC / zlib), table-driven.
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }
}
