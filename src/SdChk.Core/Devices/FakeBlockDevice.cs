// SPDX-License-Identifier: GPL-3.0-or-later
namespace SdChk.Core.Devices;

public enum FakeBehavior
{
    /// <summary>Real size equals claimed size.</summary>
    Genuine,
    /// <summary>Address X is stored at X mod RealSize.</summary>
    Wrap,
    /// <summary>Writes beyond RealSize are dropped; reads return zeros.</summary>
    Discard,
    /// <summary>Writes beyond RealSize are dropped; reads return noise.</summary>
    Garbage,
    /// <summary>Writes beyond RealSize fail with an I/O error.</summary>
    WriteError,
}

public sealed record FakeCardOptions
{
    public long ClaimedSize { get; init; } = 512L << 30;

    public long RealSize { get; init; } = 32L << 30;

    public FakeBehavior Behavior { get; init; } = FakeBehavior.Wrap;

    /// <summary>Recent writes are served from this cache even if the flash dropped them (models cheating controllers).</summary>
    public long WriteCacheBytes { get; init; }

    /// <summary>Physical ranges that return corrupted data.</summary>
    public IReadOnlyList<Extent> BadRanges { get; init; } = Array.Empty<Extent>();

    public double FastWriteMBps { get; init; } = 90;

    public double SlowWriteMBps { get; init; } = 90;

    /// <summary>Sequential bytes written after which write speed falls to <see cref="SlowWriteMBps"/>.</summary>
    public long SlowdownAfterBytes { get; init; } = long.MaxValue;

    public double ReadMBps { get; init; } = 95;

    public double RandomWriteIops { get; init; } = 600;

    public double RandomReadIops { get; init; } = 2000;

    public IReadOnlyList<Extent>? UsableExtents { get; init; }
}

/// <summary>Sparse in-memory card with configurable fake behaviour and a virtual clock.</summary>
public sealed class FakeBlockDevice : IBlockDevice
{
    private const int SequentialThreshold = 128 * 1024;

    private readonly FakeCardOptions _o;
    private readonly long _real;
    private readonly Dictionary<long, byte[]> _store = new();
    private readonly Dictionary<long, (byte[] Data, long Seq)> _cache = new();
    private readonly Queue<(long Index, long Seq)> _cacheOrder = new();
    private long _cacheSeq;
    private readonly VirtualClock _clock = new();
    private long _sequentialWritten;

    public FakeBlockDevice(FakeCardOptions options)
    {
        _o = options;
        _real = options.Behavior == FakeBehavior.Genuine ? options.ClaimedSize : options.RealSize / Pattern.BlockSize * Pattern.BlockSize;
        Size = options.ClaimedSize;
        UsableExtents = options.UsableExtents ?? new[] { new Extent(0, Size) };
        Description = $"Simulated card claiming {SizeProbe.Fmt(Size)}, really {SizeProbe.Fmt(_real)} ({options.Behavior})";
    }

    public string Description { get; }

    public long Size { get; }

    public IReadOnlyList<Extent> UsableExtents { get; }

    public IClock Clock => _clock;

    public long RealSize => _real;

    public void Read(long offset, Span<byte> buffer)
    {
        Check(offset, buffer.Length);
        for (int i = 0; i < buffer.Length; i += Pattern.BlockSize)
        {
            ReadBlock(offset + i, buffer.Slice(i, Pattern.BlockSize));
        }

        _clock.Advance(buffer.Length >= SequentialThreshold
            ? buffer.Length / (_o.ReadMBps * 1e6)
            : buffer.Length / (double)Pattern.BlockSize / _o.RandomReadIops);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        Check(offset, buffer.Length);
        for (int i = 0; i < buffer.Length; i += Pattern.BlockSize)
        {
            WriteBlock(offset + i, buffer.Slice(i, Pattern.BlockSize));
        }

        if (buffer.Length >= SequentialThreshold)
        {
            double mbps = _sequentialWritten >= _o.SlowdownAfterBytes ? _o.SlowWriteMBps : _o.FastWriteMBps;
            _sequentialWritten += buffer.Length;
            _clock.Advance(buffer.Length / (mbps * 1e6));
        }
        else
        {
            _clock.Advance(buffer.Length / (double)Pattern.BlockSize / _o.RandomWriteIops);
        }
    }

    public void Flush()
    {
    }

    /// <summary>Host-side reset only. The card's own cache is not affected; only writing more data pushes it out.</summary>
    public void ResetCache() => _sequentialWritten = 0;

    public void Dispose()
    {
    }

    private void Check(long offset, int length)
    {
        if (offset < 0 || offset + length > Size || offset % Pattern.BlockSize != 0 || length % Pattern.BlockSize != 0)
        {
            throw new IOException($"Invalid I/O range {offset}+{length}");
        }
    }

    private void ReadBlock(long offset, Span<byte> block)
    {
        long index = offset / Pattern.BlockSize;
        if (_cache.TryGetValue(index, out var cached))
        {
            cached.Data.CopyTo(block);
            return;
        }

        if (offset >= _real && _o.Behavior != FakeBehavior.Wrap)
        {
            if (_o.Behavior == FakeBehavior.Garbage)
            {
                Pattern.FillBlock(block, -1 - index, 0xDEADBEEFUL);
            }
            else
            {
                block.Clear();
            }

            return;
        }

        long phys = _o.Behavior == FakeBehavior.Wrap ? offset % _real : offset;
        if (_store.TryGetValue(phys / Pattern.BlockSize, out var data))
        {
            data.CopyTo(block);
        }
        else
        {
            block.Clear();
        }

        if (IsBad(phys))
        {
            block[7] ^= 0x5A;
        }
    }

    private void WriteBlock(long offset, ReadOnlySpan<byte> block)
    {
        long index = offset / Pattern.BlockSize;
        if (offset >= _real && _o.Behavior == FakeBehavior.WriteError)
        {
            throw new IOException($"Simulated write failure at {offset}");
        }

        if (_o.WriteCacheBytes > 0)
        {
            long seq = ++_cacheSeq;
            _cache[index] = (block.ToArray(), seq);
            _cacheOrder.Enqueue((index, seq));
            while (_cacheOrder.Count * (long)Pattern.BlockSize > _o.WriteCacheBytes)
            {
                var old = _cacheOrder.Dequeue();
                if (_cache.TryGetValue(old.Index, out var entry) && entry.Seq == old.Seq)
                {
                    _cache.Remove(old.Index);
                }
            }
        }

        if (offset >= _real && _o.Behavior != FakeBehavior.Wrap)
        {
            return;
        }

        long phys = _o.Behavior == FakeBehavior.Wrap ? offset % _real : offset;
        _store[phys / Pattern.BlockSize] = block.ToArray();
    }

    private bool IsBad(long phys)
    {
        foreach (var r in _o.BadRanges)
        {
            if (phys >= r.Start && phys < r.End)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class VirtualClock : IClock
    {
        public double Now { get; private set; }

        public void Advance(double seconds) => Now += seconds;
    }
}
