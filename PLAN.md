# PLAN

## Stage 1 proof of concept (current): SIZE and SPEED only
Decided after the prior-art review: build a focused Windows tool, defer the rest.

Delivered in Stage 1:
- `SdChk.Core`: `IBlockDevice`, unique per-run block pattern (offset header + salted noise, states Good/Changed/Corrupted/Blank/Unreadable), `SizeProbe`, `SpeedTest`, `ReportWriter` (result.html + result.log with SHA-256), `FakeBlockDevice` (genuine, wrap, discard, garbage, write-error, write cache, bad ranges, speed model, virtual clock), `FileBlockDevice`.
- `SizeProbe`: front/back/middle/bisection + power-of-two probes, cache flush, random-order verify, large-cache confirmation pass, whole-space alias scan, real-size estimate (gcd of wrap distances, or bisection for dropped writes). 31 unit tests including non power-of-two wraps (31.9 GB), cached fakes and free-space fragmentation.
- `SpeedTest`: sustained sequential write/read at start/middle/end, per-second series, drop detection, 4 KiB random Q1, class grading on p5/median, A1/A2 hint.
- `SdChk.Windows` (unverified on hardware): free-space-only raw writes through the locked volume, cluster-mapping calibration file, bitmap-based extents, hard write guard, read-only `inspect`.
- `sdchk` CLI with the risk disclaimer and typed confirmation.

Stage 1 acceptance (needs a real Windows machine and cards):
1. `sdchk inspect --drive X:` passes on FAT32, exFAT and NTFS cards.
2. `sdchk check` PASS on a known-genuine card, FAIL with the right real size on a known-fake card.
3. Compare size result with H2testw/f3 and speed with CrystalDiskMark on the same card.
4. Tune thresholds (probe size, scan stride, confirm size) from those runs.

Deferred: full bad-sector scan (Stage 2), GUI with block map, Confidence/Empty-space modes, A1/A2 grading, real-size partition fix.

---

# Original plan (full scope, kept for later stages)

## Decisions (from interview)
- Windows 11, C#/.NET 8, GUI + CLI, GPL-3.0-or-later, source on GitHub.
- Modes: **New Card Check (default: size + speed + bad sectors)**, Quick (destructive, region probes), Empty-space and Confidence (non-destructive estimates for used cards), Full (destructive).
- Region = 500 MB (configurable). Probe/chunk = 5 MB. Quick probes: start, middle, end of each region + last sectors of card.
- Hash: SHA-256 per chunk (evidence) plus regeneration compare.
- Speed grading with class estimate. Removable-only, typed confirmation.
- Output: `result.html` + `result.log`, shown inside the GUI. GUI block map shows writes landing across the card.

## Primary use case: new card check (default workflow)
User buys a new card and wants to know: 1) wrong size, 2) wrong speed, 3) bad sectors/low-quality flash. New cards hold no data, so destructive raw testing is fine and is the main path.

**New Card Check** (GUI default; `sdchk newcard --disk N --claimed-class U3`):
1. **Size fast screen (1-2 min)**: far probes + alias scan (see Stage 0). Fail fast with real-size estimate; stop unless user continues.
2. **Speed** (method informed by CrystalDiskMark, see below): all runs use unbuffered I/O and random (incompressible) data, 1 MiB blocks.
   - *Sequential write, sustained*: 4 GB continuous at the start, again at the middle and the end of the card (position matters). Log MB/s per second; report min, p5, median, max and the time/size where speed drops (write-cache exhaustion). Grade on **sustained min/median**, not peak, against the class printed on the card (C10 10, U1 10, U3 30, V30 30 MB/s).
   - *Sequential read*: same three positions; compare with the advertised "up to" read speed (peak is meaningful here).
   - *Random 4 KiB read/write at Q1T1*: IOPS and latency (A1 needs 1500 read / 500 write IOPS, A2 4000 / 2000). Indicative only: those ratings assume command queuing a PC card reader may not provide.
   - 1 discarded warm-up run, then N measured runs with an interval pause between tests; all runs are kept in the log. MB/s = 1,000,000 bytes/s (CDM convention); sizes in MiB/GiB.
3. **Bad sectors**: full write pass then full verify pass over the whole card (SHA-256/regenerate), per-region failure map, retry/error counts; optional second pass.
4. Verdict per item: SIZE / SPEED / QUALITY, each PASS / FAIL / SUSPECT, in `result.html` + `result.log`.

**Confidence mode is an ESTIMATE for used cards**, not proof. Max achievable confidence = free-space fraction (a card 50% full tops out at 50%); the UI and report state this cap. Existing files are not tested by it; the user can check those files themselves. Optional read-only scan of the used area to report read errors.

## Prior art reviewed (read in this session)
- **SDCheck** (Apache-2.0, 137 lines C++, POSIX only): seeds two MT19937-64 generators identically; writes 64 KiB blocks sequentially from the start until a write error, takes bytes written as the apparent size, then reads all back and compares against the second generator (constant memory). Weaknesses: relies on write errors, so fakes that silently discard are measured only by compares; always a full sequential pass (slow); no speed/quality reporting; Linux only; no per-run safeguards.
- **MediaTester** (GPL-3.0, C#, Windows GUI+CLI, `MediaTesterLib`): file based. 1 GiB files of 8 MiB blocks in a `MediaTester` folder, `FILE_FLAG_NO_BUFFERING | WriteThrough`, block data from `System.Random(seed = absolute block index)`. After each file it immediately reads the first and last block (QuickTestAfterEachFile), on failure verifies the whole file to find the first failing byte, stops on failure, then does a full verify pass. Options: MaxBytesToTest, delete temp files, save results file to the media; per-block write/read speed. Weaknesses: sequential fill so a wrap-around fake is only caught in the final verify (hours later); the pattern depends only on block index, so leftover files from an earlier run can pass; cannot jump to the end of the card; no alias detection; no real-size estimate beyond first failing byte.
- **CrystalDiskMark** (MIT, C++, source at CrystalDewWorld/CrystalDiskMark-Latest; the repo has `DiskBench.cpp` but not the dialog code, so the default profile values such as SEQ1M Q8T1 / RND4K Q32T1 are not confirmed from source). It is a front end for Microsoft DiskSpd: per test it runs `diskspd -b<size>K -o<queue depth> -t<threads> -W0 -S -w0|100 [-r] -Z<size>K -d<seconds> -L`. It creates one test file (default size set in the UI, GiB or MiB), pre-sizes it with `SetEndOfFile` to limit fragmentation, turns NTFS compression off, fills it with random data (zeros optional), opens with `FILE_FLAG_NO_BUFFERING`. Nine configurable test slots (sequential/random, block size, queues, threads), read then write (mix optional). Each test: one discarded "Preparing" run, then N measured runs, an interval pause between tests, and the **best (max) score** is reported plus min latency. Its README warns results depend on test size, file position, fragmentation, controller, CPU and data type (random vs zero fill). It measures speed only: no capacity or integrity check, and peak scoring hides sustained slowdowns.
- **CapacityTester** (GPL-3.0-or-later headers, C++/Qt, Linux AppImage plus experimental Windows build). Two tests. *Volume test*: fills the filesystem with files then verifies; needs an empty exFAT/FAT32 volume; reports a **file** offset, not a physical one; keeps a ~1 MB safety buffer; does not classify the fake type. *Raw disk test (fast)*: for every 1 GB step writes a 1 MB pattern block (first block at each GB, last GB skipped), then **syncs and reopens the device** to avoid cache hits, then reads and compares each block. Verify-right-after-write (its "phase 0") was rejected because it "finishes within seconds" and gave wrong results. Every pattern block starts with its own position id ("crucial": otherwise a wrapped read returns the first block's data and passes). *Raw disk test (full)*: 1 MB chunks, SHA hash per chunk, parallel hash workers; notes that storing per-block hashes does not scale (2 TB, 3.9 G sectors, ~146 GB of hashes) and that direct I/O made a USB2 stick write at 330 KB/s versus ~7.5 MB/s expected, while buffered writes report bogus 263 MB/s, so full tests use synced (not direct) writes. Open to a future "eject and re-insert before verify" step.
- **f3 / Fight Flash Fraud** (GPL-3.0, C). `f3write`/`f3read` fill the mounted volume with files and report sectors as **ok / corrupted / changed / overwritten** (changed = holds data meant for another offset, overwritten = our data replaced by later data) plus speeds. `f3probe` (destructive, raw device, unmounted) is the fast one and writes only what is needed:
  1. Block = 8-byte unsalted offset header + PRNG fill seeded by `offset ^ salt` (salt is per run; the offset is not salted because "drives know the offset").
  2. **Cache defeat**: some fakes keep a tiny cache for random access and a large one for sequential access, so a sequential read of a bad block can return good data. Probe uses **random single-block reads only**, and "overwhelms the cache" by writing a cache-sized range (measured up to 1 GB) between write and read.
  3. **Find wrap**: write a reference block near the start, then read blocks at `reference + 2^j` for each power of two; a hit that returns the reference offset ("changed") proves wrap with a power-of-two **module**.
  4. **Sampling**: if not a power-of-two wrap, binary-narrow between a known-good block and a known-bad block using random samples (8 up to 1024 per round); detection probability `1-Pr_g^k` (e.g. 64 samples at 95% good gives >= 96.2%).
  5. Reports announced vs **real size**, module, estimated cache size, physical block size, and write/read op timing; `f3fix` then writes a partition table that fits the real size.
  6. Retries each write/read once before declaring an I/O error.
- **Lessons applied to sdchk** (design changes made after this review):
  - *Random-order reads for verification*: sequential chunk reads can hide bad blocks from cache-tricking fakes. Quick/Confidence/Stage 0 verify with shuffled 4-64 KiB reads (sequential reads only for speed tests and the Full pass).
  - *Overwhelm the write cache*: between writing a batch and verifying it, write a filler range of at least the detected cache size (start with 64 MB; probe up to 1 GB in v1.1) elsewhere on the card.
  - *Reopen the device handle* (and flush) between write and verify phases; consider optional "eject and reinsert" prompt for stubborn fakes.
  - *Stage 0 gets the f3 wrap probe first* (reference block + power-of-two offsets, a few reads) then our alias scan, which also catches non-power-of-two wraps.
  - *Block states* like f3: Good / Corrupted / Changed (data from offset X) / Overwritten / Unreadable, in the block map and report.
  - *Sampling-math wording* from f3: report detection probability; pre-narrow with binary search after first bad block to give the real capacity.
  - *Real-size fix* (v1.1 idea): offer a partition sized to the real capacity (f3fix equivalent) so a fake card can still be used honestly.
  - *Full test*: regenerate data from seed instead of storing hashes; use large (>= 1-4 MiB) writes and flush; measure whether `WRITE_THROUGH` on raw Windows devices is slow before choosing it for the Full pass.
  - Retry a failed write/read once before recording an error.
  - Volume-mode (Confidence) results are file-level; our placement via `FSCTL_MOVE_FILE` is what makes them physical, and the report must say when placement fell back.
- **Licensing**: f3 and CapacityTester are GPL-3.0 (CapacityTester source headers say "or any later version"; confirm f3's per-file headers before copying anything). Same family as ours; still independent implementation by default.
- **What we keep**: unbuffered/write-through I/O, quick reads while writing, stop on failure, first-failing-byte, per-block speed, results file. **From CrystalDiskMark we take**: random data, unbuffered I/O, discarded warm-up, repeated runs, pauses between tests, MB/s = 10^6 bytes/s, 4 KiB random Q1 numbers; we change peak scoring to sustained min/median/p5 and test several card positions. **What we add**: per-run seed + offset in every sector, end-first/spread probes, alias scan for wrap fakes, real-size estimate, speed grading vs class, used-card estimate mode, block-map GUI.
- **Licensing**: both licenses (GPL-3.0, Apache-2.0) are compatible with our GPL-3.0-or-later. Code reuse is allowed with attribution and license notices kept; default is still independent implementation.

## Confidence mode (used cards, estimate only)
- Non-destructive by design, stoppable any time (button / Ctrl+C), always emits a report.
- **Test point** = one **10 MB file** (`sdchk_<seed>_<n>.bin`) written into free space; ~1 per 500 MB region (512 GB = ~1024 points). **Batch** = 32 points (configurable). Files are kept until Clean up (`sdchk cleanup` / GUI button), never auto-deleted.
- **Placement**: create the file preallocated, then `FSCTL_MOVE_FILE` its clusters to a free LCN in the target region, then write data. Fallback if the filesystem refuses: carve chunks out of reserve file(s) (<= 2 GB each, FAT32-safe) mapped with `FSCTL_GET_RETRIEVAL_POINTERS`. Validate on real hardware (M9); report lists regions it could not reach.
- **Order** (no repeats): front, back, middle, then recursive bisection `0, N-1, N/2, N/4, 3N/4, N/8, ...`; first 32 points = **Batch 1**. Later passes use a different random 10 MB slot per region.

### Stage 0: fast screen (goal: expose "512 GB is really 32 GB" in about 1-2 minutes)
0. f3-style wrap probe: write a reference block near the start, read blocks at `reference + 2^j` (random single-block reads); a block holding the reference's offset proves a power-of-two wrap in seconds.
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
5. Speed class thresholds use SD Association sustained-write minimums; user picks the printed class in the GUI/CLI.

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
