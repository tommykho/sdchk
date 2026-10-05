// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32.SafeHandles;

namespace SdChk.Core.Devices;

/// <summary>A disk image file treated as a card. Lets the engine run on any OS without hardware.</summary>
public sealed class FileBlockDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;

    public FileBlockDevice(string path, long size)
    {
        if (size <= 0 || size % Pattern.BlockSize != 0)
        {
            throw new ArgumentException("Size must be a positive multiple of 4096.", nameof(size));
        }

        _handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough);
        if (RandomAccess.GetLength(_handle) < size)
        {
            RandomAccess.SetLength(_handle, size);
        }

        Description = $"Image file {path}";
        Size = size;
        UsableExtents = new[] { new Extent(0, size) };
    }

    public string Description { get; }

    public long Size { get; }

    public IReadOnlyList<Extent> UsableExtents { get; }

    public IClock Clock { get; } = new StopwatchClock();

    public void Read(long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(_handle, buffer[total..], offset + total);
            if (n <= 0)
            {
                throw new IOException($"Short read at {offset + total}");
            }

            total += n;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer) => RandomAccess.Write(_handle, buffer, offset);

    public void Flush() => RandomAccess.FlushToDisk(_handle);

    public void ResetCache() => Flush();

    public void Dispose() => _handle.Dispose();
}
