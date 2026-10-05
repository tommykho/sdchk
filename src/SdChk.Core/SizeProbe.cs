// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace SdChk.Core;

public enum Verdict
{
    Pass,
    Suspect,
    Fail,
    Inconclusive,
}

public sealed record SizeProbeOptions
{
    /// <summary>Size of each probe written to the card.</summary>
    public int ProbeBytes { get; init; } = 8 << 20;

    /// <summary>Extra data written elsewhere to push probes out of the card's write cache before verifying.</summary>
    public long CacheDefeatBytes { get; init; } = 64L << 20;

    /// <summary>
    /// Second, larger cache flush before re-verifying every probe. Catches fakes whose write cache is bigger than
    /// <see cref="CacheDefeatBytes"/>. Also becomes the flush size for real-size refinement if it exposes a cache. 0 disables.
    /// </summary>
    public long ConfirmBytes { get; init; } = 1L << 30;

    /// <summary>Number of probes in the front/back/middle/bisection pattern.</summary>
    public int SpreadPoints { get; init; } = 32;

    /// <summary>Verification reads this many bytes at a time, in shuffled order (random reads defeat sequential caches).</summary>
    public int VerifyReadBytes { get; init; } = 64 << 10;

    public int WriteChunkBytes { get; init; } = 1 << 20;

    /// <summary>Read one block every <see cref="ScanStrideBytes"/> across the whole address space looking for misplaced data.</summary>
    public bool AliasScan { get; init; } = true;

    public int ScanStrideBytes { get; init; }

    public ulong? Salt { get; init; }

    public Action<string>? Log { get; init; }

    public Action<string, double>? Progress { get; init; }

    public CancellationToken Cancel { get; init; }
}

public sealed record ProbeOutcome(long Offset, int Good, int Corrupted, int Changed, int Blank, int Unreadable, bool WriteFailed)
{
    public bool IsGood => !WriteFailed && Corrupted + Changed + Blank + Unreadable == 0;
}

public sealed record AliasHit(long ReadOffset, long FoundOffset);

public sealed class SizeResult
{
    public Verdict Verdict { get; set; } = Verdict.Inconclusive;

    public string Summary { get; set; } = "";

    public long ClaimedSize { get; set; }

    public long? EstimatedRealSize { get; set; }

    public bool WrapAround { get; set; }

    public int ProbeBytes { get; set; }

    public long CacheDefeatBytes { get; set; }

    public List<ProbeOutcome> Probes { get; } = new();

    public List<AliasHit> AliasHits { get; } = new();

    public int AliasHitCount { get; set; }

    public long ScanReads { get; set; }

    public long BytesWritten { get; set; }

    public double CoveragePercent { get; set; }

    public bool Cancelled { get; set; }

    public double DurationSeconds { get; set; }

    public List<string> Notes { get; } = new();
}

/// <summary>
/// Fast capacity check inspired by the f3probe idea: write unique probes across the address space
/// (front, back, middle, bisection and power-of-two offsets), defeat the card's write cache, read the
/// probes back in random order, then scan the whole address space for data that landed in the wrong place.
/// </summary>
public static class SizeProbe
{
    private const int MaxAliasHitsKept = 200;

    public static SizeResult Run(IBlockDevice dev, SizeProbeOptions o)
    {
        var res = new SizeResult { ClaimedSize = dev.Size, ProbeBytes = o.ProbeBytes, CacheDefeatBytes = o.CacheDefeatBytes };
        double t0 = dev.Clock.Now;
        ulong salt = o.Salt ?? BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var rng = new Random(unchecked((int)(salt ^ (salt >> 32))));
        var ctx = new Ctx(dev, o, salt, rng, res);

        try
        {
            RunCore(ctx);
        }
        catch (OperationCanceledException)
        {
            res.Cancelled = true;
            res.Verdict = Verdict.Inconclusive;
            res.Summary = "Stopped by user before the size check finished.";
        }

        res.DurationSeconds = dev.Clock.Now - t0;
        return res;
    }

    private static void RunCore(Ctx c)
    {
        var o = c.O;
        var res = c.Res;
        var plan = PlanProbes(c.Dev, o);
        if (plan.Count < 2)
        {
            res.Verdict = Verdict.Inconclusive;
            res.Summary = "Not enough free space to place probes. Free up space or use an empty card.";
            return;
        }

        o.Log?.Invoke($"Size check: {plan.Count} probes of {o.ProbeBytes >> 20} MiB, salt {c.Salt:X16}");

        var writeFailed = new HashSet<long>();
        for (int i = 0; i < plan.Count; i++)
        {
            c.Cancel();
            o.Progress?.Invoke("Writing probes", (double)i / plan.Count);
            if (!TryWrite(c, plan[i], o.ProbeBytes))
            {
                writeFailed.Add(plan[i]);
                o.Log?.Invoke($"Write failed at {plan[i]}");
            }
        }

        DefeatCache(c, plan);
        o.Log?.Invoke("Verifying probes (random-order reads)");
        var outcomes = VerifyAll(c, plan, writeFailed, "Verifying probes");
        if (o.ConfirmBytes > o.CacheDefeatBytes)
        {
            outcomes = ConfirmPass(c, plan, writeFailed, outcomes);
        }

        res.Probes.AddRange(outcomes.OrderBy(x => x.Offset));

        if (o.AliasScan)
        {
            Scan(c, plan);
        }

        Decide(c, plan);
    }

    private static List<ProbeOutcome> VerifyAll(Ctx c, List<long> plan, HashSet<long> writeFailed, string label)
    {
        var list = new List<ProbeOutcome>();
        for (int i = 0; i < plan.Count; i++)
        {
            c.Cancel();
            c.O.Progress?.Invoke(label, (double)i / plan.Count);
            list.Add(VerifyProbe(c, plan[i], writeFailed.Contains(plan[i]), true));
        }

        return list;
    }

    /// <summary>Flush the card's cache harder, then verify every probe again; a probe is good only if good both times.</summary>
    private static List<ProbeOutcome> ConfirmPass(Ctx c, List<long> plan, HashSet<long> writeFailed, List<ProbeOutcome> first)
    {
        long len = c.O.ConfirmBytes;
        long? gap = null;
        while (len > c.O.CacheDefeatBytes && (gap = FindGap(c, plan, len)) is null)
        {
            len /= 2;
        }

        if (gap is null || len <= c.O.CacheDefeatBytes)
        {
            c.Res.Notes.Add("Large cache check skipped: not enough free space for it.");
            return first;
        }

        c.O.Log?.Invoke($"Large cache check: writing {len >> 20} MiB, then verifying all probes again");
        TryWrite(c, gap.Value, len);
        c.FillerUsed.Add(gap.Value);
        try
        {
            c.Dev.Flush();
        }
        catch (IOException)
        {
        }

        c.Dev.ResetCache();
        var second = VerifyAll(c, plan, writeFailed, "Verifying probes again");
        var merged = new List<ProbeOutcome>();
        bool exposed = false;
        for (int i = 0; i < first.Count; i++)
        {
            bool worse = second[i].Good < first[i].Good;
            exposed |= worse && first[i].IsGood;
            merged.Add(worse ? second[i] : first[i]);
        }

        if (exposed)
        {
            c.FlushBytes = len;
            c.Res.CacheDefeatBytes = len;
            c.Res.Notes.Add($"The card's write cache is larger than {c.O.CacheDefeatBytes >> 20} MiB: some data only looked stored until more data was written.");
        }

        return merged;
    }

    // ---- planning ----

    internal static List<long> PlanProbes(IBlockDevice dev, SizeProbeOptions o)
    {
        long p = o.ProbeBytes;
        var plan = new List<long>();
        bool Overlaps(long off) => plan.Any(x => Math.Abs(x - off) < p);

        void Add(long? off)
        {
            if (off is { } v && !Overlaps(v))
            {
                plan.Add(v);
            }
        }

        long span = Math.Max(0, dev.Size - p);
        Add(Snap(dev, p, 0));
        Add(Snap(dev, p, span));
        Add(Snap(dev, p, span / 2));
        foreach (double f in Bisection(o.SpreadPoints))
        {
            Add(Snap(dev, p, (long)(f * span)));
        }

        if (plan.Count > 0)
        {
            long reference = plan[0];
            for (long k = p; reference + k + p <= dev.Size; k <<= 1)
            {
                long off = reference + k;
                if (Fits(dev, p, off))
                {
                    Add(off);
                }
            }
        }

        return plan;
    }

    /// <summary>0, 1, 1/2, 1/4, 3/4, 1/8, 3/8, ... (front, back, middle, then filling the gaps).</summary>
    internal static IEnumerable<double> Bisection(int count)
    {
        int produced = 0;
        for (int level = 1; produced < count; level++)
        {
            int denom = 1 << level;
            for (int n = 1; n < denom && produced < count; n += 2)
            {
                yield return (double)n / denom;
                produced++;
            }
        }
    }

    private static bool Fits(IBlockDevice dev, long p, long off) =>
        off >= 0 && off + p <= dev.Size && dev.UsableExtents.Any(e => off >= e.Start && off + p <= e.End);

    /// <summary>Nearest probe-aligned offset to <paramref name="target"/> that lies fully inside free space.</summary>
    private static long? Snap(IBlockDevice dev, long p, long target)
    {
        long best = -1;
        long bestDist = long.MaxValue;
        foreach (var e in dev.UsableExtents)
        {
            long lo = (e.Start + p - 1) / p * p;
            long hi = (e.End - p) / p * p;
            if (lo > hi)
            {
                continue;
            }

            long cand = Math.Clamp(target / p * p, lo, hi);
            long dist = Math.Abs(cand - target);
            if (dist < bestDist)
            {
                best = cand;
                bestDist = dist;
            }
        }

        return best < 0 ? null : best;
    }

    // ---- I/O ----

    private static bool TryWrite(Ctx c, long offset, long length)
    {
        int chunk = c.O.WriteChunkBytes;
        var buf = new byte[chunk];
        try
        {
            for (long pos = 0; pos < length; pos += chunk)
            {
                int n = (int)Math.Min(chunk, length - pos);
                Pattern.Fill(buf.AsSpan(0, n), offset + pos, c.Salt);
                c.Dev.Write(offset + pos, buf.AsSpan(0, n));
                c.Res.BytesWritten += n;
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static long? FindGap(Ctx c, IEnumerable<long> probes, long length)
    {
        long p = c.O.ProbeBytes;
        var blocked = probes.Select(x => (Start: x, End: x + p)).OrderBy(x => x.Start).ToList();
        foreach (var e in c.Dev.UsableExtents)
        {
            long cursor = (e.Start + p - 1) / p * p;
            while (cursor + length <= e.End)
            {
                var hit = blocked.FirstOrDefault(b => b.Start < cursor + length && b.End > cursor);
                if (hit == default)
                {
                    return cursor;
                }

                cursor = (hit.End + p - 1) / p * p;
            }
        }

        return null;
    }

    private static void DefeatCache(Ctx c, IEnumerable<long> probes)
    {
        if (c.FlushBytes > 0)
        {
            long? gap = FindGap(c, probes, c.FlushBytes);
            if (gap is { } g)
            {
                c.O.Log?.Invoke($"Flushing card write cache with {c.FlushBytes >> 20} MiB at {g}");
                TryWrite(c, g, c.FlushBytes);
                c.FillerUsed.Add(g);
            }
            else
            {
                c.Res.Notes.Add("No free gap for cache flushing; a cheating write cache could hide a fake card.");
            }
        }

        try
        {
            c.Dev.Flush();
        }
        catch (IOException)
        {
        }

        c.Dev.ResetCache();
    }

    private static ProbeOutcome VerifyProbe(Ctx c, long offset, bool writeFailed, bool collectAlias)
    {
        int unit = c.O.VerifyReadBytes;
        int units = (int)(c.O.ProbeBytes / unit);
        var order = Enumerable.Range(0, units).ToArray();
        c.Rng.Shuffle(order);

        int good = 0, corrupted = 0, changed = 0, blank = 0, unreadable = 0;
        var buf = new byte[unit];
        foreach (int u in order)
        {
            long at = offset + (long)u * unit;
            bool ok = true;
            try
            {
                c.Dev.Read(at, buf);
            }
            catch (IOException)
            {
                ok = false;
            }

            for (int b = 0; b < unit; b += Pattern.BlockSize)
            {
                if (!ok)
                {
                    unreadable++;
                    continue;
                }

                var r = Pattern.Classify(buf.AsSpan(b, Pattern.BlockSize), at + b, c.Salt);
                switch (r.State)
                {
                    case BlockState.Good: good++; break;
                    case BlockState.Corrupted: corrupted++; break;
                    case BlockState.Blank: blank++; break;
                    case BlockState.Changed:
                        changed++;
                        if (collectAlias)
                        {
                            AddHit(c, at + b, r.FoundOffset);
                        }

                        break;
                }
            }
        }

        return new ProbeOutcome(offset, good, corrupted, changed, blank, unreadable, writeFailed);
    }

    private static void AddHit(Ctx c, long readOffset, long found)
    {
        c.Res.AliasHitCount++;
        if (c.Res.AliasHits.Count < MaxAliasHitsKept)
        {
            c.Res.AliasHits.Add(new AliasHit(readOffset, found));
        }
        else
        {
            c.Gcd = Gcd(c.Gcd, Math.Abs(found - readOffset));
        }
    }

    private static void Scan(Ctx c, List<long> plan)
    {
        long p = c.O.ProbeBytes;
        long stride = c.O.ScanStrideBytes > 0 ? c.O.ScanStrideBytes : p / 2;
        stride = Math.Max(stride, Pattern.BlockSize) / Pattern.BlockSize * Pattern.BlockSize;
        long total = c.Dev.UsableExtents.Sum(e => e.Length / stride + 1);
        long done = 0;
        var buf = new byte[Pattern.BlockSize];
        var skip = plan.Concat(c.FillerUsed).OrderBy(x => x).ToList();
        o_log(c, $"Alias scan: one block every {stride >> 10} KiB over the whole address space (~{total} reads)");

        foreach (var e in c.Dev.UsableExtents)
        {
            for (long pos = (e.Start + Pattern.BlockSize - 1) / Pattern.BlockSize * Pattern.BlockSize; pos + Pattern.BlockSize <= e.End; pos += stride)
            {
                if ((++done & 0x3FF) == 0)
                {
                    c.Cancel();
                    c.O.Progress?.Invoke("Scanning for misplaced data", Math.Min(1.0, (double)done / total));
                }

                if (InsideAny(skip, pos, p))
                {
                    continue;
                }

                c.Res.ScanReads++;
                try
                {
                    c.Dev.Read(pos, buf);
                }
                catch (IOException)
                {
                    continue;
                }

                var r = Pattern.Classify(buf, pos, c.Salt);
                if (r.State == BlockState.Changed)
                {
                    AddHit(c, pos, r.FoundOffset);
                }
            }
        }
    }

    private static void o_log(Ctx c, string s) => c.O.Log?.Invoke(s);

    private static bool InsideAny(List<long> sortedStarts, long pos, long length)
    {
        int i = sortedStarts.BinarySearch(pos);
        if (i < 0)
        {
            i = ~i - 1;
        }

        return i >= 0 && pos >= sortedStarts[i] && pos < sortedStarts[i] + length;
    }

    // ---- decision ----

    private static void Decide(Ctx c, List<long> plan)
    {
        var res = c.Res;
        long p = c.O.ProbeBytes;
        long tested = (long)res.Probes.Count * p + res.ScanReads * Pattern.BlockSize + c.FlushBytes * c.FillerUsed.Count;
        long usable = c.Dev.UsableExtents.Sum(e => e.Length);
        res.CoveragePercent = usable == 0 ? 0 : Math.Min(100.0, 100.0 * tested / usable);

        if (res.AliasHitCount > 0)
        {
            long gcd = c.Gcd;
            foreach (var h in res.AliasHits)
            {
                gcd = Gcd(gcd, Math.Abs(h.FoundOffset - h.ReadOffset));
            }

            long minDiff = res.AliasHits.Min(h => Math.Abs(h.FoundOffset - h.ReadOffset));
            long real = gcd >= Pattern.BlockSize * 256L ? gcd : minDiff;
            res.WrapAround = true;
            res.EstimatedRealSize = real;
            res.Verdict = Verdict.Fail;
            res.Summary = $"FAKE CAPACITY (wrap-around): data written at higher addresses reappeared at lower ones. " +
                          $"Claimed {Fmt(res.ClaimedSize)}, real capacity about {Fmt(real)}.";
            return;
        }

        var good = res.Probes.Where(x => x.IsGood).Select(x => x.Offset).ToList();
        var bad = res.Probes.Where(x => !x.IsGood).Select(x => x.Offset).ToList();
        if (bad.Count == 0)
        {
            res.Verdict = Verdict.Pass;
            res.Summary = $"No sign of fake capacity: {res.Probes.Count} probes across the card verified and no misplaced data " +
                          $"found in {res.ScanReads} scan reads. This is a sampled test; it cannot rule out scattered bad sectors.";
            return;
        }

        long firstBad = bad.Min();
        if (good.Any(g => g > firstBad))
        {
            res.Verdict = Verdict.Suspect;
            res.Summary = $"Failures are scattered: {bad.Count} of {res.Probes.Count} probes failed but good probes exist beyond the first failure. " +
                          "The card may have defective areas rather than a fake size. Run a full test.";
            return;
        }

        var below = good.Where(g => g < firstBad).ToList();
        if (below.Count == 0)
        {
            res.Verdict = Verdict.Fail;
            res.Summary = "Even the first probe failed: the card is not storing data correctly.";
            return;
        }

        long lo = below.Max() + p;
        long hi = firstBad;
        long resolution = p;
        o_log(c, $"Refining real size between {Fmt(lo)} and {Fmt(hi)}");
        while (hi - lo > resolution)
        {
            c.Cancel();
            long mid = (lo + hi) / 2 / p * p;
            if (mid + p > hi || !Fits(c.Dev, p, mid))
            {
                c.Res.Notes.Add("Real size refinement stopped early: no free space at the next test position.");
                break;
            }

            if (ProbeIsGood(c, mid))
            {
                lo = mid + p;
            }
            else
            {
                hi = mid;
            }
        }

        res.EstimatedRealSize = lo;
        res.Verdict = Verdict.Fail;
        res.Summary = $"FAKE CAPACITY: data beyond about {Fmt(lo)} is not stored. Claimed {Fmt(res.ClaimedSize)}, " +
                      $"real capacity about {Fmt(lo)} (accuracy +/- {Fmt(resolution)}).";
    }

    private static bool ProbeIsGood(Ctx c, long offset)
    {
        bool wrote = TryWrite(c, offset, c.O.ProbeBytes);
        DefeatCache(c, new[] { offset });
        return wrote && VerifyProbe(c, offset, false, false).IsGood;
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    /// <summary>Binary size with the decimal figure cards are sold by, e.g. "29.72 GiB (31.91 GB)".</summary>
    public static string Fmt(long bytes)
    {
        double gib = bytes / (double)(1L << 30);
        return gib >= 1
            ? $"{gib:0.##} GiB ({bytes / 1e9:0.##} GB)"
            : $"{bytes / (double)(1 << 20):0.##} MiB";
    }

    private sealed class Ctx(IBlockDevice dev, SizeProbeOptions o, ulong salt, Random rng, SizeResult res)
    {
        public IBlockDevice Dev { get; } = dev;

        public SizeProbeOptions O { get; } = o;

        public ulong Salt { get; } = salt;

        public Random Rng { get; } = rng;

        public SizeResult Res { get; } = res;

        public List<long> FillerUsed { get; } = new();

        public long FlushBytes { get; set; } = o.CacheDefeatBytes;

        public long Gcd { get; set; }

        public void Cancel() => O.Cancel.ThrowIfCancellationRequested();
    }
}
