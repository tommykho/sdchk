# PLAN

## Decisions (from interview)
- Windows 11, C#/.NET 8, GUI + CLI, GPL-3.0-or-later, source on GitHub.
- Modes: Quick (destructive, region probes), Empty-space (non-destructive), Full (destructive).
- Region = 500 MB (configurable). Probe/chunk = 5 MB. Quick probes: start, middle, end of each region + last sectors of card.
- Hash: SHA-256 per chunk (evidence) plus regeneration compare.
- Speed grading with class estimate. Removable-only, typed confirmation.
- Output: `result.html` + `result.log`, shown inside the GUI. GUI block map shows writes landing across the card.

## Confidence mode (added)
- Non-destructive, stoppable any time (button / Ctrl+C), always emits a report. Uses the free-space file mapping shared with Empty-space.
- **Chunk** = 10 MB. **Test point** = one chunk; a card gets ~1 point per 500 MB region (512 GB = ~1024 points). **Batch** = 32 points (configurable).
- **Order** (deterministic, no repeats): front, back, middle, then recursive bisection of the regions `0, N-1, N/2, N/4, 3N/4, N/8, 3N/8, 5N/8, 7N/8, ...`, plus probes just before/after common real sizes (4/8/16/32/64/128/256 GB) that fall inside the claimed size. The first 32 points of this order are **Batch 1** (the wrap-around reference set); later passes use a different random 10 MB slot per region.
- **Write first, verify later** (a wrap fake reads back its own fresh write fine; it fails only after the aliased location is overwritten):
  1. Write a whole batch of 32 chunks in different regions. No read yet.
  2. Read back and SHA-256 verify that batch once.
  3. Do not delete the test file; continue with the next batch.
  4. After every 10 batches (configurable), re-read and re-verify Batch 1. A mismatch means later writes overwrote it: wrap-around.
  5. Stop or finish: final sweep re-verifies every point in reverse order.
- Each sector embeds seed + absolute offset, so a bad read reports "data from offset A found at B" (real size = B - A) or "zeros/0xFF" (discarding fake).
- **First mismatch, read error or short write stops the run** with FAIL. Then a short bisect between last good and first bad offset estimates the real capacity ("claimed 512 GB, real ~16 GB").
- Test file(s) are kept for the run and after Stop; they hold the card's free space. A **Clean up** button / `sdchk cleanup` deletes them. Never auto-deleted.
- Live display: coverage % = verified bytes / claimed capacity; MB/s; ETA hours to target (`(target% x capacity - verified) / rolling throughput`) and to 100%; batch number; last Batch-1 recheck result.
- Honest wording: PASS = "no failure found in X% of the card"; report shows detection probability `1-(1-f)^n`. Coverage is capped by free space (shown up front, with unreachable regions listed).
- Separate **Speed test** (2-4 GB sequential write in free space, MB/s over time) because 10 MB chunks cannot expose the card's write-cache drop. Per-chunk MB/s still logged; slow = far below median.
- Tests (FakeBlockDevice): wrap fake caught by the Batch-1 recheck and reports correct real size; discard fake caught at first verify; genuine passes; Stop mid-batch gives a valid report; planner order unit-tested.
- Milestone: M6b after Empty-space.

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
