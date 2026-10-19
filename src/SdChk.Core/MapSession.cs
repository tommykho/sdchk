// SPDX-License-Identifier: GPL-3.0-or-later
namespace SdChk.Core;

public enum SessionStage
{
    Idle,
    Map,
    FreeSpace,
    Journal,
    Write,
    Validate,
    Restore,
    Done,
    Cancelled,
    Failed,
}

/// <summary>Outcome of the areas in one map cell, ordered by severity.</summary>
public enum CellStatus : byte
{
    None,
    Planned,
    Journaled,
    Written,
    Verified,
    Changed,
    Failed,
}

public sealed record MapLayout(long Size, long CellBytes, int Cols, int Rows)
{
    public int Cells => (int)((Size + CellBytes - 1) / CellBytes);

    public static MapLayout For(long size)
    {
        long cell = 16L << 20;
        while ((size + cell - 1) / cell > 4096)
        {
            cell <<= 1;
        }

        int cells = (int)((size + cell - 1) / cell);
        int cols = Math.Min(64, cells);
        return new MapLayout(size, cell, cols, (cells + cols - 1) / cols);
    }
}

public sealed class MapSessionOptions
{
    public SizeProbeOptions Probe { get; init; } = new();

    /// <summary>Where the undo journal lives. Must not be on the card being tested.</summary>
    public string JournalDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "sdchk-journals");

    /// <summary>Root of the tested drive (for example "F:\"); the journal is refused if it would be stored there.</summary>
    public string? DeviceRoot { get; init; }

    /// <summary>Pause before each step until <see cref="MapSession.Continue"/> is called.</summary>
    public bool StepMode { get; init; }

    /// <summary>Slow motion for simulations: pause after every area so the map visibly fills.</summary>
    public int AreaDelayMs { get; init; }

    public bool KeepJournal { get; init; }
}

public sealed class SessionSnapshot
{
    public long Version { get; init; }

    public SessionStage Stage { get; init; }

    public bool Waiting { get; init; }

    public string? NextStep { get; init; }

    public string Message { get; init; } = "";

    public string ProgressLabel { get; init; } = "";

    public double Progress { get; init; }

    public MapLayout Layout { get; init; } = new(0, 1, 0, 0);

    public byte[] FreePct { get; init; } = Array.Empty<byte>();

    public byte[] Status { get; init; } = Array.Empty<byte>();

    public byte[] Restored { get; init; } = Array.Empty<byte>();

    public bool FreeKnown { get; init; }

    public long FreeBytes { get; init; }

    public long UsedBytes { get; init; }

    public int FreeRanges { get; init; }

    public int PlannedAreas { get; init; }

    public long PlannedBytes { get; init; }

    public string? JournalPath { get; init; }

    public long JournalBytes { get; init; }

    public int JournalEntries { get; init; }

    public SizeResult? Size { get; init; }

    public RestoreResult? RestoreResult { get; init; }

    public bool UserDataCheckAvailable { get; init; }

    public int DamagedBeforeRestore { get; init; }

    public int DamagedAfterRestore { get; init; }

    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

    public double ElapsedSeconds { get; init; }

    public bool Finished => Stage is SessionStage.Done or SessionStage.Cancelled or SessionStage.Failed;
}

/// <summary>
/// The guided flow behind the map UI: show the whole card, show its free space, plan and journal the areas to be
/// written, write them, read them back, then restore the saved originals. Every step can wait for the user.
/// </summary>
public sealed class MapSession : IDisposable
{
    private const int MaxLog = 300;

    private readonly IBlockDevice _dev;
    private readonly MapSessionOptions _o;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _go = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly RunLog _runLog = new();
    private readonly List<string> _logTail = new();
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly byte[] _free;
    private readonly byte[] _status;
    private readonly byte[] _restored;

    private long _version;
    private SessionStage _stage = SessionStage.Idle;
    private bool _waiting;
    private string? _next;
    private string _message = "Ready.";
    private string _progressLabel = "";
    private double _progress;
    private bool _freeKnown;
    private long _freeBytes;
    private int _plannedAreas;
    private long _plannedBytes;
    private Journal? _journal;
    private SizeResult? _size;
    private RestoreResult? _restoreResult;
    private bool _userCheck;
    private int _damagedBefore;
    private int _damagedAfter;
    private DateTime _ended;
    private Task? _worker;

    public MapSession(IBlockDevice dev, MapSessionOptions options)
    {
        _dev = dev;
        _o = options;
        Layout = MapLayout.For(dev.Size);
        _free = new byte[Layout.Cells];
        Array.Fill(_free, (byte)255);
        _status = new byte[Layout.Cells];
        _restored = new byte[Layout.Cells];
        Log($"Device: {dev.Description}");
    }

    public MapLayout Layout { get; }

    public Task? Completion => _worker;

    public void Start()
    {
        lock (_lock)
        {
            if (_worker is not null)
            {
                throw new InvalidOperationException("Already started.");
            }

            _worker = Task.Run(Work);
        }
    }

    public void Continue() => _go.Release();

    public void Cancel()
    {
        _cts.Cancel();
        Touch(() => _message = "Stopping... originals will be restored.");
    }

    public (string Html, string LogText) BuildReport()
    {
        var report = new RunReport { Device = _dev.Description, Mode = "map prototype", Size = _size };
        string log = ReportWriter.BuildLog(report, _runLog);
        return (ReportWriter.BuildHtml(report, log), log);
    }

    public SessionSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new SessionSnapshot
            {
                Version = _version,
                Stage = _stage,
                Waiting = _waiting,
                NextStep = _next,
                Message = _message,
                ProgressLabel = _progressLabel,
                Progress = _progress,
                Layout = Layout,
                FreePct = (byte[])_free.Clone(),
                Status = (byte[])_status.Clone(),
                Restored = (byte[])_restored.Clone(),
                FreeKnown = _freeKnown,
                FreeBytes = _freeBytes,
                UsedBytes = _freeKnown ? _dev.Size - _freeBytes : 0,
                FreeRanges = _dev.UsableExtents.Count,
                PlannedAreas = _plannedAreas,
                PlannedBytes = _plannedBytes,
                JournalPath = _journal?.Path,
                JournalBytes = _journal?.Bytes ?? 0,
                JournalEntries = _journal?.Entries.Count ?? 0,
                Size = _size,
                RestoreResult = _restoreResult,
                UserDataCheckAvailable = _userCheck,
                DamagedBeforeRestore = _damagedBefore,
                DamagedAfterRestore = _damagedAfter,
                Log = _logTail.ToArray(),
                ElapsedSeconds = ((_ended == default ? DateTime.UtcNow : _ended) - _started).TotalSeconds,
            };
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException)
        {
        }

        _journal?.Dispose();
        _cts.Dispose();
        _go.Dispose();
    }

    // ---- worker ----

    private void Work()
    {
        try
        {
            SetStage(SessionStage.Map, "Whole card mapped. Each square is part of the card's claimed capacity.");
            Gate("Show free space");

            ShowFreeSpace();
            Gate("Plan areas and create the journal");

            RunSizeCheck();
        }
        catch (OperationCanceledException)
        {
            Log("Stopped by user.");
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            Touch(() => _message = ex.Message);
            RestoreIfNeeded(afterFailure: true);
            SetStage(SessionStage.Failed, ex.Message);
            Finish();
            return;
        }

        bool cancelled = _cts.IsCancellationRequested;
        RestoreIfNeeded(afterFailure: false);
        SetStage(cancelled ? SessionStage.Cancelled : SessionStage.Done, FinalMessage(cancelled));
        Finish();
    }

    private void Finish()
    {
        lock (_lock)
        {
            _ended = DateTime.UtcNow;
            _version++;
        }
    }

    private string FinalMessage(bool cancelled)
    {
        string restored = _restoreResult is null ? "" : _restoreResult.Ok ? " Originals restored and verified." : " RESTORE PROBLEM: journal kept, see details.";
        if (cancelled)
        {
            return "Stopped." + restored;
        }

        return (_size?.Summary ?? "Finished.") + restored;
    }

    private void ShowFreeSpace()
    {
        SetStage(SessionStage.FreeSpace, "Showing free space (light) and used space (dark).");
        long cb = Layout.CellBytes;
        var freeBytes = new long[Layout.Cells];
        foreach (var e in _dev.UsableExtents)
        {
            int first = (int)(e.Start / cb);
            int last = (int)((e.End - 1) / cb);
            for (int i = first; i <= last && i < freeBytes.Length; i++)
            {
                long lo = Math.Max(e.Start, i * cb);
                long hi = Math.Min(e.End, (i + 1) * cb);
                freeBytes[i] += hi - lo;
            }
        }

        lock (_lock)
        {
            for (int i = 0; i < freeBytes.Length; i++)
            {
                long len = Math.Min(cb, _dev.Size - i * cb);
                _free[i] = (byte)Math.Round(100.0 * freeBytes[i] / len);
            }

            _freeKnown = true;
            _freeBytes = Extents.Sum(_dev.UsableExtents);
            _version++;
        }

        Log($"Free space: {_freeBytes >> 20} MiB of {_dev.Size >> 20} MiB in {_dev.UsableExtents.Count} ranges");
    }

    private void RunSizeCheck()
    {
        Directory.CreateDirectory(_o.JournalDirectory);
        if (_o.DeviceRoot is { } root && string.Equals(Path.GetPathRoot(Path.GetFullPath(_o.JournalDirectory)), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"The journal folder is on the card being tested ({root}). Choose a folder on another drive.");
        }

        string path = Path.Combine(_o.JournalDirectory, $"sdchk-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.sdchkj");
        var journal = Journal.Create(path, _dev.Description, _dev.Size);
        lock (_lock)
        {
            _journal = journal;
            _version++;
        }

        var observer = new SessionObserver(this, journal);
        var options = _o.Probe with
        {
            Observer = observer,
            Log = Log,
            Progress = SetProgress,
            Cancel = _cts.Token,
        };

        var result = SizeProbe.Run(_dev, options);
        lock (_lock)
        {
            _size = result;
            _version++;
        }

        Log($"Size result: {result.Verdict} - {result.Summary}");
    }

    private void RestoreIfNeeded(bool afterFailure)
    {
        var journal = _journal;
        if (journal is null || journal.Entries.Count == 0)
        {
            return;
        }

        try
        {
            if (_dev is IUserDataProbe probe)
            {
                lock (_lock)
                {
                    _userCheck = true;
                    _damagedBefore = probe.CountDamagedUserBlocks();
                }
            }

            if (!afterFailure && !_cts.IsCancellationRequested)
            {
                SetStage(SessionStage.Restore, "Ready to put every saved original back.");
                try
                {
                    Gate("Restore originals");
                }
                catch (OperationCanceledException)
                {
                    Log("Stopped while waiting; restoring now.");
                }
            }

            SetStage(SessionStage.Restore, "Restoring the saved originals and verifying them...");
            var result = journal.Restore(_dev, entry => MarkRestored(entry.Extent), f => SetProgress("Restoring originals", f));
            lock (_lock)
            {
                _restoreResult = result;
                if (_dev is IUserDataProbe p2)
                {
                    _damagedAfter = p2.CountDamagedUserBlocks();
                }

                _version++;
            }

            Log(result.Ok
                ? $"Restored {result.Entries} saved areas ({result.Bytes >> 20} MiB) and verified them."
                : $"RESTORE PROBLEM in {result.Failures.Count} areas: " + string.Join("; ", result.Failures.Take(5).Select(f => $"{f.Offset}: {f.Reason}")));

            if (result.Ok && !_o.KeepJournal)
            {
                journal.Dispose();
                File.Delete(journal.Path);
                Log("Journal deleted (everything restored).");
            }
            else
            {
                Log($"Journal kept at {journal.Path}");
            }
        }
        catch (OperationCanceledException)
        {
            Log("Restore was interrupted. Journal kept at " + journal.Path);
        }
    }

    // ---- state helpers ----

    private void Gate(string nextStep)
    {
        if (!_o.StepMode)
        {
            return;
        }

        Touch(() =>
        {
            _waiting = true;
            _next = nextStep;
        });
        try
        {
            _go.Wait(_cts.Token);
        }
        finally
        {
            Touch(() =>
            {
                _waiting = false;
                _next = null;
            });
        }
    }

    private void SetStage(SessionStage stage, string message)
    {
        Touch(() =>
        {
            _stage = stage;
            _message = message;
        });
        Log(message);
    }

    private void SetProgress(string label, double fraction) =>
        Touch(() =>
        {
            _progressLabel = label;
            _progress = fraction;
        });

    private void Touch(Action change)
    {
        lock (_lock)
        {
            change();
            _version++;
        }
    }

    private void Log(string message)
    {
        _runLog.Info(message);
        lock (_lock)
        {
            _logTail.Add(_runLog.Lines[^1]);
            if (_logTail.Count > MaxLog)
            {
                _logTail.RemoveAt(0);
            }

            _version++;
        }
    }

    private void Mark(Extent e, CellStatus status)
    {
        long cb = Layout.CellBytes;
        int first = (int)(e.Start / cb);
        int last = (int)((e.End - 1) / cb);
        lock (_lock)
        {
            for (int i = first; i <= last && i < _status.Length; i++)
            {
                if ((byte)status > _status[i])
                {
                    _status[i] = (byte)status;
                }
            }

            _version++;
        }
    }

    private void MarkRestored(Extent e)
    {
        long cb = Layout.CellBytes;
        int first = (int)(e.Start / cb);
        int last = (int)((e.End - 1) / cb);
        lock (_lock)
        {
            for (int i = first; i <= last && i < _restored.Length; i++)
            {
                _restored[i] = 1;
            }

            _version++;
        }

        Pause();
    }

    private void Pause()
    {
        if (_o.AreaDelayMs > 0)
        {
            _cts.Token.WaitHandle.WaitOne(_o.AreaDelayMs);
        }
    }

    private sealed class SessionObserver(MapSession s, Journal j) : JournalObserver(j, s._dev, s._cts.Token, f => s.SetProgress("Saving originals to the journal", f))
    {
        private bool _journalStageShown;
        private List<PlannedArea> _areas = new();

        public override void Planned(IReadOnlyList<PlannedArea> areas)
        {
            _areas = areas.ToList();
            long bytes = areas.Sum(a => a.Extent.Length);
            s.CheckJournalSpace(bytes);
            s.Touch(() =>
            {
                s._plannedAreas = areas.Count;
                s._plannedBytes = bytes;
            });
            foreach (var a in areas)
            {
                s.Mark(a.Extent, CellStatus.Planned);
            }

            s.SetStage(SessionStage.Journal, $"{areas.Count} areas planned ({bytes >> 20} MiB). Their current content will be saved to the journal before anything is written.");
        }

        public override void Protect(IReadOnlyList<Extent> extents)
        {
            if (!_journalStageShown)
            {
                _journalStageShown = true;
                s.Gate("Create the journal (save originals)");
            }

            base.Protect(extents);
            foreach (var e in extents)
            {
                s.Mark(e, CellStatus.Journaled);
            }

            s.Touch(() => { });
            s.Log($"Journal holds {s._journal!.Bytes >> 20} MiB in {s._journal.Entries.Count} areas");
        }

        public override void PhaseStart(ProbePhase phase)
        {
            switch (phase)
            {
                case ProbePhase.Write:
                    s.SetStage(SessionStage.Write, "Writing unique test data to the planned areas.");
                    s.Gate("Write the areas");
                    break;
                case ProbePhase.Validate:
                    s.SetStage(SessionStage.Validate, "Reading the areas back in random order and checking them.");
                    s.Gate("Read back and validate");
                    break;
                case ProbePhase.Scan:
                    s.Touch(() => s._message = "Scanning the whole card for data that landed in the wrong place.");
                    break;
                case ProbePhase.Refine:
                    s.Touch(() => s._message = "Narrowing down the real capacity.");
                    break;
            }
        }

        public override void AreaWritten(PlannedArea area)
        {
            s.Mark(area.Extent, CellStatus.Written);
            s.Pause();
        }

        public override void AreaVerified(ProbeOutcome outcome)
        {
            var extent = new Extent(outcome.Offset, s._o.Probe.ProbeBytes);
            s.Mark(extent, outcome.IsGood ? CellStatus.Verified : outcome.Changed > 0 ? CellStatus.Changed : CellStatus.Failed);
            s.Pause();
        }

        public override void AliasFound(AliasHit hit) => s.Mark(new Extent(hit.ReadOffset / Pattern.BlockSize * Pattern.BlockSize, Pattern.BlockSize), CellStatus.Changed);
    }

    private void CheckJournalSpace(long bytes)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(_o.JournalDirectory)) ?? "";
        try
        {
            long free = new DriveInfo(root).AvailableFreeSpace;
            if (free < bytes + (64L << 20))
            {
                throw new IOException($"Not enough room for the journal: needs about {bytes >> 20} MiB, {free >> 20} MiB free on {root}. Nothing was written to the card.");
            }
        }
        catch (ArgumentException)
        {
        }
    }
}
