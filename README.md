# sdchk

Open-source microSD / flash-drive checker for **Windows 11**. Detects fake capacity (e.g. a 16 GB card relabelled 512 GB), defective sectors and slow/low-grade dies, and produces evidence you can attach to a refund or fraud dispute.

> Status: **planning**. See [PLAN.md](PLAN.md). Nothing below is implemented yet.

## Why

Counterfeit cards report a false size to the OS. They accept writes past their real capacity, then wrap around or discard the data. Only writing unique data across the whole address range and reading it back proves the real size.

## Features (v1 target)

| Mode | Destructive | What it does |
|---|---|---|
| **Quick** | Yes (erases card) | Splits card into 500 MB regions. Writes unique 5 MB probes at start, middle and end of each region plus the card's last sectors, then reads back in shuffled order and verifies. Minutes. |
| **Empty-space** | No | Fills a pre-allocated file in free space, maps it to physical offsets, writes a 5 MB chunk near each 500 MB region, verifies via SHA-256, deletes the file. Existing files untouched. Coverage limited by free space. |
| **Confidence** | No | Non-destructive, stoppable any time. Writes batches of 32 x 10 MB chunks into free space in a spread order (front, back, middle, then bisecting the gaps), verifies each batch by SHA-256, keeps the test file, and re-verifies the first batch every 10 batches to prove there is no wrap-around. Shows coverage %, ETA hours to a target such as 2%, estimates real capacity, and stops on the first failure. Needs free space; unreachable regions are reported. `sdchk cleanup` removes test files. |
| **Full** | Yes (erases card) | Writes unique data to every sector start-to-end, then reads everything back. Gold standard; slow (hours on big cards). |

Common to all modes:
- Live **GUI block map**: every 500 MB region shows pending / written / verified / failed so you can see data landing across the whole card.
- **Speed grading**: sequential read/write MB/s (min/avg/max), slow-block detection, error/retry counts, estimated speed class (C4/C10/U1/U3/V30). Estimate only; random I/O (A1/A2) not tested.
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
- H2testw, F3 (concept references)

This project is a clean-room implementation of the technique; check each reference's license before copying any code.

## License

GPL-3.0-or-later.
