using Queueify.Launcher;

namespace Queueify.Tests;

// Before compressing, the app's machine code goes through 7-Zip's x86 branch filter: calls (E8) and jumps (E9)
// store their target relative to where they are, so calls to one function all differ; made absolute, they
// repeat, and LZMA finds them.
public class X86FilterTests
{
    private static byte[] CallAt(int offset, int relative, int length = 64)
    {
        var data = new byte[length];
        Array.Fill(data, (byte)0x90);
        data[offset] = 0xE8;
        BitConverter.GetBytes(relative).CopyTo(data, offset + 1);
        return data;
    }

    [Fact]
    public void Calls_to_the_same_function_look_the_same_after_filtering()
    {
        // The call at 16 jumps 0x30 ahead, the one at 32 jumps 0x20 ahead: both go to 0x45.
        var first = CallAt(16, 0x30);
        var second = CallAt(32, 0x20);

        X86Filter.Encode(first);
        X86Filter.Encode(second);

        Assert.Equal(first.AsSpan(17, 4).ToArray(), second.AsSpan(33, 4).ToArray());
        Assert.Equal(0x45, BitConverter.ToInt32(first, 17));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(1000)]
    [InlineData(1 << 20)]
    public void Decoding_gives_back_exactly_what_was_encoded(int length)
    {
        // Random bytes with plenty of E8 and E9 in them, near each other and at the very end.
        var random = new Random(length);
        var original = new byte[length];
        random.NextBytes(original);
        for (int i = 0; i < length; i += random.Next(1, 8)) original[i] = random.Next(2) == 0 ? (byte)0xE8 : (byte)0xE9;
        if (length > 0) original[^1] = 0xE8;

        var data = (byte[])original.Clone();
        X86Filter.Encode(data);
        X86Filter.Decode(data);

        Assert.Equal(original, data);
    }

    [Fact]
    public void Real_program_code_comes_back_unchanged()
    {
        var original = File.ReadAllBytes(Environment.ProcessPath!);
        var data = (byte[])original.Clone();

        X86Filter.Encode(data);
        Assert.NotEqual(original, data);
        X86Filter.Decode(data);

        Assert.Equal(original, data);
    }
}
