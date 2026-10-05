// SPDX-License-Identifier: GPL-3.0-or-later
namespace SdChk.Core.Devices;

/// <summary>Named simulated cards for demos and tests. Sizes are real; speeds are scaled to small runs.</summary>
public static class FakeScenarios
{
    private const long GiB = 1L << 30;

    public static readonly IReadOnlyDictionary<string, (string Description, FakeCardOptions Options)> All =
        new Dictionary<string, (string, FakeCardOptions)>
        {
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
