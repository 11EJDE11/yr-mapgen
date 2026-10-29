using CNCMaps.FileFormats.Encodings;

namespace MapGen.Formats;

/// <summary>
/// Block framing used by IsoMapPack5 (LZO) and OverlayPack/OverlayDataPack (Format80):
/// each block is [u16 compressed size][u16 uncompressed size][data], at most 8 KiB uncompressed.
/// </summary>
public static class PackEncoding
{
    private const int BlockSize = 8192;

    public static byte[] EncodeLzo(byte[] data) => Encode(data, MiniLZO.MiniLZO.Compress);

    public static byte[] EncodeFormat80(byte[] data) => Encode(data, Format80.Encode);

    private static byte[] Encode(byte[] data, Func<byte[], byte[]> compress)
    {
        var output = new List<byte>(data.Length / 2);
        for (int offset = 0; offset < data.Length; offset += BlockSize)
        {
            int length = Math.Min(BlockSize, data.Length - offset);
            byte[] chunk = new byte[length];
            Array.Copy(data, offset, chunk, 0, length);
            byte[] compressed = compress(chunk);
            output.AddRange(BitConverter.GetBytes((ushort)compressed.Length));
            output.AddRange(BitConverter.GetBytes((ushort)length));
            output.AddRange(compressed);
        }
        return output.ToArray();
    }

    public static byte[] DecodeLzo(byte[] data, int expectedLength = -1) => Decode(data, expectedLength, lzo: true);

    public static byte[] DecodeFormat80(byte[] data, int expectedLength = -1) => Decode(data, expectedLength, lzo: false);

    private static unsafe byte[] Decode(byte[] data, int expectedLength, bool lzo)
    {
        var output = new List<byte>(expectedLength > 0 ? expectedLength : data.Length * 4);
        int offset = 0;
        while (offset + 4 <= data.Length)
        {
            int compressedSize = BitConverter.ToUInt16(data, offset);
            int uncompressedSize = BitConverter.ToUInt16(data, offset + 2);
            offset += 4;
            if (offset + compressedSize > data.Length)
                break;

            byte[] block = new byte[uncompressedSize];
            fixed (byte* src = &data[offset], dst = block)
            {
                if (lzo)
                {
                    uint outLen = (uint)uncompressedSize;
                    MiniLZO.MiniLZO.Decompress(src, (uint)compressedSize, dst, ref outLen);
                }
                else
                {
                    Format80.DecodeInto(src, dst);
                }
            }
            output.AddRange(block);
            offset += compressedSize;
        }
        return output.ToArray();
    }

    /// <summary>Joins a base64 section split over numbered keys 1..n.</summary>
    public static byte[] FromBase64Section(IReadOnlyDictionary<string, string> section)
    {
        var keys = section.Keys.Where(k => int.TryParse(k, out _)).OrderBy(int.Parse);
        string joined = string.Concat(keys.Select(k => section[k]));
        int pad = (4 - joined.Length % 4) % 4;
        return Convert.FromBase64String(joined + new string('=', pad));
    }

    /// <summary>Splits base64 data into 70-character lines keyed from 1, as the game and editors expect.</summary>
    public static IEnumerable<(string Key, string Value)> ToBase64Lines(byte[] data)
    {
        string s = Convert.ToBase64String(data);
        int line = 1;
        for (int i = 0; i < s.Length; i += 70)
            yield return ((line++).ToString(), s.Substring(i, Math.Min(70, s.Length - i)));
    }
}
