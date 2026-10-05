// SPDX-License-Identifier: GPL-3.0-or-later
namespace SdChk.Core;

/// <summary>A byte range of the device that is safe to write test data to (unallocated space).</summary>
public readonly record struct Extent(long Start, long Length)
{
    public long End => Start + Length;
}

public interface IClock
{
    /// <summary>Seconds on a monotonic clock. Fake devices return virtual time.</summary>
    double Now { get; }
}

public sealed class StopwatchClock : IClock
{
    private static readonly long Origin = System.Diagnostics.Stopwatch.GetTimestamp();

    public double Now => System.Diagnostics.Stopwatch.GetElapsedTime(Origin).TotalSeconds;
}

/// <summary>
/// Block-addressed storage under test. Offsets and lengths passed to Read/Write are multiples of
/// <see cref="Pattern.BlockSize"/>. Failures are reported with <see cref="IOException"/>.
/// </summary>
public interface IBlockDevice : IDisposable
{
    string Description { get; }

    /// <summary>Capacity the device claims, in bytes.</summary>
    long Size { get; }

    /// <summary>Sorted, non-overlapping ranges that may be written (free space). A new card is one extent.</summary>
    IReadOnlyList<Extent> UsableExtents { get; }

    IClock Clock { get; }

    void Read(long offset, Span<byte> buffer);

    void Write(long offset, ReadOnlySpan<byte> buffer);

    void Flush();

    /// <summary>Drop host-side caches between the write and verify phases (flush + reopen where possible).</summary>
    void ResetCache();
}
