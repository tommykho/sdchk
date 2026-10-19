// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SdChk.Core;

public sealed record JournalEntry(long Offset, long Length, string Sha256, long DataPosition)
{
    public Extent Extent => new(Offset, Length);
}

public sealed record RestoreFailure(long Offset, string Reason);

public sealed class RestoreResult
{
    public int Entries { get; set; }

    public long Bytes { get; set; }

    public List<RestoreFailure> Failures { get; } = new();

    public bool Ok => Failures.Count == 0;
}

public sealed record JournalHeader(string Tool, string Device, long DeviceSize, DateTime CreatedUtc);

/// <summary>
/// Undo log kept on the PC (never on the card under test). Before any block of the card is overwritten its original
/// content is saved here and flushed to disk; afterwards every block is written back and verified. A torn tail
/// (power loss while journaling) is ignored on reopen because its blocks had not been overwritten yet.
/// </summary>
public sealed class Journal : IDisposable
{
    private static readonly byte[] FileMagic = Encoding.ASCII.GetBytes("SDCHKJ01");
    private static readonly byte[] RecordMagic = Encoding.ASCII.GetBytes("REC1");
    private const int RecordHeaderBytes = 4 + 8 + 8 + 32;
    private const int Chunk = 1 << 20;

    private readonly FileStream _file;
    private readonly List<JournalEntry> _entries = new();

    private Journal(FileStream file, string path, JournalHeader header)
    {
        _file = file;
        Path = path;
        Header = header;
    }

    public string Path { get; }

    public JournalHeader Header { get; }

    public IReadOnlyList<JournalEntry> Entries => _entries;

    public long Bytes => _entries.Sum(e => e.Length);

    public static Journal Create(string path, string deviceDescription, long deviceSize)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.WriteThrough);
        var header = new JournalHeader("sdchk " + typeof(Journal).Assembly.GetName().Version, deviceDescription, deviceSize, DateTime.UtcNow);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(header);
        fs.Write(FileMagic);
        fs.Write(BitConverter.GetBytes(json.Length));
        fs.Write(json);
        fs.Flush(true);
        return new Journal(fs, path, header);
    }

    /// <summary>Open an existing journal (for recovery). Incomplete trailing records are dropped.</summary>
    public static Journal Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            var magic = new byte[FileMagic.Length];
            fs.ReadExactly(magic);
            if (!magic.AsSpan().SequenceEqual(FileMagic))
            {
                throw new IOException("Not an sdchk journal file.");
            }

            var len = new byte[4];
            fs.ReadExactly(len);
            var json = new byte[BitConverter.ToInt32(len)];
            fs.ReadExactly(json);
            var header = JsonSerializer.Deserialize<JournalHeader>(json) ?? throw new IOException("Bad journal header.");
            var journal = new Journal(fs, path, header);
            journal.ScanRecords();
            return journal;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private void ScanRecords()
    {
        var head = new byte[RecordHeaderBytes];
        while (_file.Position + RecordHeaderBytes <= _file.Length)
        {
            _file.ReadExactly(head);
            if (!head.AsSpan(0, 4).SequenceEqual(RecordMagic))
            {
                break;
            }

            long offset = BitConverter.ToInt64(head, 4);
            long length = BitConverter.ToInt64(head, 12);
            var hash = head.AsSpan(20, 32);
            long dataPos = _file.Position;
            if (length < 0 || dataPos + length > _file.Length || hash.IndexOfAnyExcept((byte)0) < 0)
            {
                break;
            }

            _entries.Add(new JournalEntry(offset, length, Convert.ToHexString(hash), dataPos));
            _file.Position = dataPos + length;
        }
    }

    /// <summary>Save the current content of every part of <paramref name="extents"/> not already in the journal, then flush to disk.</summary>
    public void Protect(IBlockDevice dev, IReadOnlyList<Extent> extents, CancellationToken cancel = default, Action<double>? progress = null)
    {
        var todo = new List<Extent>();
        foreach (var e in extents)
        {
            todo.AddRange(Extents.Subtract(e, _entries.Select(x => x.Extent)));
        }

        long total = todo.Sum(x => x.Length);
        long done = 0;
        var buf = new byte[Chunk];
        _file.Seek(0, SeekOrigin.End);
        foreach (var part in todo)
        {
            long headerPos = _file.Position;
            var head = new byte[RecordHeaderBytes];
            RecordMagic.CopyTo(head, 0);
            BitConverter.GetBytes(part.Start).CopyTo(head, 4);
            BitConverter.GetBytes(part.Length).CopyTo(head, 12);
            _file.Write(head);
            long dataPos = _file.Position;

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long pos = 0; pos < part.Length; pos += Chunk)
            {
                cancel.ThrowIfCancellationRequested();
                int n = (int)Math.Min(Chunk, part.Length - pos);
                try
                {
                    dev.Read(part.Start + pos, buf.AsSpan(0, n));
                }
                catch (IOException ex)
                {
                    throw new IOException($"Cannot save the original data at {part.Start + pos}; nothing was overwritten. {ex.Message}");
                }

                _file.Write(buf, 0, n);
                sha.AppendData(buf, 0, n);
                done += n;
                progress?.Invoke(total == 0 ? 1 : (double)done / total);
            }

            byte[] hash = sha.GetHashAndReset();
            _file.Position = headerPos + 20;
            _file.Write(hash);
            _file.Flush(true);
            _file.Seek(0, SeekOrigin.End);
            _entries.Add(new JournalEntry(part.Start, part.Length, Convert.ToHexString(hash), dataPos));
        }
    }

    /// <summary>Write every saved block back (newest first), flush, and verify each against the journal.</summary>
    public RestoreResult Restore(IBlockDevice dev, Action<JournalEntry>? entryDone = null, Action<double>? progress = null)
    {
        var result = new RestoreResult();
        long total = Math.Max(1, _entries.Sum(e => e.Length) * 2);
        long done = 0;
        var buf = new byte[Chunk];
        var back = new byte[Chunk];

        foreach (var entry in _entries.AsEnumerable().Reverse())
        {
            result.Entries++;
            if (!JournalDataIsIntact(entry, buf))
            {
                result.Failures.Add(new RestoreFailure(entry.Offset, "Journal data is corrupt; block NOT restored."));
                continue;
            }

            string? writeError = null;
            for (long pos = 0; pos < entry.Length; pos += Chunk)
            {
                int n = (int)Math.Min(Chunk, entry.Length - pos);
                _file.Position = entry.DataPosition + pos;
                _file.ReadExactly(buf, 0, n);
                try
                {
                    dev.Write(entry.Offset + pos, buf.AsSpan(0, n));
                }
                catch (IOException ex)
                {
                    writeError = ex.Message;
                }

                done += n;
                progress?.Invoke((double)done / total);
            }

            try
            {
                dev.Flush();
            }
            catch (IOException)
            {
            }

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            bool readable = true;
            for (long pos = 0; pos < entry.Length; pos += Chunk)
            {
                int n = (int)Math.Min(Chunk, entry.Length - pos);
                try
                {
                    dev.Read(entry.Offset + pos, back.AsSpan(0, n));
                    sha.AppendData(back, 0, n);
                }
                catch (IOException)
                {
                    readable = false;
                    break;
                }

                done += n;
                progress?.Invoke((double)done / total);
            }

            if (!readable || Convert.ToHexString(sha.GetHashAndReset()) != entry.Sha256)
            {
                result.Failures.Add(new RestoreFailure(entry.Offset, writeError ?? "Read-back after restore does not match the original."));
            }
            else
            {
                result.Bytes += entry.Length;
                entryDone?.Invoke(entry);
            }
        }

        return result;
    }

    private bool JournalDataIsIntact(JournalEntry entry, byte[] buf)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _file.Position = entry.DataPosition;
        for (long pos = 0; pos < entry.Length; pos += Chunk)
        {
            int n = (int)Math.Min(Chunk, entry.Length - pos);
            _file.ReadExactly(buf, 0, n);
            sha.AppendData(buf, 0, n);
        }

        return Convert.ToHexString(sha.GetHashAndReset()) == entry.Sha256;
    }

    public void Dispose() => _file.Dispose();
}
