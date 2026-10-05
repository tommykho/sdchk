// SPDX-License-Identifier: GPL-3.0-or-later
namespace SdChk.Core;

public sealed record SpeedClassInfo(string Name, double MinWriteMBps);

public static class SpeedClasses
{
    public static readonly IReadOnlyList<SpeedClassInfo> All = new SpeedClassInfo[]
    {
        new("C2", 2), new("C4", 4), new("C6", 6), new("C10", 10),
        new("U1", 10), new("U3", 30),
        new("V6", 6), new("V10", 10), new("V30", 30), new("V60", 60), new("V90", 90),
    };

    public static SpeedClassInfo? Find(string? name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed record SpeedTestOptions
{
    /// <summary>Contiguous bytes written (then read) at each of the start, middle and end positions.</summary>
    public long SequentialBytes { get; init; } = 1L << 30;

    public int IoBytes { get; init; } = 1 << 20;

    /// <summary>Device-time length of one speed sample (1 s for real cards; shorter for scaled simulations).</summary>
    public double SampleSeconds { get; init; } = 1.0;

    public int RandomOps { get; init; } = 1000;

    public long RandomWindowBytes { get; init; } = 256L << 20;

    public string? ClaimedClass { get; init; }

    public ulong? Salt { get; init; }

    public Action<string>? Log { get; init; }

    public Action<string, double>? Progress { get; init; }

    public CancellationToken Cancel { get; init; }
}

public sealed record SpeedStats(double Min, double P5, double Median, double Mean, double Max, int Samples);

public sealed class PositionSpeed
{
    public string Label { get; set; } = "";

    public long Offset { get; set; }

    public long Bytes { get; set; }

    public SpeedStats Write { get; set; } = new(0, 0, 0, 0, 0, 0);

    public SpeedStats Read { get; set; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>Write MB/s over time, one sample per ~second of device time.</summary>
    public List<double> WriteSeries { get; } = new();

    public long? DropAfterBytes { get; set; }

    public int BadBlocksOnReadBack { get; set; }
}

public sealed class RandomSpeed
{
    public double WriteIops { get; set; }

    public double ReadIops { get; set; }

    public double WriteP95Ms { get; set; }

    public double ReadP95Ms { get; set; }

    public int Ops { get; set; }
}

public sealed class SpeedResult
{
    public List<PositionSpeed> Positions { get; } = new();

    public RandomSpeed? Random { get; set; }

    public string? ClaimedClass { get; set; }

    public double? ClassMinWriteMBps { get; set; }

    public Verdict Verdict { get; set; } = Verdict.Inconclusive;

    public string Summary { get; set; } = "";

    public bool? MeetsA1 { get; set; }

    public bool? MeetsA2 { get; set; }

    public bool Cancelled { get; set; }

    public long SequentialBytesPerPosition { get; set; }

    public List<string> Notes { get; } = new();
}

/// <summary>
/// Sustained sequential write/read at the start, middle and end of the card plus 4 KiB random I/O at queue depth 1.
/// Grades on sustained minimum speed (what speed classes promise), not on peak.
/// </summary>
public static class SpeedTest
{
    public static SpeedResult Run(IBlockDevice dev, SpeedTestOptions o)
    {
        var res = new SpeedResult { ClaimedClass = o.ClaimedClass, SequentialBytesPerPosition = o.SequentialBytes };
        ulong salt = o.Salt ?? (ulong)Random.Shared.NextInt64();
        var cls = SpeedClasses.Find(o.ClaimedClass);
        res.ClassMinWriteMBps = cls?.MinWriteMBps;

        try
        {
            string[] labels = { "start", "middle", "end" };
            double[] fractions = { 0, 0.5, 1 };
            var used = new List<Extent>();
            for (int i = 0; i < labels.Length; i++)
            {
                long? at = Place(dev, o.SequentialBytes, fractions[i], used);
                if (at is null)
                {
                    res.Notes.Add($"No contiguous free space of {o.SequentialBytes >> 20} MiB for the {labels[i]} position; skipped.");
                    continue;
                }

                used.Add(new Extent(at.Value, o.SequentialBytes));
                res.Positions.Add(RunSequential(dev, o, labels[i], at.Value, salt, res));
            }

            if (res.Positions.Count > 0)
            {
                res.Random = RunRandom(dev, o, salt, used);
            }
        }
        catch (OperationCanceledException)
        {
            res.Cancelled = true;
            res.Summary = "Stopped by user before the speed test finished.";
            return res;
        }

        Grade(res, o);
        return res;
    }

    private static long? Place(IBlockDevice dev, long bytes, double fraction, List<Extent> used)
    {
        long target = (long)(fraction * Math.Max(0, dev.Size - bytes)) / (1 << 20) * (1 << 20);
        long? best = null;
        long bestDist = long.MaxValue;
        foreach (var e in dev.UsableExtents)
        {
            long lo = (e.Start + (1 << 20) - 1) / (1 << 20) * (1 << 20);
            long hi = (e.End - bytes) / (1 << 20) * (1 << 20);
            if (lo > hi)
            {
                continue;
            }

            long cand = Math.Clamp(target, lo, hi);
            if (used.Any(u => cand < u.End && cand + bytes > u.Start))
            {
                continue;
            }

            long dist = Math.Abs(cand - target);
            if (dist < bestDist)
            {
                best = cand;
                bestDist = dist;
            }
        }

        return best;
    }

    private static PositionSpeed RunSequential(IBlockDevice dev, SpeedTestOptions o, string label, long offset, ulong salt, SpeedResult res)
    {
        o.Log?.Invoke($"Speed: sequential {label} at {offset >> 20} MiB, {o.SequentialBytes >> 20} MiB");
        var pos = new PositionSpeed { Label = label, Offset = offset, Bytes = o.SequentialBytes };
        var buf = new byte[o.IoBytes];
        var clock = dev.Clock;

        dev.ResetCache();
        var samples = new List<double>();
        double bucketTime = 0;
        long bucketBytes = 0;
        long done = 0;
        var cumulative = new List<long>();
        double totalTime = 0;
        for (long at = 0; at < o.SequentialBytes; at += o.IoBytes)
        {
            o.Cancel.ThrowIfCancellationRequested();
            int n = (int)Math.Min(o.IoBytes, o.SequentialBytes - at);
            Pattern.Fill(buf.AsSpan(0, n), offset + at, salt);
            double t = clock.Now;
            dev.Write(offset + at, buf.AsSpan(0, n));
            double dt = clock.Now - t;
            totalTime += dt;
            bucketTime += dt;
            bucketBytes += n;
            done += n;
            if (bucketTime >= o.SampleSeconds)
            {
                samples.Add(bucketBytes / bucketTime / 1e6);
                cumulative.Add(done);
                bucketTime = 0;
                bucketBytes = 0;
            }

            o.Progress?.Invoke($"Writing {label}", (double)done / o.SequentialBytes);
        }

        double tf = clock.Now;
        dev.Flush();
        totalTime += clock.Now - tf;
        if (bucketTime > o.SampleSeconds * 0.2)
        {
            samples.Add(bucketBytes / bucketTime / 1e6);
            cumulative.Add(done);
        }

        pos.WriteSeries.AddRange(samples);
        pos.Write = Stats(samples, o.SequentialBytes / Math.Max(totalTime, 1e-9) / 1e6);
        pos.DropAfterBytes = FindDrop(samples, cumulative);

        dev.ResetCache();
        var rsamples = new List<double>();
        double rTime = 0, rBytes = 0, rTotalTime = 0;
        int bad = 0;
        for (long at = 0; at < o.SequentialBytes; at += o.IoBytes)
        {
            o.Cancel.ThrowIfCancellationRequested();
            int n = (int)Math.Min(o.IoBytes, o.SequentialBytes - at);
            double t = clock.Now;
            try
            {
                dev.Read(offset + at, buf.AsSpan(0, n));
            }
            catch (IOException)
            {
                bad += n / Pattern.BlockSize;
                continue;
            }

            double dt = clock.Now - t;
            rTotalTime += dt;
            rTime += dt;
            rBytes += n;
            if (rTime >= o.SampleSeconds)
            {
                rsamples.Add(rBytes / rTime / 1e6);
                rTime = 0;
                rBytes = 0;
            }

            for (int b = 0; b < n; b += Pattern.BlockSize)
            {
                if (Pattern.Classify(buf.AsSpan(b, Pattern.BlockSize), offset + at + b, salt).State != BlockState.Good)
                {
                    bad++;
                }
            }

            o.Progress?.Invoke($"Reading {label}", (double)(at + n) / o.SequentialBytes);
        }

        if (rTime > o.SampleSeconds * 0.2)
        {
            rsamples.Add(rBytes / rTime / 1e6);
        }

        pos.Read = Stats(rsamples, o.SequentialBytes / Math.Max(rTotalTime, 1e-9) / 1e6);
        pos.BadBlocksOnReadBack = bad;
        if (bad > 0)
        {
            res.Notes.Add($"{bad} blocks read back wrong at the {label} position (integrity problem).");
        }

        return pos;
    }

    private static RandomSpeed RunRandom(IBlockDevice dev, SpeedTestOptions o, ulong salt, List<Extent> used)
    {
        long window = Math.Min(o.RandomWindowBytes, o.SequentialBytes);
        var seq = used[Math.Min(1, used.Count - 1)];
        long start = seq.Start;
        long blocks = window / Pattern.BlockSize;
        var rng = new Random((int)(salt & 0x7FFFFFFF));
        var order = new long[o.RandomOps];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = start + rng.NextInt64(blocks) * Pattern.BlockSize;
        }

        o.Log?.Invoke($"Speed: {o.RandomOps} random 4 KiB writes and reads in a {window >> 20} MiB window");
        var buf = new byte[Pattern.BlockSize];
        var wLat = new List<double>();
        foreach (long at in order)
        {
            o.Cancel.ThrowIfCancellationRequested();
            Pattern.FillBlock(buf, at, salt ^ 0x5A5A);
            double t = dev.Clock.Now;
            dev.Write(at, buf);
            wLat.Add(dev.Clock.Now - t);
        }

        dev.Flush();
        dev.ResetCache();
        var rLat = new List<double>();
        foreach (long at in order)
        {
            o.Cancel.ThrowIfCancellationRequested();
            double t = dev.Clock.Now;
            dev.Read(at, buf);
            rLat.Add(dev.Clock.Now - t);
        }

        return new RandomSpeed
        {
            Ops = o.RandomOps,
            WriteIops = wLat.Count / Math.Max(wLat.Sum(), 1e-9),
            ReadIops = rLat.Count / Math.Max(rLat.Sum(), 1e-9),
            WriteP95Ms = Percentile(wLat, 95) * 1000,
            ReadP95Ms = Percentile(rLat, 95) * 1000,
        };
    }

    internal static SpeedStats Stats(List<double> samples, double mean)
    {
        if (samples.Count == 0)
        {
            return new SpeedStats(mean, mean, mean, mean, mean, 0);
        }

        return new SpeedStats(samples.Min(), Percentile(samples, 5), Percentile(samples, 50), mean, samples.Max(), samples.Count);
    }

    internal static double Percentile(List<double> values, double pct)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var s = values.OrderBy(x => x).ToList();
        double rank = pct / 100.0 * (s.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        return s[lo] + (s[hi] - s[lo]) * (rank - lo);
    }

    /// <summary>Bytes written when speed first falls below 60% of the opening speed (write-cache exhaustion).</summary>
    internal static long? FindDrop(List<double> samples, List<long> cumulative)
    {
        if (samples.Count < 8)
        {
            return null;
        }

        double baseline = Percentile(samples.Take(3).ToList(), 50);
        for (int i = 3; i + 2 < samples.Count; i++)
        {
            if (Percentile(samples.Skip(i).Take(3).ToList(), 50) < 0.6 * baseline)
            {
                return cumulative[i];
            }
        }

        return null;
    }

    private static void Grade(SpeedResult res, SpeedTestOptions o)
    {
        if (res.Positions.Count == 0)
        {
            res.Verdict = Verdict.Inconclusive;
            res.Summary = "No speed measurement possible (no contiguous free space).";
            return;
        }

        double p5 = res.Positions.Min(x => x.Write.P5);
        double median = res.Positions.Min(x => x.Write.Median);
        if (res.Random is { } r)
        {
            res.MeetsA1 = r.ReadIops >= 1500 && r.WriteIops >= 500;
            res.MeetsA2 = r.ReadIops >= 4000 && r.WriteIops >= 2000;
        }

        if (o.SequentialBytes < (1L << 30))
        {
            res.Notes.Add("Short sequential run (< 1 GiB): the card's fast write cache may hide its real sustained speed. Use --seq-mib 4096 for a trustworthy grade.");
        }

        if (res.ClassMinWriteMBps is not { } min)
        {
            res.Verdict = Verdict.Inconclusive;
            res.Summary = $"Sustained write: median {median:0.0} MB/s, 5th percentile {p5:0.0} MB/s. No speed class given to grade against.";
            return;
        }

        if (p5 >= min)
        {
            res.Verdict = Verdict.Pass;
            res.Summary = $"Meets {res.ClaimedClass} (min {min:0} MB/s sustained write): 5th percentile {p5:0.0} MB/s, median {median:0.0} MB/s.";
        }
        else if (median >= min)
        {
            res.Verdict = Verdict.Suspect;
            res.Summary = $"Borderline for {res.ClaimedClass} (min {min:0} MB/s): median {median:0.0} MB/s but 5th percentile only {p5:0.0} MB/s (dips below the class minimum).";
        }
        else
        {
            res.Verdict = Verdict.Fail;
            res.Summary = $"Too slow for {res.ClaimedClass} (min {min:0} MB/s sustained write): median {median:0.0} MB/s, 5th percentile {p5:0.0} MB/s.";
        }
    }
}
