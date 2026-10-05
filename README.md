# sdchk

Open-source microSD / flash-drive checker for **Windows 11**. Finds fake capacity (a 16 GB card relabelled 512 GB) and slow cards, and writes an evidence report you can attach to a refund or fraud dispute.

> **Status: Stage 1 proof of concept. Scope is SIZE and SPEED only.** The size and speed logic is unit-tested against simulated fake cards on Linux. The Windows drive backend has **not yet been run on real hardware**: start with `sdchk inspect`.

## WARNING - read before use

**This test writes test data into the free space of your card. If the card is FAKE and wraps around (it reports more capacity than it really has), these writes can OVERWRITE AND DESTROY EXISTING FILES. BACK UP ALL FILES FIRST. Use at your own risk: the software is provided without warranty of any kind (GPL-3.0-or-later), and the authors are not liable for data loss.**

The CLI shows this text and requires you to type `I UNDERSTAND` (or pass `--accept-risk`). The same text is in every report.

## What it checks (Stage 1)

**1. Size** - fast capacity check, a few minutes even for 512 GB:
- Writes unique 8 MiB probes at the front, back, middle, by bisection, and at power-of-two offsets (the f3probe idea).
- Writes extra data to push the probes out of the card's write cache, then reads every probe back in shuffled order. A second, larger cache flush and re-verify catches fakes with big caches.
- Scans the whole address space (one block every 4 MiB) for data that landed in the wrong place. Any hit proves wrap-around and gives the real size.
- If data is simply dropped (zeros, noise or write errors), bisects to the real capacity (+/- 8 MiB).
- Verdict: PASS (sampled; cannot rule out scattered bad sectors), FAIL (real size reported), SUSPECT or INCONCLUSIVE.

**2. Speed** - graded the way speed classes are defined:
- Sustained sequential write and read of 1 GiB (use `--seq-mib 4096` for a final grade) at the start, middle and end of the card, with MB/s per second and detection of the "fast cache, then slow" drop.
- Graded on the 5th percentile and median, not peak, against the class you say is printed on the card (C10, U1, U3, V30 ...).
- 4 KiB random write/read IOPS at queue depth 1, with an indicative A1/A2 hint.
- Skipped automatically if the capacity is fake.

Not in Stage 1: full-card bad-sector scan, GUI, used-card "confidence" mode. See [PLAN.md](PLAN.md).

## Usage

```
sdchk list                                   # drives (Windows)
sdchk inspect --drive F:                     # read-only safety inspection, run this first
sdchk check --drive F: --class U3            # size + speed (Windows, run as Administrator)
sdchk check --drive F: --size-only
sdchk check --image card.img --image-gib 4   # on a disk-image file, any OS
sdchk simulate                               # list simulated fake cards
sdchk check --simulate wrap-32-of-512 --class U3
```

Options: `--class`, `--size-only`, `--speed-only`, `--probe-mib`, `--seq-mib`, `--random-ops`, `--out DIR`, `--allow-fixed`, `--accept-risk`.
Exit code: 0 all pass, 2 any fail, 3 suspect/inconclusive, 1 error.

Output: `results\result.html` (self-contained, with a speed chart and the log's SHA-256) and `results\result.log`.

## How it touches the card (Windows backend)

- Lists only removable drives (use `--allow-fixed` for a card reader Windows calls fixed); never the system drive.
- Locks the volume, reads the file-system free-space bitmap and writes **only to clusters the file system reports as free**, below the file system, so probes land at known physical positions. A hard guard refuses any write outside free space.
- Before trusting the bitmap it creates a 1 MiB calibration file and proves where clusters live on disk (NTFS, exFAT, FAT32). If it cannot prove it, it stops without writing. The temporary file is removed afterwards.
- Everything is written unbuffered with write-through.
- On a genuine card your files are untouched. On a fake wrap-around card a "free" cluster can physically be someone else's data: that is the warning above.

## Build

```
dotnet build -c Release
dotnet test
dotnet publish src/SdChk.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Requires .NET 8 SDK. Layout: `src/SdChk.Core` (engine, simulated cards, reports), `src/SdChk.Windows` (drive backend), `src/SdChk.Cli`, `tests/SdChk.Tests`.

## Prior art

- [ulikoehler/SDCheck](https://github.com/ulikoehler/SDCheck) (Apache-2.0): sequential fill and compare with a second seeded RNG.
- [dkrahmer/MediaTester](https://github.com/dkrahmer/MediaTester) (GPL-3.0): unbuffered 8 MiB blocks, quick read of first/last block per file.
- [AltraMayor/f3](https://github.com/AltraMayor/f3) (GPL-3.0): f3probe wrap/sampling/cache ideas, ok/corrupted/changed/overwritten block states.
- [c0xc/CapacityTester](https://github.com/c0xc/CapacityTester) (GPL-3.0): raw 1 GB-step test, per-position ids, cache reopen lessons.
- [CrystalDewWorld/CrystalDiskMark-Latest](https://github.com/CrystalDewWorld/CrystalDiskMark-Latest) (MIT): speed-test methodology.

sdchk is an independent implementation of these techniques. Corroborate a dispute with a well-known tool (H2testw or f3) as well.

## License

GPL-3.0-or-later.
