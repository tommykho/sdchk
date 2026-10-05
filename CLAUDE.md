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
- Always unbuffered, sector-aligned I/O; write phase fully completes before read phase.
- Per-sector data must be unique (seed + absolute offset); never reuse a repeating pattern.
- Core logic must be testable via `FakeBlockDevice` simulating: genuine card, wrap-around fake, silently-discarding fake, bad sectors, slow regions, read errors.
- Confidence mode must stop on the first hash/read/write failure, honor Stop at any time, and still emit a report; report coverage honestly (tested fraction), never "card is good".
- Report outputs: `result.html` + `result.log`. Never claim a verdict stronger than the evidence (e.g. Quick test cannot prove every sector good; say "sampled").
- Do not copy code from SDCheck/MediaTester/H2testw/F3 without confirming license compatibility; implement independently.

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
