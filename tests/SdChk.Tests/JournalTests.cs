using SdChk.Core;
using SdChk.Core.Devices;
using Xunit;

namespace SdChk.Tests;

public sealed class JournalTests : IDisposable
{
    private const long GiB = 1L << 30;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sdchk-j-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private string JournalPath() => Path.Combine(_dir, "j.sdchkj");

    private static FakeCardOptions UsedCard(FakeBehavior behavior, long real) => new()
    {
        ClaimedSize = 512 * GiB,
        RealSize = real,
        Behavior = behavior,
        UsedExtents = FakeScenarios.Files(real, 0.30, 5),
    };

    private static SizeProbeOptions Small(ProbeObserver? observer) => new()
    {
        Salt = 77,
        ProbeBytes = 4 << 20,
        CacheDefeatBytes = 16 << 20,
        ConfirmBytes = 32 << 20,
        Observer = observer,
    };

    [Fact]
    public void ProtectOverwriteRestoreReturnsOriginalBytes()
    {
        using var dev = new FakeBlockDevice(UsedCard(FakeBehavior.Genuine, 512 * GiB));
        using var journal = Journal.Create(JournalPath(), dev.Description, dev.Size);
        var area = dev.Options.UsedExtents[0];
        var part = new Extent(area.Start, 8 << 20);
        journal.Protect(dev, new[] { part });

        var junk = new byte[1 << 20];
        Random.Shared.NextBytes(junk);
        for (long p = 0; p < part.Length; p += junk.Length)
        {
            dev.Write(part.Start + p, junk);
        }

        Assert.True(dev.CountDamagedUserBlocks() > 0);
        var result = journal.Restore(dev);
        Assert.True(result.Ok);
        Assert.Equal(0, dev.CountDamagedUserBlocks());
    }

    [Fact]
    public void ProtectIsIdempotentAndSkipsCoveredRanges()
    {
        using var dev = new FakeBlockDevice(UsedCard(FakeBehavior.Genuine, 512 * GiB));
        using var journal = Journal.Create(JournalPath(), dev.Description, dev.Size);
        journal.Protect(dev, new[] { new Extent(0, 4 << 20) });
        journal.Protect(dev, new[] { new Extent(0, 4 << 20), new Extent(2 << 20, 4 << 20) });
        Assert.Equal(6L << 20, journal.Bytes);
        Assert.Equal(2, journal.Entries.Count);
    }

    [Fact]
    public void WrapAroundFakeDestroysFilesWithoutJournal()
    {
        long real = 31_914_983_424;
        using var dev = new FakeBlockDevice(UsedCard(FakeBehavior.Wrap, real));
        var r = SizeProbe.Run(dev, Small(null));
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.True(dev.CountDamagedUserBlocks() > 0, "without protection the wrap-around should have overwritten user data");
    }

    [Fact]
    public void WrapAroundFakeWithJournalLeavesFilesIntact()
    {
        long real = 31_914_983_424;
        using var dev = new FakeBlockDevice(UsedCard(FakeBehavior.Wrap, real));
        using var journal = Journal.Create(JournalPath(), dev.Description, dev.Size);
        var r = SizeProbe.Run(dev, Small(new JournalObserver(journal, dev)));
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Equal(real, r.EstimatedRealSize);
        Assert.True(dev.CountDamagedUserBlocks() > 0, "damage exists until the journal is restored");

        var restore = journal.Restore(dev);
        Assert.True(restore.Ok, string.Join("; ", restore.Failures.Select(f => f.Reason)));
        Assert.Equal(0, dev.CountDamagedUserBlocks());
    }

    [Theory]
    [InlineData(FakeBehavior.Discard)]
    [InlineData(FakeBehavior.Garbage)]
    [InlineData(FakeBehavior.WriteError)]
    public void DroppedWriteFakesRestoreCleanly(FakeBehavior behavior)
    {
        using var dev = new FakeBlockDevice(UsedCard(behavior, 16 * GiB));
        using var journal = Journal.Create(JournalPath(), dev.Description, dev.Size);
        var r = SizeProbe.Run(dev, Small(new JournalObserver(journal, dev)));
        Assert.Equal(Verdict.Fail, r.Verdict);
        var restore = journal.Restore(dev);
        Assert.True(restore.Ok, string.Join("; ", restore.Failures.Select(f => f.Reason)));
        Assert.Equal(0, dev.CountDamagedUserBlocks());
    }

    [Fact]
    public void GenuineCardWithFilesPassesAndRestoresToo()
    {
        using var dev = new FakeBlockDevice(new FakeCardOptions
        {
            ClaimedSize = 64 * GiB,
            Behavior = FakeBehavior.Genuine,
            UsedExtents = FakeScenarios.Files(64 * GiB, 0.4, 1),
        });
        using var journal = Journal.Create(JournalPath(), dev.Description, dev.Size);
        var r = SizeProbe.Run(dev, Small(new JournalObserver(journal, dev)));
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.True(journal.Restore(dev).Ok);
        Assert.Equal(0, dev.CountDamagedUserBlocks());
    }

    private sealed class CancelAfterWrites(int count, CancellationTokenSource cts) : ProbeObserver
    {
        private int _n;

        public override void AreaWritten(PlannedArea area)
        {
            if (++_n == count)
            {
                cts.Cancel();
            }
        }
    }

    private sealed class Chain(params ProbeObserver[] list) : ProbeObserver
    {
        public override void Planned(IReadOnlyList<PlannedArea> a) => list.ToList().ForEach(x => x.Planned(a));

        public override void Protect(IReadOnlyList<Extent> e) => list.ToList().ForEach(x => x.Protect(e));

        public override void AreaWritten(PlannedArea a) => list.ToList().ForEach(x => x.AreaWritten(a));
    }

    [Fact]
    public void CancelMidWriteThenRestoreLeavesFilesIntact()
    {
        long real = 31_914_983_424;
        using var dev = new FakeBlockDevice(UsedCard(FakeBehavior.Wrap, real));
        using var journal = Journal.Create(JournalPath(), dev.Description, dev.Size);
        using var cts = new CancellationTokenSource();
        var observer = new Chain(new JournalObserver(journal, dev), new CancelAfterWrites(12, cts));
        var r = SizeProbe.Run(dev, Small(observer) with { Cancel = cts.Token });
        Assert.True(r.Cancelled);
        Assert.True(journal.Restore(dev).Ok);
        Assert.Equal(0, dev.CountDamagedUserBlocks());
    }

    [Fact]
    public void RecoveryFromAFileAfterACrash()
    {
        long real = 31_914_983_424;
        string path = JournalPath();
        using (var dev = new FakeBlockDevice(UsedCard(FakeBehavior.Wrap, real)))
        {
            Journal journal = Journal.Create(path, dev.Description, dev.Size);
            using var cts = new CancellationTokenSource();
            SizeProbe.Run(dev, Small(new Chain(new JournalObserver(journal, dev), new CancelAfterWrites(20, cts))) with { Cancel = cts.Token });
            journal.Dispose();

            using var reopened = Journal.Open(path);
            Assert.True(reopened.Entries.Count > 10);
            Assert.True(dev.CountDamagedUserBlocks() > 0);
            Assert.True(reopened.Restore(dev).Ok);
            Assert.Equal(0, dev.CountDamagedUserBlocks());
        }
    }

    [Fact]
    public void TornTailIsIgnoredOnOpen()
    {
        using var dev = new FakeBlockDevice(UsedCard(FakeBehavior.Genuine, 512 * GiB));
        string path = JournalPath();
        using (var j = Journal.Create(path, dev.Description, dev.Size))
        {
            j.Protect(dev, new[] { new Extent(0, 2 << 20), new Extent(8 << 20, 2 << 20) });
        }

        long len = new FileInfo(path).Length;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(len - 1000);
        }

        using var reopened = Journal.Open(path);
        Assert.Single(reopened.Entries);
    }

    [Fact]
    public void CorruptJournalDataIsReportedNotWritten()
    {
        using var dev = new FakeBlockDevice(UsedCard(FakeBehavior.Genuine, 512 * GiB));
        string path = JournalPath();
        long dataPos;
        using (var j = Journal.Create(path, dev.Description, dev.Size))
        {
            j.Protect(dev, new[] { new Extent(0, 2 << 20) });
            dataPos = j.Entries[0].DataPosition;
        }

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.Position = dataPos + 100;
            fs.WriteByte(0xAB);
            fs.WriteByte(0xCD);
        }

        using var reopened = Journal.Open(path);
        var result = reopened.Restore(dev);
        Assert.False(result.Ok);
        Assert.Contains("corrupt", result.Failures[0].Reason);
    }

    [Fact]
    public void ComplementAndSubtractWork()
    {
        var free = Extents.Complement(100, new[] { new Extent(10, 20), new Extent(25, 20), new Extent(90, 10) });
        Assert.Equal(new[] { new Extent(0, 10), new Extent(45, 45) }, free);
        var parts = Extents.Subtract(new Extent(0, 100), new[] { new Extent(10, 10), new Extent(50, 10) });
        Assert.Equal(new[] { new Extent(0, 10), new Extent(20, 30), new Extent(60, 40) }, parts);
    }
}
