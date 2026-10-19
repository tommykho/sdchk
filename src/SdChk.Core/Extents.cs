// SPDX-License-Identifier: GPL-3.0-or-later
namespace SdChk.Core;

public static class Extents
{
    public static long Sum(IEnumerable<Extent> extents) => extents.Sum(e => e.Length);

    public static bool Overlaps(Extent a, Extent b) => a.Start < b.End && b.Start < a.End;

    /// <summary>Free ranges of [0, size) not covered by <paramref name="used"/> (which may be unsorted and overlapping).</summary>
    public static List<Extent> Complement(long size, IEnumerable<Extent> used)
    {
        var result = new List<Extent>();
        long cursor = 0;
        foreach (var u in used.OrderBy(x => x.Start))
        {
            if (u.Start > cursor)
            {
                result.Add(new Extent(cursor, Math.Min(u.Start, size) - cursor));
            }

            cursor = Math.Max(cursor, u.End);
            if (cursor >= size)
            {
                break;
            }
        }

        if (cursor < size)
        {
            result.Add(new Extent(cursor, size - cursor));
        }

        return result.Where(e => e.Length > 0).ToList();
    }

    /// <summary>The parts of <paramref name="target"/> not covered by <paramref name="covered"/>.</summary>
    public static List<Extent> Subtract(Extent target, IEnumerable<Extent> covered)
    {
        var result = new List<Extent>();
        long cursor = target.Start;
        foreach (var c in covered.Where(c => Overlaps(c, target)).OrderBy(c => c.Start))
        {
            if (c.Start > cursor)
            {
                result.Add(new Extent(cursor, c.Start - cursor));
            }

            cursor = Math.Max(cursor, c.End);
        }

        if (cursor < target.End)
        {
            result.Add(new Extent(cursor, target.End - cursor));
        }

        return result;
    }
}
