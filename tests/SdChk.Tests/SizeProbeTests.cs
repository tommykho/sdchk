using SdChk.Core;
using SdChk.Core.Devices;
using Xunit;

namespace SdChk.Tests;

public class SizeProbeTests
{
    private const long GiB = 1L << 30;

    private static SizeResult Run(FakeCardOptions card, SizeProbeOptions? o = null)
    {
        using var dev = new FakeBlockDevice(card);
        return SizeProbe.Run(dev, o ?? new SizeProbeOptions { Salt = 42, ConfirmBytes = 96L << 20 });
    }

    [Fact]
    public void BisectionOrderIsFrontBackMiddleThenGaps()
    {
        var f = SizeProbe.Bisection(7).ToArray();
        Assert.Equal(new[] { 0.5, 0.25, 0.75, 0.125, 0.375, 0.625, 0.875 }, f);
    }

    [Fact]
    public void PlanStartsFrontBackMiddle()
    {
        using var dev = new FakeBlockDevice(new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine });
        var plan = SizeProbe.PlanProbes(dev, new SizeProbeOptions());
        Assert.Equal(0, plan[0]);
        Assert.Equal(64 * GiB - (8 << 20), plan[1]);
        Assert.InRange(plan[2], 32 * GiB - (8 << 20), 32 * GiB + (8 << 20));
    }

    [Fact]
    public void GenuineCardPasses()
    {
        var r = Run(new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine });
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.False(r.WrapAround);
    }

    [Theory]
    [InlineData(16L << 30)]
    [InlineData(32L << 30)]
    [InlineData(31_914_983_424L)]
    [InlineData(7_948_206_080L)]
    public void WrapAroundIsDetectedWithRealSize(long real)
    {
        var r = Run(new FakeCardOptions { ClaimedSize = 512 * GiB, RealSize = real, Behavior = FakeBehavior.Wrap });
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.True(r.WrapAround);
        Assert.Equal(real, r.EstimatedRealSize);
    }

    [Theory]
    [InlineData(FakeBehavior.Discard)]
    [InlineData(FakeBehavior.Garbage)]
    [InlineData(FakeBehavior.WriteError)]
    public void DroppedWritesAreDetectedWithinOneProbe(FakeBehavior behavior)
    {
        long real = 16 * GiB;
        var r = Run(new FakeCardOptions { ClaimedSize = 512 * GiB, RealSize = real, Behavior = behavior });
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.NotNull(r.EstimatedRealSize);
        Assert.InRange(r.EstimatedRealSize!.Value, real - (16 << 20), real + (16 << 20));
    }

    [Fact]
    public void LargeWriteCacheIsExposedByTheConfirmationPass()
    {
        var card = new FakeCardOptions { ClaimedSize = 512 * GiB, RealSize = 16 * GiB, Behavior = FakeBehavior.Discard, WriteCacheBytes = 128L << 20 };
        var small = Run(card, new SizeProbeOptions { Salt = 7, ConfirmBytes = 0 });
        Assert.NotEqual(Verdict.Fail, small.Verdict);

        var confirmed = Run(card, new SizeProbeOptions { Salt = 7, ConfirmBytes = 256L << 20 });
        Assert.Equal(Verdict.Fail, confirmed.Verdict);
        Assert.InRange(confirmed.EstimatedRealSize!.Value, 16 * GiB - (16 << 20), 16 * GiB + (16 << 20));
        Assert.Contains(confirmed.Notes, n => n.Contains("write cache"));
    }

    [Fact]
    public void SmallWriteCacheIsDefeatedByTheDefaultFlush()
    {
        var card = new FakeCardOptions { ClaimedSize = 512 * GiB, RealSize = 16 * GiB, Behavior = FakeBehavior.Discard, WriteCacheBytes = 32L << 20 };
        Assert.Equal(Verdict.Fail, Run(card, new SizeProbeOptions { Salt = 7, ConfirmBytes = 0 }).Verdict);
    }

    [Fact]
    public void ScatteredDefectsAreSuspectNotFakeSize()
    {
        var card = new FakeCardOptions
        {
            ClaimedSize = 64 * GiB,
            Behavior = FakeBehavior.Genuine,
            BadRanges = new[] { new Extent(0, 16 << 20) },
        };
        var r = Run(card);
        Assert.Equal(Verdict.Suspect, r.Verdict);
        Assert.Null(r.EstimatedRealSize);
    }

    [Fact]
    public void NoFreeSpaceIsInconclusive()
    {
        var card = new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine, UsableExtents = new[] { new Extent(0, 1 << 20) } };
        Assert.Equal(Verdict.Inconclusive, Run(card).Verdict);
    }

    [Fact]
    public void ProbesStayInsideFreeSpace()
    {
        var free = new[] { new Extent(1 * GiB, 4 * GiB), new Extent(40 * GiB, 8 * GiB) };
        using var dev = new FakeBlockDevice(new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine, UsableExtents = free });
        var plan = SizeProbe.PlanProbes(dev, new SizeProbeOptions());
        Assert.All(plan, off => Assert.Contains(free, e => off >= e.Start && off + (8 << 20) <= e.End));
        var r = SizeProbe.Run(dev, new SizeProbeOptions { Salt = 1, ConfirmBytes = 96L << 20 });
        Assert.Equal(Verdict.Pass, r.Verdict);
    }

    [Fact]
    public void CancellationGivesInconclusiveResult()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = Run(new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine }, new SizeProbeOptions { Cancel = cts.Token });
        Assert.True(r.Cancelled);
        Assert.Equal(Verdict.Inconclusive, r.Verdict);
    }

    [Fact]
    public void WorksOnARealFileImage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sdchk-test-{Guid.NewGuid():N}.img");
        try
        {
            using var dev = new FileBlockDevice(path, 256L << 20);
            var r = SizeProbe.Run(dev, new SizeProbeOptions { ProbeBytes = 1 << 20, CacheDefeatBytes = 8 << 20, ConfirmBytes = 32 << 20, Salt = 3 });
            Assert.Equal(Verdict.Pass, r.Verdict);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
