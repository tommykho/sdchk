// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SdChk.Windows;

internal static class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 1;
    public const uint FileShareWrite = 2;
    public const uint OpenExisting = 3;
    public const uint FileFlagNoBuffering = 0x20000000;
    public const uint FileFlagWriteThrough = 0x80000000;

    public const uint FsctlLockVolume = 0x00090018;
    public const uint FsctlGetVolumeBitmap = 0x0009006F;
    public const uint FsctlGetRetrievalPointers = 0x00090073;
    public const uint IoctlDiskGetLengthInfo = 0x0007405C;

    public const int ErrorMoreData = 234;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr inBuffer, uint inSize, IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlushFileBuffers(SafeFileHandle handle);
}

/// <summary>Sector-aligned native buffer, required for FILE_FLAG_NO_BUFFERING I/O.</summary>
internal sealed unsafe class AlignedBuffer : IDisposable
{
    private readonly void* _ptr;

    public AlignedBuffer(int size)
    {
        Size = size;
        _ptr = NativeMemory.AlignedAlloc((nuint)size, 4096);
    }

    public int Size { get; }

    public Span<byte> Span => new(_ptr, Size);

    public void Dispose() => NativeMemory.AlignedFree(_ptr);
}
