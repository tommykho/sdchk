using SdChk.Core;
using Xunit;

namespace SdChk.Tests;

public class PatternTests
{
    private const ulong Salt = 0x1234ABCD;

    [Fact]
    public void GoodBlockClassifiesGood()
    {
        var b = new byte[Pattern.BlockSize];
        Pattern.FillBlock(b, 8192, Salt);
        Assert.Equal(BlockState.Good, Pattern.Classify(b, 8192, Salt).State);
    }

    [Fact]
    public void BlockFromOtherOffsetIsChangedAndReportsOrigin()
    {
        var b = new byte[Pattern.BlockSize];
        Pattern.FillBlock(b, 1 << 30, Salt);
        var r = Pattern.Classify(b, 4096, Salt);
        Assert.Equal(BlockState.Changed, r.State);
        Assert.Equal(1 << 30, r.FoundOffset);
    }

    [Fact]
    public void ZeroAndFfAreBlank()
    {
        Assert.Equal(BlockState.Blank, Pattern.Classify(new byte[Pattern.BlockSize], 0, Salt).State);
        var ff = new byte[Pattern.BlockSize];
        Array.Fill(ff, (byte)0xFF);
        Assert.Equal(BlockState.Blank, Pattern.Classify(ff, 0, Salt).State);
    }

    [Fact]
    public void FlippedByteIsCorrupted()
    {
        var b = new byte[Pattern.BlockSize];
        Pattern.FillBlock(b, 0, Salt);
        b[2000] ^= 1;
        Assert.Equal(BlockState.Corrupted, Pattern.Classify(b, 0, Salt).State);
    }

    [Fact]
    public void DifferentSaltDoesNotMatch()
    {
        var b = new byte[Pattern.BlockSize];
        Pattern.FillBlock(b, 0, Salt);
        Assert.Equal(BlockState.Corrupted, Pattern.Classify(b, 0, Salt + 1).State);
    }

    [Fact]
    public void BlocksAreUnique()
    {
        var a = new byte[Pattern.BlockSize];
        var b = new byte[Pattern.BlockSize];
        Pattern.FillBlock(a, 0, Salt);
        Pattern.FillBlock(b, Pattern.BlockSize, Salt);
        Assert.False(a.AsSpan().SequenceEqual(b));
    }
}
