# PLAN

## Decisions (from interview)
- Windows 11, C#/.NET 8, GUI + CLI, GPL-3.0-or-later, source on GitHub.
- Modes: Quick (destructive, region probes), Empty-space (non-destructive), Full (destructive).
- Region = 500 MB (configurable). Probe/chunk = 5 MB. Quick probes: start, middle, end of each region + last sectors of card.
- Hash: SHA-256 per chunk (evidence) plus regeneration compare.
- Speed grading with class estimate. Removable-only, typed confirmation.
- Output: `result.html` + `result.log`, shown inside the GUI. GUI block map shows writes landing across the card.

## Confidence mode (added)
- Non-destructive by design, stoppable any time (button / Ctrl+C), always emits a report.
- **Test point** = one **10 MB file** (`sdchk_<seed>_<n>.bin`) written into free space; ~1 per 500 MB region (512 GB = ~1024 points). **Batch** = 32 points (configurable). Files are kept until Clean up (`sdchk cleanup` / GUI button), never auto-deleted.
- **Placement**: create the file preallocated, then `FSCTL_MOVE_FILE` its clusters to a free LCN in the target region, then write data. Fallback if the filesystem refuses: carve chunks out of reserve file(s) (<= 2 GB each, FAT32-safe) mapped with `FSCTL_GET_RETRIEVAL_POINTERS`. Validate on real hardware (M9); report lists regions it could not reach.
- **Order** (no repeats): front, back, middle, then recursive bisection `0, N-1, N/2, N/4, 3N/4, N/8, ...`; first 32 points = **Batch 1**. Later passes use a different random 10 MB slot per region.

### Stage 0: fast screen (goal: expose "512 GB is really 32 GB" in about 1-2 minutes)
1. Write ~12 chunks: offset 0, the last chunk of the claimed size, and the 1/2/4/8/16/32/64/128/256 GB marks inside it. About 120 MB, seconds.
2. Verify them immediately. Discarding, zero-returning, error-returning and exact-boundary wrap fakes fail here: FAIL, stop.
3. **Alias scan**: strided raw reads (one 4 KiB sector every ~5 MB, so a 10 MB chunk cannot be missed) across the whole claimed range, looking for our magic with an embedded offset H that differs from the read address A. Every hit proves wrap-around: data meant for H is stored at A. `H - A = k x R` yields the real size R (consistent across several chunks), refined by a short bisect. ~100k reads for 512 GB, ~30-100 s on typical cards.
4. Stage 0 passing is not a pass of the card; it only means no cheap fake pattern was found. Continue with batches.

### Batches (Stage 1+)
1. Write a whole batch (32 files) with no reads.
2. Verify that batch once (SHA-256 + regenerate-compare).
3. Continue with next batch. Do not delete files.
4. After every 10 batches (configurable) re-verify Batch 1; a mismatch means later writes overwrote it (wrap-around) and the alias scan runs again.
5. On stop/finish: final sweep re-verifies every point in reverse order.
- Each sector embeds seed + absolute offset: a bad read reports "data from offset A found at B" (wrap, real size = B - A), "zeros/0xFF" (discarding) or "no magic" (garbage).
- **First mismatch, read error or short write stops the run** with FAIL and a real-capacity estimate ("claimed 512 GB, real ~32 GB").
- Live display: coverage % (verified bytes / claimed), MB/s, ETA hours to the target and to 100%, batch number, last Batch-1 recheck.
- Honest wording: PASS = "no failure found in X% of the card"; show detection probability `1-(1-f)^n`.
- **Data-loss warning** (README + GUI confirm): on a wrap-around fake, a write meant for free space can land on a real file's physical location and overwrite it. Non-destructive modes are only safe on genuine cards. Back up the card first; use Quick/Full for suspected fakes.
- Separate **Speed test**: 2-4 GB sequential write, MB/s over time (10 MB files cannot show the write-cache drop).
- Tests (FakeBlockDevice): 512-as-32 wrap fake (R = decimal 31.9 GB, not a power of two) caught by alias scan in Stage 0 with R estimated within 1%; discard/zero fakes caught at first verify; genuine passes with no false alias; Stop mid-batch gives a valid report; planner order unit-tested.
- Milestone: M6b after Empty-space.

## Assumptions to confirm (change if wrong)
1. Empty-space/Confidence placement uses `FSCTL_MOVE_FILE` to put small files at chosen free clusters (fallback: reserve-file carving). Coverage depends on free-space layout; report shows which regions were reachable.
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
