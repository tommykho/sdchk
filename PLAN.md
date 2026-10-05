# PLAN

## Decisions (from interview)
- Windows 11, C#/.NET 8, GUI + CLI, GPL-3.0-or-later, source on GitHub.
- Modes: Quick (destructive, region probes), Empty-space (non-destructive), Full (destructive).
- Region = 500 MB (configurable). Probe/chunk = 5 MB. Quick probes: start, middle, end of each region + last sectors of card.
- Hash: SHA-256 per chunk (evidence) plus regeneration compare.
- Speed grading with class estimate. Removable-only, typed confirmation.
- Output: `result.html` + `result.log`, shown inside the GUI. GUI block map shows writes landing across the card.

## Confidence mode (added)
- Non-destructive, uses the same free-space file mapping as Empty-space; user can Stop any time (button / Ctrl+C) and still gets a full report.
- Unit = 10 MB chunk. Order = stratified random without repeats: one chunk per 500 MB region first, then further passes, so any early stop is spread across the whole card.
- Each chunk: write unique data, flush, wait for a different region, then read back and SHA-256 compare (read-back delayed so the card cannot serve it from its cache). **First mismatch, read error or short write stops the run immediately** with verdict FAIL, offset and expected/actual hash.
- Live display: coverage % = verified bytes / claimed capacity; measured MB/s; ETA hours to target (`remaining = target% x capacity - verified`, divided by rolling throughput) and to 100%.
- Report states the honest meaning: coverage is the tested fraction of the claimed address space. Also shows detection probability for a defect covering fraction f: `1-(1-f)^n` after n chunks (e.g. a 16 GB card sold as 512 GB is 97% fake, so a few chunks catch it). PASS is only "no failure found in X%", never "card is good".
- Limit: only free clusters reachable; if free space is small, coverage cap is shown up front.
- Milestone: new M6b after Empty-space (shares mapping code); tests via FakeBlockDevice (wrap fake must fail within first few chunks; genuine must pass; Stop mid-run produces valid report).

## Assumptions to confirm (change if wrong)
1. "Empty-space" cannot pick physical offsets via normal files, so: pre-allocate one large file in free space, map extents with `FSCTL_GET_RETRIEVAL_POINTERS`, write 5 MB chunks only where extents fall near each 500 MB boundary, verify, delete. Coverage depends on free-space layout; report shows which regions were reachable.
2. Card readers sometimes present as "fixed" disks. v1 lists removable only; add `--allow-fixed` later if needed.
3. Speed class is an estimate from sequential tests (C/U/V); A1/A2 random I/O is out of v1.
4. GUI report view uses WebView2 (Evergreen runtime ships with Win11).
5. Reference repos are not read in this session (outside repo scope); algorithms are implemented from the technique, not copied.

## Architecture
```
IBlockDevice { Size, SectorSize, Read(off,buf), Write(off,buf), Flush }
PatternGenerator   seed+offset -> 4 KiB unique sectors (xoshiro256**/ChaCha)
TestPlan           Quick | Full | EmptySpace -> ordered list of (offset,len)
TestRunner         write all -> read/verify (shuffled for Quick) ; events: progress, block state, speed sample
Analyzer           real capacity, bad blocks, wrap detection, speed stats/grade
ReportWriter       result.html, result.log (+ SHA-256 of log)
```
Block states for GUI map: Pending, Written, Verified, Failed, Slow.

## Milestones
1. **Core + fakes**: IBlockDevice, PatternGenerator, FakeBlockDevice (genuine/wrap/discard/bad/slow), unit tests. (Linux-testable)
2. **Test modes**: Quick, Full planners/runner, Analyzer verdict. Tests prove 16 GB-as-512 GB fake is caught at ~16 GB.
3. **Report**: HTML + log writer, golden-file tests.
4. **Windows layer**: enumerate removable disks (WMI/`IOCTL_STORAGE_QUERY_PROPERTY`), lock/dismount, unbuffered I/O, safety guards.
5. **CLI**: `list`, `quick`, `full`, `empty`, `--out`.
6. **Empty-space mode**: free-space file mapping.
7. **GUI**: device picker, mode select, confirm dialog, block map, speed graph, log tab, WebView2 result tab.
8. **CI + release**: GitHub Actions (build/test on Linux+Windows), single-file self-contained exe artifact, docs.
9. **Real-hardware validation** on the user's fake cards; tune thresholds.

## Risks
- Cards that cache/ack writes then drop them: mitigated by write-then-read-later with large gaps and shuffled read order.
- Controller wear-leveling/GC making slow phases look like defects: slow-block flagged by percentile, not absolute, and reported as indicator only.
- Windows volume locking failing if an app holds the drive: clear error, no silent fallback.
- Timing: full test on 512 GB at ~20 MB/s is ~14 h; show ETA, support pause/resume (v1.1).

## Next step
Reply with corrections to the assumptions above (esp. #1), then start Milestone 1.
