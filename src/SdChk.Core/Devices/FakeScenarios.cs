// SPDX-License-Identifier: GPL-3.0-or-later
namespace SdChk.Core.Devices;

/// <summary>Named simulated cards for demos and tests. Sizes are real; speeds are scaled to small runs.</summary>
public static class FakeScenarios
{
    private const long GiB = 1L << 30;

    /// <summary>Pseudo-random file-like ranges covering about <paramref name="fraction"/> of the first <paramref name="region"/> bytes.</summary>
    public static List<Extent> Files(long region, double fraction, int seed, long fileBytes = 256L << 20)
    {
        var rng = new Random(seed);
        long count = Math.Max(1, (long)(region * fraction / fileBytes));
        var list = new List<Extent> { new(0, 64L << 20) };
        for (long i = 0; i < count; i++)
        {
            long start = rng.NextInt64(64L << 20, Math.Max((64L << 20) + 1, region - fileBytes)) / (1 << 20) * (1 << 20);
            list.Add(new Extent(start, fileBytes / 2 + rng.NextInt64(fileBytes / 2) / (1 << 20) * (1 << 20)));
        }

        return Merge(list);
    }

    private static List<Extent> Merge(List<Extent> list)
    {
        var merged = new List<Extent>();
        foreach (var e in list.OrderBy(x => x.Start))
        {
            if (merged.Count > 0 && e.Start <= merged[^1].End)
            {
                merged[^1] = new Extent(merged[^1].Start, Math.Max(merged[^1].End, e.End) - merged[^1].Start);
            }
            else
            {
                merged.Add(e);
            }
        }

        return merged;
    }

    public static readonly IReadOnlyDictionary<string, (string Description, FakeCardOptions Options)> All =
        new Dictionary<string, (string, FakeCardOptions)>
        {
            ["used-wrap-32-of-512"] = ("512 GiB label, real 31.9 GB, wraps around, holds your files (shows the journal protecting them)", new FakeCardOptions { ClaimedSize = 512 * GiB, RealSize = 31_914_983_424, Behavior = FakeBehavior.Wrap, UsedExtents = Files(31_914_983_424, 0.30, 7) }),
            ["used-discard-16-of-512"] = ("512 GiB label, real 16 GiB, extra writes dropped, holds your files", new FakeCardOptions { ClaimedSize = 512 * GiB, RealSize = 16 * GiB, Behavior = FakeBehavior.Discard, UsedExtents = Files(16 * GiB, 0.35, 11) }),
            ["used-genuine-64"] = ("Genuine 64 GiB card, 40% full", new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine, UsedExtents = Files(64 * GiB, 0.40, 3) }),
            ["genuine"] = ("Genuine 64 GiB card, U3 speed", new FakeCardOptions { ClaimedSize = 64 * GiB, Behavior = FakeBehavior.Genuine, FastWriteMBps = 60, SlowWriteMBps = 45, SlowdownAfterBytes = 40L << 20 }),
            ["wrap-32-of-512"] = ("512 GiB label, real 31.9 GB, wraps around (non power of two)", new FakeCardOptions { ClaimedSize = 512 * GiB, RealSize = 31_914_983_424, Behavior = FakeBehavior.Wrap }),
            ["wrap-16-of-512"] = ("512 GiB label, real 16 GiB, wraps around", new FakeCardOptions { ClaimedSize = 512 * GiB, RealSize = 16 * GiB, Behavior = FakeBehavior.Wrap }),
            ["discard-16-of-512"] = ("512 GiB label, real 16 GiB, extra writes silently dropped (reads zeros)", new FakeCardOptions { ClaimedSize = 512 * GiB, RealSize = 16 * GiB, Behavior = FakeBehavior.Discard }),
            ["cached-discard-16-of-512"] = ("As above but a 32 MiB write cache hides the drop until flushed", new FakeCardOptions { ClaimedSize = 512 * GiB, RealSize = 16 * GiB, Behavior = FakeBehavior.Discard, WriteCacheBytes = 32L << 20 }),
            ["garbage-32-of-256"] = ("256 GiB label, real 32 GiB, reads return noise beyond", new FakeCardOptions { ClaimedSize = 256 * GiB, RealSize = 32 * GiB, Behavior = FakeBehavior.Garbage }),
            ["write-error-8-of-128"] = ("128 GiB label, real 8 GiB, writes fail beyond", new FakeCardOptions { ClaimedSize = 128 * GiB, RealSize = 8 * GiB, Behavior = FakeBehavior.WriteError }),
            ["slow-genuine"] = ("Genuine 128 GiB size but slow flash: fast cache then 6 MB/s", new FakeCardOptions { ClaimedSize = 128 * GiB, Behavior = FakeBehavior.Genuine, FastWriteMBps = 40, SlowWriteMBps = 6, SlowdownAfterBytes = 16L << 20, RandomWriteIops = 90, RandomReadIops = 700 }),
        };
}
