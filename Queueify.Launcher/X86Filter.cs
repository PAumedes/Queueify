namespace Queueify.Launcher;

/// <summary>
/// 7-Zip's x86 branch converter (BCJ), from the public-domain LZMA SDK by Igor Pavlov (Bra86.c): the 32-bit
/// target of each likely call (E8) or jump (E9) becomes absolute on the way in and relative again on the
/// way out. Works on x64 code too, and leaves anything that isn't code as reversible noise. Run over a
/// whole buffer at once; the last four bytes are never touched.
/// </summary>
public static class X86Filter
{
    private static readonly bool[] AllowedAfter = [true, true, true, false, true, false, false, false];
    private static readonly int[] BitNumber = [0, 1, 2, 2, 3, 3, 3, 3];

    public static void Encode(byte[] data) => Convert(data, encoding: true);

    public static void Decode(byte[] data) => Convert(data, encoding: false);

    // A plausible target's top byte is 00 or FF: within 16 MB either way.
    private static bool IsNear(byte b) => b is 0 or 0xFF;

    private static void Convert(byte[] data, bool encoding)
    {
        const uint ip = 5; // a target is relative to the end of the 5-byte instruction
        int limit = data.Length - 4;
        int pos = 0, previous = -1;
        uint mask = 0; // which of the last three bytes were E8/E9 that weren't converted
        while (true)
        {
            while (pos < limit && (data[pos] & 0xFE) != 0xE8) pos++;
            if (pos >= limit) return;

            int gap = pos - previous;
            if (gap > 3) mask = 0;
            else
            {
                mask = (mask << (gap - 1)) & 7;
                if (mask != 0 && (!AllowedAfter[mask] || IsNear(data[pos + 4 - BitNumber[mask]])))
                {
                    previous = pos;
                    mask = ((mask << 1) & 7) | 1;
                    pos++;
                    continue;
                }
            }
            previous = pos;

            if (!IsNear(data[pos + 4]))
            {
                mask = ((mask << 1) & 7) | 1;
                pos++;
                continue;
            }

            uint source = (uint)data[pos + 4] << 24 | (uint)data[pos + 3] << 16 | (uint)data[pos + 2] << 8 | data[pos + 1];
            uint target;
            while (true)
            {
                target = encoding ? source + ip + (uint)pos : source - (ip + (uint)pos);
                if (mask == 0) break;
                int index = BitNumber[mask] * 8;
                if (!IsNear((byte)(target >> (24 - index)))) break;
                source = target ^ ((1u << (32 - index)) - 1);
            }
            data[pos + 4] = (byte)~(((target >> 24) & 1) - 1);
            data[pos + 3] = (byte)(target >> 16);
            data[pos + 2] = (byte)(target >> 8);
            data[pos + 1] = (byte)target;
            pos += 5;
        }
    }
}
