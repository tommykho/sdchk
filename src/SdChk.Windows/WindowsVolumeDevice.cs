// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SdChk.Core;

namespace SdChk.Windows;

public sealed record VolumeInfo(string Letter, string Label, string Format, long TotalBytes, long FreeBytes, string DriveType, bool IsSystem);

/// <summary>
/// The free space of a mounted volume, written directly (below the file system) so probes land at known
/// physical offsets. UNVERIFIED ON REAL HARDWARE: run <c>sdchk inspect</c> first, it performs the same safety
/// checks read-only.
///
/// Safety: the volume is locked; only clusters the file system reports as free are ever written
/// (hard guard in <see cref="Write"/>); and before use the LCN-to-byte mapping is proven with a calibration file.
/// </summary>
public sealed class WindowsVolumeDevice : IBlockDevice
{
    private const string CalibrationName = "sdchk_calibration.tmp";

    private readonly SafeFileHandle _handle;
    private readonly string _calibrationPath;
    private readonly bool _readOnly;
    private readonly AlignedBuffer _scratch = new(1 << 20);

    private WindowsVolumeDevice(string letter, SafeFileHandle handle, long size, List<Extent> extents, string calibrationPath, string description, bool readOnly)
    {
        Letter = letter;
        _handle = handle;
        Size = size;
        UsableExtents = extents;
        _calibrationPath = calibrationPath;
        Description = description;
        _readOnly = readOnly;
    }

    public string Letter { get; }

    public string Description { get; }

    public long Size { get; }

    public IReadOnlyList<Extent> UsableExtents { get; }

    public IClock Clock { get; } = new StopwatchClock();

    public static IReadOnlyList<VolumeInfo> List()
    {
        var result = new List<VolumeInfo>();
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady)
            {
                continue;
            }

            result.Add(new VolumeInfo(d.Name[..2], d.VolumeLabel, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace, d.DriveType.ToString(),
                string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase)));
        }

        return result;
    }

    /// <param name="drive">Drive letter such as "F:".</param>
    /// <param name="readOnly">Inspect only: opens read-only and never writes to the volume.</param>
    /// <param name="allowFixed">Allow a drive Windows classes as fixed (some card readers do).</param>
    public static WindowsVolumeDevice Open(string drive, bool readOnly, bool allowFixed, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Raw volume access needs Windows.");
        }

        string letter = drive.Trim().TrimEnd('\\').ToUpperInvariant();
        if (letter.Length != 2 || letter[1] != ':' || !char.IsAsciiLetter(letter[0]))
        {
            throw new ArgumentException("Use a drive letter such as F:");
        }

        var info = List().FirstOrDefault(v => v.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"Drive {letter} not found or not ready.");
        if (info.IsSystem)
        {
            throw new InvalidOperationException("Refusing to test the system drive.");
        }

        if (info.DriveType != "Removable" && !allowFixed)
        {
            throw new InvalidOperationException($"Drive {letter} is reported as {info.DriveType}, not Removable. Pass --allow-fixed only if this is your card reader.");
        }

        string root = letter + "\\";
        if (!Native.GetDiskFreeSpaceW(root, out uint spc, out uint bps, out _, out uint totalClusters))
        {
            throw new IOException($"GetDiskFreeSpace failed ({Marshal.GetLastWin32Error()}).");
        }

        long cluster = (long)spc * bps;
        string calibPath = Path.Combine(root, CalibrationName);
        var (calibLcn, calibMarker) = CreateCalibrationFile(calibPath, cluster);
        SafeFileHandle? handle = null;
        try
        {
            uint access = readOnly ? Native.GenericRead : Native.GenericRead | Native.GenericWrite;
            handle = Native.CreateFileW($"\\\\.\\{letter}", access, Native.FileShareRead | Native.FileShareWrite, IntPtr.Zero,
                Native.OpenExisting, Native.FileFlagNoBuffering | Native.FileFlagWriteThrough, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new IOException($"Cannot open volume {letter} ({Marshal.GetLastWin32Error()}). Run as Administrator.");
            }

            if (!readOnly && !Ioctl(handle, Native.FsctlLockVolume))
            {
                throw new IOException("Cannot lock the volume. Close Explorer windows and programs using the card, then retry.");
            }

            long size = GetLength(handle);
            var bitmap = ReadBitmap(handle, totalClusters);
            if (!IsSet(bitmap, calibLcn))
            {
                throw new InvalidOperationException("Safety check failed: the volume bitmap does not mark the calibration file as used. No raw writes were made.");
            }

            var map = CalibrateMapping(handle, calibLcn, calibMarker, cluster, info.Format, size);
            var extents = BuildExtents(bitmap, totalClusters, cluster, map, size);
            log?.Invoke($"Volume {letter}: {info.Format}, cluster {cluster} B, mapping base {map.DataStart} shift {map.Shift}, {extents.Sum(e => e.Length) >> 20} MiB free in {extents.Count} ranges");
            string desc = $"Drive {letter} ({info.Label}, {info.Format}, {size >> 20} MiB volume, free space only)";
            var dev = new WindowsVolumeDevice(letter, handle, size, extents, calibPath, desc, readOnly);
            handle = null;
            return dev;
        }
        catch
        {
            handle?.Dispose();
            TryDelete(calibPath);
            throw;
        }
    }

    public void Read(long offset, Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = Math.Min(_scratch.Size, buffer.Length - done);
            var span = _scratch.Span[..n];
            int got = RandomAccess.Read(_handle, span, offset + done);
            if (got != n)
            {
                throw new IOException($"Short read at {offset + done}");
            }

            span.CopyTo(buffer[done..]);
            done += n;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (_readOnly)
        {
            throw new InvalidOperationException("Device opened read-only.");
        }

        if (!InsideFree(offset, buffer.Length))
        {
            throw new InvalidOperationException($"Refusing to write outside free space: {offset}+{buffer.Length}");
        }

        int done = 0;
        while (done < buffer.Length)
        {
            int n = Math.Min(_scratch.Size, buffer.Length - done);
            buffer.Slice(done, n).CopyTo(_scratch.Span);
            RandomAccess.Write(_handle, _scratch.Span[..n], offset + done);
            done += n;
        }
    }

    public void Flush() => Native.FlushFileBuffers(_handle);

    public void ResetCache() => Flush();

    public void Dispose()
    {
        _handle.Dispose();
        _scratch.Dispose();
        TryDelete(_calibrationPath);
    }

    private bool InsideFree(long offset, int length)
    {
        foreach (var e in UsableExtents)
        {
            if (offset >= e.Start && offset + length <= e.End)
            {
                return true;
            }
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool Ioctl(SafeFileHandle h, uint code) => Native.DeviceIoControl(h, code, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    private static long GetLength(SafeFileHandle h)
    {
        IntPtr buf = Marshal.AllocHGlobal(8);
        try
        {
            if (!Native.DeviceIoControl(h, Native.IoctlDiskGetLengthInfo, IntPtr.Zero, 0, buf, 8, out _, IntPtr.Zero))
            {
                throw new IOException($"Cannot read volume length ({Marshal.GetLastWin32Error()}).");
            }

            return Marshal.ReadInt64(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ---- calibration: prove how cluster numbers map to byte offsets before trusting the bitmap ----

    private static (long Lcn, byte[] Marker) CreateCalibrationFile(string path, long cluster)
    {
        long size = Math.Max(1L << 20, cluster) / cluster * cluster;
        var marker = new byte[512];
        Random.Shared.NextBytes(marker);
        Encoding.ASCII.GetBytes("SDCHK-CALIBRATION").CopyTo(marker, 0);

        using (var h = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.WriteThrough | (FileOptions)0x20000000))
        using (var buf = new AlignedBuffer((int)size))
        {
            buf.Span.Clear();
            marker.CopyTo(buf.Span);
            RandomAccess.Write(h, buf.Span, 0);
            Native.FlushFileBuffers(h);
        }

        using var r = Native.CreateFileW(path, Native.GenericRead, Native.FileShareRead | Native.FileShareWrite, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
        if (r.IsInvalid)
        {
            throw new IOException("Cannot reopen calibration file.");
        }

        IntPtr inBuf = Marshal.AllocHGlobal(8);
        IntPtr outBuf = Marshal.AllocHGlobal(4096);
        try
        {
            Marshal.WriteInt64(inBuf, 0);
            if (!Native.DeviceIoControl(r, Native.FsctlGetRetrievalPointers, inBuf, 8, outBuf, 4096, out _, IntPtr.Zero))
            {
                throw new IOException($"Cannot locate calibration file ({Marshal.GetLastWin32Error()}).");
            }

            // RETRIEVAL_POINTERS_BUFFER: DWORD ExtentCount, (pad), LARGE_INTEGER StartingVcn, then {NextVcn, Lcn}[]
            int count = Marshal.ReadInt32(outBuf);
            if (count < 1)
            {
                throw new IOException("Calibration file has no extents.");
            }

            long lcn = Marshal.ReadInt64(outBuf, 16 + 8);
            if (lcn < 0)
            {
                throw new IOException("Calibration file is not stored in clusters (compressed or resident).");
            }

            return (lcn, marker);
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    private readonly record struct Mapping(long DataStart, long Shift);

    private static Mapping CalibrateMapping(SafeFileHandle volume, long lcn, byte[] marker, long cluster, string format, long volumeSize)
    {
        var candidates = new List<Mapping> { new(0, 0) };
        long dataStart = ReadDataStart(volume);
        if (dataStart > 0)
        {
            candidates.Insert(0, new Mapping(dataStart, 0));
            candidates.Insert(1, new Mapping(dataStart, 2));
        }

        using var buf = new AlignedBuffer(4096);
        foreach (var m in candidates)
        {
            long at = m.DataStart + (lcn - m.Shift) * cluster;
            if (at < 0 || at + 4096 > volumeSize)
            {
                continue;
            }

            if (RandomAccess.Read(volume, buf.Span, at) == 4096 && buf.Span[..marker.Length].SequenceEqual(marker))
            {
                return m;
            }
        }

        throw new InvalidOperationException($"Safety check failed: could not prove where {format} clusters live on disk. No raw writes were made.");
    }

    /// <summary>Byte offset of the data area for FAT32/exFAT; 0 for NTFS or unknown (calibration decides).</summary>
    private static long ReadDataStart(SafeFileHandle volume)
    {
        using var buf = new AlignedBuffer(4096);
        if (RandomAccess.Read(volume, buf.Span, 0) < 512)
        {
            return 0;
        }

        var b = buf.Span;
        string oem = Encoding.ASCII.GetString(b.Slice(3, 8));
        if (oem.StartsWith("EXFAT", StringComparison.Ordinal))
        {
            long bytesPerSector = 1L << b[108];
            return BitConverter.ToUInt32(b.Slice(88, 4)) * bytesPerSector;
        }

        if (oem.StartsWith("NTFS", StringComparison.Ordinal))
        {
            return 0;
        }

        long bps = BitConverter.ToUInt16(b.Slice(11, 2));
        long reserved = BitConverter.ToUInt16(b.Slice(14, 2));
        long fats = b[16];
        long fatSize = BitConverter.ToUInt16(b.Slice(22, 2));
        if (fatSize == 0)
        {
            fatSize = BitConverter.ToUInt32(b.Slice(36, 4));
        }

        long rootSectors = (BitConverter.ToUInt16(b.Slice(17, 2)) * 32L + bps - 1) / Math.Max(1, bps);
        return bps == 0 ? 0 : (reserved + fats * fatSize + rootSectors) * bps;
    }

    // ---- free-space map ----

    private static byte[] ReadBitmap(SafeFileHandle h, uint totalClusters)
    {
        uint outSize = 16 + (totalClusters + 7) / 8 + 4096;
        IntPtr outBuf = Marshal.AllocHGlobal((int)outSize);
        IntPtr inBuf = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(inBuf, 0);
            if (!Native.DeviceIoControl(h, Native.FsctlGetVolumeBitmap, inBuf, 8, outBuf, outSize, out uint returned, IntPtr.Zero))
            {
                throw new IOException($"Cannot read the volume bitmap ({Marshal.GetLastWin32Error()}).");
            }

            long start = Marshal.ReadInt64(outBuf, 0);
            if (start != 0)
            {
                throw new IOException("Unexpected bitmap start.");
            }

            var data = new byte[returned - 16];
            Marshal.Copy(outBuf + 16, data, 0, data.Length);
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    private static bool IsSet(byte[] bitmap, long lcn) =>
        lcn / 8 < bitmap.Length && (bitmap[lcn / 8] & (1 << (int)(lcn % 8))) != 0;

    private static List<Extent> BuildExtents(byte[] bitmap, uint totalClusters, long cluster, Mapping map, long volumeSize)
    {
        var list = new List<Extent>();
        long runStart = -1;
        for (long i = 0; i <= totalClusters; i++)
        {
            bool free = i < totalClusters && !IsSet(bitmap, i);
            if (free && runStart < 0)
            {
                runStart = i;
            }
            else if (!free && runStart >= 0)
            {
                long start = map.DataStart + (runStart - map.Shift) * cluster;
                long end = map.DataStart + (i - map.Shift) * cluster;
                start = (start + 4095) / 4096 * 4096;
                end = Math.Min(end, volumeSize) / 4096 * 4096;
                if (end > start)
                {
                    list.Add(new Extent(start, end - start));
                }

                runStart = -1;
            }
        }

        return list;
    }
}
