// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace SdChk.Core;

public enum BlockState
{
    Good,
    /// <summary>Content is ours but was written for a different offset (wrap-around evidence).</summary>
    Changed,
    Corrupted,
    /// <summary>All 0x00 or all 0xFF.</summary>
    Blank,
    Unreadable,
}

public readonly record struct BlockResult(BlockState State, long FoundOffset);

/// <summary>
/// Per-run unique test data. Word 0 of every 4 KiB block is its own (unsalted) offset, so data that
/// comes back from another place identifies where it was written for; the rest is salted noise that
/// the card cannot guess.
/// </summary>
public static class Pattern
{
    public const int BlockSize = 4096;
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public static void FillBlock(Span<byte> block, long offset, ulong salt)
    {
        var words = MemoryMarshal.Cast<byte, ulong>(block);
        words[0] = (ulong)offset;
        ulong s = Mix(salt ^ (ulong)offset);
        for (int i = 1; i < words.Length; i++)
        {
            s += Gamma;
            words[i] = Mix(s);
        }
    }

    public static void Fill(Span<byte> buffer, long startOffset, ulong salt)
    {
        for (int i = 0; i < buffer.Length; i += BlockSize)
        {
            FillBlock(buffer.Slice(i, BlockSize), startOffset + i, salt);
        }
    }

    public static BlockResult Classify(ReadOnlySpan<byte> block, long expectedOffset, ulong salt)
    {
        if (IsBlank(block))
        {
            return new BlockResult(BlockState.Blank, -1);
        }

        var words = MemoryMarshal.Cast<byte, ulong>(block);
        long header = (long)words[0];
        Span<byte> scratch = stackalloc byte[BlockSize];

        if (header == expectedOffset)
        {
            FillBlock(scratch, expectedOffset, salt);
            return block.SequenceEqual(scratch)
                ? new BlockResult(BlockState.Good, expectedOffset)
                : new BlockResult(BlockState.Corrupted, -1);
        }

        if (header >= 0 && header % BlockSize == 0)
        {
            FillBlock(scratch, header, salt);
            if (block.SequenceEqual(scratch))
            {
                return new BlockResult(BlockState.Changed, header);
            }
        }

        return new BlockResult(BlockState.Corrupted, -1);
    }

    private static bool IsBlank(ReadOnlySpan<byte> block) =>
        block.IndexOfAnyExcept((byte)0) < 0 || block.IndexOfAnyExcept((byte)0xFF) < 0;
}
