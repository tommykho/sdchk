using SdChk.Core;
using SdChk.Core.Devices;
using Xunit;

namespace SdChk.Tests;

public sealed class MapSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sdchk-ms-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private MapSessionOptions Options(bool step) => new()
    {
        StepMode = step,
        JournalDirectory = _dir,
        Probe = new SizeProbeOptions { Salt = 5, ProbeBytes = 4 << 20, CacheDefeatBytes = 16 << 20, ConfirmBytes = 32 << 20 },
    };

    private static void WaitFor(MapSession s, Func<SessionSnapshot, bool> pred)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (pred(s.Snapshot()))
            {
                return;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("Condition not reached: " + s.Snapshot().Message);
    }

    [Fact]
    public void LayoutFitsAtMost4096Cells()
    {
        var l = MapLayout.For(512L << 30);
        Assert.Equal(4096, l.Cells);
        Assert.Equal(64, l.Cols);
        Assert.Equal(64, l.Rows);
        Assert.Equal(64, MapLayout.For(1L << 30).Cells);
    }

    [Fact]
    public async Task RunAllOnAWrapFakeShowsFreeSpaceJournalsFailsAndRestoresFiles()
    {
        var (_, card) = FakeScenarios.All["used-wrap-32-of-512"];
        using var dev = new FakeBlockDevice(card);
        using var s = new MapSession(dev, Options(step: false));
        s.Start();
        await s.Completion!.WaitAsync(TimeSpan.FromMinutes(2));
        var snap = s.Snapshot();

        Assert.Equal(SessionStage.Done, snap.Stage);
        Assert.True(snap.FreeKnown);
        Assert.InRange(snap.FreeBytes, 400L << 30, 512L << 30);
        Assert.Contains(snap.FreePct, v => v < 100 && v != 255);
        Assert.Equal(Verdict.Fail, snap.Size!.Verdict);
        Assert.Equal(31_914_983_424, snap.Size.EstimatedRealSize);
        Assert.True(snap.UserDataCheckAvailable);
        Assert.True(snap.DamagedBeforeRestore > 0, "the fake wrapped writes onto user data");
        Assert.Equal(0, snap.DamagedAfterRestore);
        Assert.True(snap.RestoreResult!.Ok);
        Assert.Contains(snap.Status, v => v == (byte)CellStatus.Verified);
        Assert.Contains(snap.Status, v => v == (byte)CellStatus.Changed || v == (byte)CellStatus.Failed);
        Assert.Contains(snap.Restored, v => v == 1);
        Assert.False(File.Exists(snap.JournalPath!), "journal is deleted after a clean restore");
    }

    [Fact]
    public async Task GenuineCardPassesAndAllPlannedCellsEndUpVerified()
    {
        var (_, card) = FakeScenarios.All["used-genuine-64"];
        using var dev = new FakeBlockDevice(card);
        using var s = new MapSession(dev, Options(step: false));
        s.Start();
        await s.Completion!.WaitAsync(TimeSpan.FromMinutes(2));
        var snap = s.Snapshot();
        Assert.Equal(SessionStage.Done, snap.Stage);
        Assert.Equal(Verdict.Pass, snap.Size!.Verdict);
        Assert.DoesNotContain(snap.Status, v => v == (byte)CellStatus.Failed || v == (byte)CellStatus.Changed);
        Assert.Equal(0, snap.DamagedAfterRestore);
    }

    [Fact]
    public async Task StepModeWaitsAtEveryGateInOrder()
    {
        var (_, card) = FakeScenarios.All["used-genuine-64"];
        using var dev = new FakeBlockDevice(card);
        using var s = new MapSession(dev, Options(step: true));
        s.Start();

        var expected = new[]
        {
            (SessionStage.Map, "Show free space"),
            (SessionStage.FreeSpace, "Plan areas and create the journal"),
            (SessionStage.Journal, "Create the journal (save originals)"),
            (SessionStage.Write, "Write the areas"),
            (SessionStage.Validate, "Read back and validate"),
            (SessionStage.Restore, "Restore originals"),
        };
        foreach (var (stage, next) in expected)
        {
            WaitFor(s, x => x.Waiting && x.NextStep == next);
            var snap = s.Snapshot();
            Assert.Equal(stage, snap.Stage);
            if (stage == SessionStage.Map)
            {
                Assert.False(snap.FreeKnown, "free space is hidden until its step");
            }

            if (stage == SessionStage.FreeSpace)
            {
                Assert.True(snap.FreeKnown);
                Assert.DoesNotContain(snap.Status, v => v != 0);
            }

            if (stage == SessionStage.Journal)
            {
                Assert.Equal(0, snap.JournalBytes);
                Assert.Contains(snap.Status, v => v == (byte)CellStatus.Planned);
            }

            if (stage == SessionStage.Write)
            {
                Assert.True(snap.JournalBytes > 0);
                Assert.Contains(snap.Status, v => v == (byte)CellStatus.Journaled);
                Assert.DoesNotContain(snap.Status, v => v == (byte)CellStatus.Written);
            }

            if (stage == SessionStage.Validate)
            {
                Assert.Contains(snap.Status, v => v == (byte)CellStatus.Written);
                Assert.DoesNotContain(snap.Status, v => v == (byte)CellStatus.Verified);
            }

            s.Continue();
        }

        await s.Completion!.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(SessionStage.Done, s.Snapshot().Stage);
    }

    [Fact]
    public async Task CancelWhileWrittenRestoresOriginals()
    {
        var (_, card) = FakeScenarios.All["used-wrap-32-of-512"];
        using var dev = new FakeBlockDevice(card);
        using var s = new MapSession(dev, Options(step: true));
        s.Start();
        foreach (var next in new[] { "Show free space", "Plan areas and create the journal", "Create the journal (save originals)", "Write the areas" })
        {
            WaitFor(s, x => x.Waiting && x.NextStep == next);
            s.Continue();
        }

        WaitFor(s, x => x.Waiting && x.NextStep == "Read back and validate");
        Assert.True(dev.CountDamagedUserBlocks() > 0, "areas are written and the fake wrapped them onto user data");
        s.Cancel();
        await s.Completion!.WaitAsync(TimeSpan.FromMinutes(1));
        var snap = s.Snapshot();
        Assert.Equal(SessionStage.Cancelled, snap.Stage);
        Assert.True(snap.RestoreResult!.Ok);
        Assert.Equal(0, dev.CountDamagedUserBlocks());
    }

    [Fact]
    public async Task JournalOnTheCardIsRefused()
    {
        var (_, card) = FakeScenarios.All["used-genuine-64"];
        using var dev = new FakeBlockDevice(card);
        string root = Path.GetPathRoot(Path.GetFullPath(_dir))!;
        var opts = new MapSessionOptions { JournalDirectory = _dir, DeviceRoot = root, Probe = Options(false).Probe };
        using var s2 = new MapSession(dev, opts);
        s2.Start();
        await s2.Completion!.WaitAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(SessionStage.Failed, s2.Snapshot().Stage);
        Assert.Equal(0, dev.CountDamagedUserBlocks());
        Assert.Contains("card", s2.Snapshot().Message);
    }
}
