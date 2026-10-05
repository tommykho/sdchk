// SPDX-License-Identifier: GPL-3.0-or-later
using SdChk.Core;
using SdChk.Core.Devices;
using SdChk.Windows;

namespace SdChk.Cli;

internal static class Program
{
    private const string Usage = """
        sdchk - SD card size and speed checker (proof of concept)

          sdchk list                          List drives
          sdchk inspect --drive F:            Read-only safety inspection of a drive (no test data written)
          sdchk check --drive F: [options]    Size + speed check of the free space of a drive (Windows, Administrator)
          sdchk check --image FILE --image-gib N [options]   Same on a disk-image file (any OS)
          sdchk check --simulate NAME [options]              Same on a simulated card (any OS)
          sdchk simulate                      List simulated cards

        Options:
          --class U3         Printed speed class: C2 C4 C6 C10 U1 U3 V6 V10 V30 V60 V90
          --size-only | --speed-only
          --probe-mib 8      Size probe length          --seq-mib 1024   Sequential bytes per position (use 4096 for a final grade)
          --random-ops 1000  Random 4 KiB operations    --out DIR        Report folder (default .\results)
          --allow-fixed      Allow a drive Windows reports as fixed
          --accept-risk      Skip the typed confirmation (you accept the warning)
        """;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var opt = new Args(args.Skip(1).ToArray());
        switch (args[0])
        {
            case "list": return List();
            case "simulate": return ListSimulations();
            case "inspect": return Inspect(opt);
            case "check": return Check(opt);
            default:
                Console.Error.WriteLine($"Unknown command '{args[0]}'.\n{Usage}");
                return 1;
        }
    }

    private static int List()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Drive listing needs Windows. Use --image or --simulate on this OS.");
            return 0;
        }

        Console.WriteLine("Drive  Type       Format  Size        Free        Label");
        foreach (var v in WindowsVolumeDevice.List())
        {
            Console.WriteLine($"{v.Letter,-6} {v.DriveType,-10} {v.Format,-7} {v.TotalBytes / 1e9,7:0.0} GB {v.FreeBytes / 1e9,7:0.0} GB  {v.Label}{(v.IsSystem ? "  (system - cannot be tested)" : "")}");
        }

        return 0;
    }

    private static int ListSimulations()
    {
        foreach (var (name, (desc, _)) in FakeScenarios.All)
        {
            Console.WriteLine($"{name,-26} {desc}");
        }

        return 0;
    }

    private static int Inspect(Args a)
    {
        string drive = a.Value("--drive") ?? throw new ArgumentException("--drive F: is required");
        using var dev = WindowsVolumeDevice.Open(drive, readOnly: true, a.Flag("--allow-fixed"), Console.WriteLine);
        Console.WriteLine($"{dev.Description}");
        Console.WriteLine($"Free ranges usable for tests: {dev.UsableExtents.Count}, total {dev.UsableExtents.Sum(e => e.Length) >> 20} MiB of {dev.Size >> 20} MiB");
        Console.WriteLine("All safety checks passed (read-only). Nothing was written except a temporary 1 MiB calibration file, now removed.");
        return 0;
    }

    private static int Check(Args a)
    {
        bool real = false;
        IBlockDevice dev;
        if (a.Value("--simulate") is { } sim)
        {
            if (!FakeScenarios.All.TryGetValue(sim, out var scenario))
            {
                throw new ArgumentException($"Unknown simulation '{sim}'. Run: sdchk simulate");
            }

            dev = new FakeBlockDevice(scenario.Options);
        }
        else if (a.Value("--image") is { } image)
        {
            long gib = long.Parse(a.Value("--image-gib") ?? "1");
            dev = new FileBlockDevice(image, gib << 30);
            real = true;
        }
        else
        {
            string drive = a.Value("--drive") ?? throw new ArgumentException("Give --drive F:, --image FILE or --simulate NAME");
            real = true;
            dev = null!;
            if (!Confirm(a))
            {
                return 1;
            }

            dev = WindowsVolumeDevice.Open(drive, readOnly: false, a.Flag("--allow-fixed"), Console.WriteLine);
        }

        if (real && dev is FileBlockDevice && !Confirm(a))
        {
            dev.Dispose();
            return 1;
        }

        using (dev)
        {
            return Execute(dev, a, simulated: !real);
        }
    }

    private static bool Confirm(Args a)
    {
        Console.WriteLine();
        Console.WriteLine(Disclaimer.Text);
        Console.WriteLine();
        if (a.Flag("--accept-risk"))
        {
            Console.WriteLine("(--accept-risk given)");
            return true;
        }

        Console.Write($"Type {Disclaimer.ConfirmPhrase} to continue: ");
        bool ok = string.Equals(Console.ReadLine()?.Trim(), Disclaimer.ConfirmPhrase, StringComparison.Ordinal);
        if (!ok)
        {
            Console.WriteLine("Not confirmed. Nothing was written.");
        }

        return ok;
    }

    private static int Execute(IBlockDevice dev, Args a, bool simulated)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("\nStopping...");
        };

        var log = new RunLog();
        var progress = ConsoleProgress();
        int probeMib = int.Parse(a.Value("--probe-mib") ?? "8");
        long seqMib = long.Parse(a.Value("--seq-mib") ?? (simulated ? "64" : "1024"));
        string? cls = a.Value("--class");
        if (cls is not null && SpeedClasses.Find(cls) is null)
        {
            throw new ArgumentException($"Unknown speed class '{cls}'.");
        }

        Console.WriteLine($"Device: {dev.Description}");
        log.Info($"Device: {dev.Description}");
        SizeResult? size = null;
        SpeedResult? speed = null;

        if (!a.Flag("--speed-only"))
        {
            size = SizeProbe.Run(dev, new SizeProbeOptions
            {
                ProbeBytes = probeMib << 20,
                ConfirmBytes = simulated ? 96L << 20 : 1L << 30,
                Log = log.Info,
                Progress = progress,
                Cancel = cts.Token,
            });
            Console.Error.Write("\r".PadRight(60) + "\r");
            Console.WriteLine($"SIZE : {size.Verdict.ToString().ToUpperInvariant()} - {size.Summary}");
        }

        bool skipSpeed = a.Flag("--size-only") || cts.IsCancellationRequested;
        if (!skipSpeed && size is { Verdict: Verdict.Fail })
        {
            log.Info("Speed test skipped: capacity is fake, speed positions would overwrite each other.");
            Console.WriteLine("SPEED: skipped (capacity is fake).");
            skipSpeed = true;
        }

        if (!skipSpeed)
        {
            speed = SpeedTest.Run(dev, new SpeedTestOptions
            {
                SequentialBytes = seqMib << 20,
                RandomOps = int.Parse(a.Value("--random-ops") ?? "1000"),
                ClaimedClass = cls,
                SampleSeconds = simulated ? 0.05 : 1.0,
                Log = log.Info,
                Progress = progress,
                Cancel = cts.Token,
            });
            Console.Error.Write("\r".PadRight(60) + "\r");
            Console.WriteLine($"SPEED: {speed.Verdict.ToString().ToUpperInvariant()} - {speed.Summary}");
            foreach (var n in speed.Notes)
            {
                Console.WriteLine($"  note: {n}");
            }
        }

        var report = new RunReport { Device = dev.Description, Mode = simulated ? "simulation" : "check", Size = size, Speed = speed };
        var (html, logPath) = ReportWriter.Write(a.Value("--out") ?? "results", report, log);
        Console.WriteLine($"Report: {Path.GetFullPath(html)}\nLog:    {Path.GetFullPath(logPath)}");

        var verdicts = new[] { size?.Verdict, speed?.Verdict }.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return verdicts.Any(v => v == Verdict.Fail) ? 2 : verdicts.All(v => v == Verdict.Pass) ? 0 : 3;
    }

    private static Action<string, double> ConsoleProgress()
    {
        long last = 0;
        return (what, fraction) =>
        {
            if (Console.IsErrorRedirected || Environment.TickCount64 - last < 200)
            {
                return;
            }

            last = Environment.TickCount64;
            Console.Error.Write($"\r{what,-34} {fraction * 100,5:0}%   ");
        };
    }

    private sealed class Args(string[] raw)
    {
        public bool Flag(string name) => raw.Contains(name, StringComparer.OrdinalIgnoreCase);

        public string? Value(string name)
        {
            int i = Array.FindIndex(raw, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < raw.Length ? raw[i + 1] : null;
        }
    }
}
