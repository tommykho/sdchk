# sdchk

Open-source microSD / flash-drive checker for **Windows 11**. Detects fake capacity (e.g. a 16 GB card relabelled 512 GB), defective sectors and slow/low-grade dies, and produces evidence you can attach to a refund or fraud dispute.

> Status: **planning**. See [PLAN.md](PLAN.md). Nothing below is implemented yet.

## Why

Counterfeit cards report a false size to the OS. They accept writes past their real capacity, then wrap around or discard the data. Only writing unique data across the whole address range and reading it back proves the real size.

## Features (v1 target)

| Mode | Destructive | What it does |
|---|---|---|
| **New Card Check** (default) | Yes (new card) | Answers three questions: wrong size? wrong speed? bad sectors? Size fast screen (1-2 min), then sustained speed test vs the printed class, then full write + verify of every sector. Verdict per item: SIZE / SPEED / QUALITY. |
| **Quick** | Yes (erases card) | Splits card into 500 MB regions. Writes unique 5 MB probes at start, middle and end of each region plus the card's last sectors, then reads back in shuffled order and verifies. Minutes. |
| **Empty-space** | No | Fills a pre-allocated file in free space, maps it to physical offsets, writes a 5 MB chunk near each 500 MB region, verifies via SHA-256, deletes the file. Existing files untouched. Coverage limited by free space. |
| **Confidence** (estimate only, used cards) | No* | ESTIMATE only: max confidence equals the free-space fraction (a half-full card caps at 50%); existing files are not tested. Non-destructive on genuine cards, stoppable any time. **Fast screen first (~1-2 min):** writes 10 MB files at 0, the card end and power-of-two marks, verifies them, then scans the whole card for data that landed in the wrong place (wrap-around), which exposes "512 GB that is really 32 GB" and estimates the real size. Then writes batches of 32 files in a spread order (front, back, middle, bisection), verifies each batch by SHA-256, keeps the files, and re-verifies batch 1 every 10 batches. Shows coverage %, ETA to a target such as 2%, stops on first failure. `sdchk cleanup` removes test files. *On a wrap-around fake a write can overwrite existing files: back up first. |
| **Full** | Yes (erases card) | Writes unique data to every sector start-to-end, then reads everything back. Gold standard; slow (hours on big cards). |

Common to all modes:
- Live **GUI block map**: every 500 MB region shows pending / written / verified / failed so you can see data landing across the whole card.
- **Speed grading** (CrystalDiskMark-style methodology): unbuffered random-data I/O, 1 MiB sequential read/write at the start, middle and end of the card, sustained 4 GB writes graded on min/median rather than peak, 4 KiB random Q1 read/write IOPS, slow-block detection, error/retry counts, and an estimated speed class (C10/U1/U3/V30; A1/A2 indicative only).
- **Verdict**: claimed vs real capacity, first bad offset, bad-block list, speed result.
- **Output**: `result.html` and `result.log`, viewable inside the GUI. HTML includes tool version, timestamp, device model/serial, claimed/real size, and a SHA-256 of the log.
- **GUI + CLI** sharing one core library.

## How detection works

1. Every 4 KiB sector gets a header (magic, run seed, absolute offset) plus pseudo-random payload derived from `seed + offset`. No two sectors are identical, so wrap-around and aliasing are caught.
2. Write phase completes **before** the read phase, so wrapped overwrites of early data are exposed.
3. Direct, unbuffered I/O (`FILE_FLAG_NO_BUFFERING | WRITE_THROUGH`) on sector-aligned buffers; the OS cache cannot hide failures.
4. Real capacity = end of the largest contiguous verified range from offset 0.

## Safety

- Lists **removable** drives only; refuses system/boot disks.
- Destructive modes require typing the drive letter/serial to confirm.
- Needs Administrator (raw disk access).

## Build and run (planned)

```
dotnet build -c Release
sdchk list
sdchk quick  --disk 3
sdchk full   --disk 3 --out results\
sdchk empty  --drive F:
sdchk confidence --drive F: --target 2%   # Ctrl+C or Stop ends it; report shows coverage reached
sdchk cleanup --drive F:                 # delete kept test files
sdchk-gui.exe
```

Requires Windows 11 and .NET 8 (self-contained single-file release planned).

## Prior art

- [ulikoehler/SDCheck](https://github.com/ulikoehler/SDCheck)
- [dkrahmer/MediaTester](https://github.com/dkrahmer/MediaTester)
- [CrystalDewWorld/CrystalDiskMark-Latest](https://github.com/CrystalDewWorld/CrystalDiskMark-Latest) (MIT): speed-test methodology
- H2testw, F3 (concept references)

SDCheck (Apache-2.0) writes sequentially until errors then compares against a second identically seeded RNG. MediaTester (GPL-3.0) writes 1 GiB files in unbuffered 8 MiB blocks and quick-reads the first/last block of each file. sdchk adds per-run seeds with embedded offsets, end-first probing, wrap-around (alias) detection, real-size estimation, speed grading and a used-card estimate mode. Both licenses are compatible with GPL-3.0-or-later; credit them if any code is reused.

## License

GPL-3.0-or-later.
