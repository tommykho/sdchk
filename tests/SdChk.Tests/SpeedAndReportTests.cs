using SdChk.Core;
using SdChk.Core.Devices;
using Xunit;

namespace SdChk.Tests;

public class SpeedAndReportTests
{
    private const long GiB = 1L << 30;

    private static SpeedResult Speed(FakeCardOptions card, string cls)
    {
        using var dev = new FakeBlockDevice(card);
        return SpeedTest.Run(dev, new SpeedTestOptions { SequentialBytes = 64L << 20, SampleSeconds = 0.05, RandomOps = 200, ClaimedClass = cls, Salt = 5 });
    }

    [Fact]
    public void FastSustainedCardPassesU3()
    {
        var r = Speed(new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine, FastWriteMBps = 60, SlowWriteMBps = 45 }, "U3");
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.Equal(3, r.Positions.Count);
    }

    [Fact]
    public void CacheThenSlowFlashFailsU3AndShowsDrop()
    {
        var r = Speed(new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine, FastWriteMBps = 80, SlowWriteMBps = 5, SlowdownAfterBytes = 16L << 20 }, "U3");
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.NotNull(r.Positions[0].DropAfterBytes);
        Assert.True(r.Positions[0].Write.Max > 60 && r.Positions[0].Write.Median < 20);
    }

    [Fact]
    public void SameCardPassesALowerClass()
    {
        var card = new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine, FastWriteMBps = 80, SlowWriteMBps = 12, SlowdownAfterBytes = 16L << 20 };
        Assert.Equal(Verdict.Pass, Speed(card, "C10").Verdict);
        Assert.Equal(Verdict.Fail, Speed(card, "U3").Verdict);
    }

    [Fact]
    public void RandomIopsAndA1Hint()
    {
        var good = Speed(new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine, RandomReadIops = 2500, RandomWriteIops = 700 }, "C10");
        Assert.Equal(true, good.MeetsA1);
        Assert.Equal(false, good.MeetsA2);
        var bad = Speed(new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine, RandomReadIops = 300, RandomWriteIops = 40 }, "C10");
        Assert.Equal(false, bad.MeetsA1);
    }

    [Fact]
    public void SpeedWithoutClassIsInconclusive()
    {
        var r = Speed(new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine }, "");
        Assert.Equal(Verdict.Inconclusive, r.Verdict);
    }

    [Fact]
    public void PercentileInterpolates()
    {
        Assert.Equal(5.0, SpeedTest.Percentile(new List<double> { 1, 3, 5, 7, 9 }, 50));
        Assert.Equal(1.0, SpeedTest.Percentile(new List<double> { 1, 3, 5, 7, 9 }, 0));
    }

    [Fact]
    public void ReportContainsDisclaimerVerdictsAndHash()
    {
        using var dev = new FakeBlockDevice(new FakeCardOptions { ClaimedSize = 512 * GiB, RealSize = 16 * GiB, Behavior = FakeBehavior.Wrap });
        var log = new RunLog();
        var size = SizeProbe.Run(dev, new SizeProbeOptions { Salt = 9, Log = log.Info });
        var report = new RunReport { Device = "<b>test</b>", Mode = "simulation", Size = size };
        string text = ReportWriter.BuildLog(report, log);
        string html = ReportWriter.BuildHtml(report, text);
        Assert.Contains("OVERWRITE", text);
        Assert.Contains("OVERWRITE", html);
        Assert.Contains("FAIL", html);
        Assert.Contains(ReportWriter.Sha256(text), html);
        Assert.DoesNotContain("<b>test</b>", html);
        Assert.Contains("&lt;b&gt;test&lt;/b&gt;", html);
    }

    [Fact]
    public void ReportFilesAreWritten()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sdchk-rep-{Guid.NewGuid():N}");
        try
        {
            var (html, logPath) = ReportWriter.Write(dir, new RunReport { Device = "x", Mode = "t" }, new RunLog());
            Assert.True(File.Exists(html));
            Assert.True(File.Exists(logPath));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
