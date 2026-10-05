# CLAUDE.md

Project: **sdchk**, a Windows 11 microSD fake-capacity / defect / speed checker. GPL-3.0-or-later. See README.md (what) and PLAN.md (how/milestones).

## Stack
- C# / .NET 8. Solution layout:
  - `src/SdChk.Core`: platform-neutral engine (patterns, test modes, verdict, report). Depends only on `IBlockDevice`.
  - `src/SdChk.Windows`: Win32 raw-disk implementation of `IBlockDevice` (P/Invoke), volume bitmap/retrieval-pointer helpers.
  - `src/SdChk.Cli`: `sdchk` command line.
  - `src/SdChk.Gui`: WinForms app (block map, speed graph, log, WebView2 result view).
  - `tests/SdChk.Tests`: xUnit; uses `FakeBlockDevice`.
- Dev sandbox is Linux: Core and Tests must build/run with `dotnet test` on Linux. Windows-only code stays in `SdChk.Windows`/`Gui`/`Cli` Windows paths. Never claim Windows behaviour was verified unless it ran on Windows (CI: GitHub Actions `windows-latest`).

## Non-negotiable rules
- Destructive operations: removable disks only; never the system/boot disk; require explicit typed confirmation; check disk number, size and serial immediately before the first write. Default to refusal when unsure.
- Always unbuffered, sector-aligned I/O; write phase fully completes (flush, reopen handle, overwhelm write cache) before the read phase; verification of probes uses random-order small reads, not just sequential.
- Per-sector data must be unique (seed + absolute offset); never reuse a repeating pattern.
- Core logic must be testable via `FakeBlockDevice` simulating: genuine card, wrap-around fake, silently-discarding fake, bad sectors, slow regions, read errors.
- Confidence mode: Stage 0 fast screen (far probes + alias scan) before batches; 10 MB files, spread order, batches of 32 verified after writing, Batch 1 rechecked every 10 batches, files kept until explicit cleanup; stop on first failure, honor Stop, always emit a report; coverage honest (tested fraction), never "card is good"; warn that non-destructive modes can overwrite data on fakes.
- Report outputs: `result.html` + `result.log`. Never claim a verdict stronger than the evidence (e.g. Quick test cannot prove every sector good; say "sampled").
- SDCheck (Apache-2.0), MediaTester, f3, CapacityTester (GPL-3.0) and CrystalDiskMark (MIT) are license-compatible; independent implementation is the default, any reused code needs attribution, kept notices and a check of its per-file GPL "only" vs "or later" header.
- Primary workflow is New Card Check (size, speed, bad sectors). Confidence mode is an estimate for used cards; its maximum confidence is the free-space fraction, and wording must say so.

## Conventions
- Nullable enabled, warnings as errors, `dotnet format` clean.
- Small functions, no comments restating code; comment only non-obvious Win32 details.
- Commit to branch `claude/dazzling-goldberg-dg5lba`; no PRs unless asked.

## Commands
```
dotnet build
dotnet test
dotnet format --verify-no-changes
```
