// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SdChk.Core;
using SdChk.Core.Devices;
using SdChk.Windows;

namespace SdChk.Ui;

public sealed record UiOptions(int Port = 5199, bool OpenBrowser = true, string? JournalDirectory = null);

public sealed record OpenRequest(string Source, string? Id, string? Letter, string? Path, int? Gib, string? Confirm, bool AllowFixed, bool StepMode, bool SlowMotion);

public sealed record RecoverRequest(string JournalPath, string Source, string? Letter, string? Path, int? Gib, string? Confirm, bool AllowFixed);

/// <summary>Local-only web UI for the map prototype. Bound to 127.0.0.1, protected by a per-run token.</summary>
public sealed class UiServer
{
    private readonly UiOptions _o;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly object _lock = new();
    private MapSession? _session;
    private IBlockDevice? _device;

    public UiServer(UiOptions options) => _o = options;

    public string Url => $"http://127.0.0.1:{_o.Port}/?t={_token}";

    private string JournalDir => _o.JournalDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } local ? local : Path.GetTempPath(),
        "sdchk", "journals");

    public async Task RunAsync(CancellationToken cancel)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{_o.Port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(j =>
        {
            j.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            j.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            j.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        });
        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path == "/favicon.ico")
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            string host = ctx.Request.Host.Value ?? "";
            bool hostOk = host == $"127.0.0.1:{_o.Port}" || host == $"localhost:{_o.Port}";
            bool tokenOk = ctx.Request.Path.StartsWithSegments("/api")
                ? ctx.Request.Headers["X-SdChk-Token"] == _token
                : ctx.Request.Query["t"] == _token;
            if (!hostOk || !tokenOk)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("Forbidden");
                return;
            }

            await next();
        });

        app.MapGet("/", async ctx =>
        {
            using var s = typeof(UiServer).Assembly.GetManifestResourceStream("index.html")!;
            using var r = new StreamReader(s);
            string html = (await r.ReadToEndAsync()).Replace("{{TOKEN}}", _token);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            await ctx.Response.WriteAsync(html);
        });

        app.MapGet("/api/info", () => new
        {
            version = typeof(UiServer).Assembly.GetName().Version?.ToString(),
            windows = OperatingSystem.IsWindows(),
            disclaimer = Disclaimer.Text,
            confirmPhrase = Disclaimer.ConfirmPhrase,
            journalDirectory = JournalDir,
            sims = FakeScenarios.All.Select(kv => new { id = kv.Key, description = kv.Value.Description, claimedBytes = kv.Value.Options.ClaimedSize }),
            drives = OperatingSystem.IsWindows() ? WindowsVolumeDevice.List().Where(v => !v.IsSystem) : Enumerable.Empty<VolumeInfo>(),
        });

        app.MapPost("/api/open", (OpenRequest req) => Open(req));
        app.MapPost("/api/start", () => WithSession(s => s.Start()));
        app.MapPost("/api/next", () => WithSession(s => s.Continue()));
        app.MapPost("/api/cancel", () => WithSession(s => s.Cancel()));
        app.MapGet("/api/state", () => Results.Json(CurrentSnapshot()));
        app.MapGet("/api/report", () => Report(html: true));
        app.MapGet("/api/log", () => Report(html: false));
        app.MapGet("/api/journals", () =>
        {
            Directory.CreateDirectory(JournalDir);
            return Directory.GetFiles(JournalDir, "*.sdchkj").Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new { path = f.FullName, bytes = f.Length, modified = f.LastWriteTimeUtc });
        });
        app.MapPost("/api/recover", (RecoverRequest req) => Recover(req));

        if (_o.OpenBrowser)
        {
            app.Lifetime.ApplicationStarted.Register(() => TryOpenBrowser(Url));
        }

        await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(app, cancel);
        lock (_lock)
        {
            Dispose();
        }
    }

    private IResult Open(OpenRequest req)
    {
        lock (_lock)
        {
            if (_session is { } cur && !cur.Snapshot().Finished && cur.Completion is not null)
            {
                return Results.BadRequest(new { error = "A test is still running. Stop it first." });
            }

            Dispose();
            try
            {
                bool real = req.Source != "sim";
                if (real && req.Confirm != Disclaimer.ConfirmPhrase)
                {
                    return Results.BadRequest(new { error = $"Type {Disclaimer.ConfirmPhrase} to confirm you read the warning." });
                }

                var (dev, root, probe, delay) = CreateDevice(req.Source, req.Id, req.Letter, req.Path, req.Gib, req.AllowFixed, req.SlowMotion);
                _device = dev;
                _session = new MapSession(dev, new MapSessionOptions
                {
                    StepMode = req.StepMode,
                    AreaDelayMs = delay,
                    JournalDirectory = JournalDir,
                    DeviceRoot = root,
                    Probe = probe,
                });
                return Results.Json(new { layout = _session.Layout, description = dev.Description });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException or KeyNotFoundException)
            {
                Dispose();
                return Results.BadRequest(new { error = ex.Message });
            }
        }
    }

    private static (IBlockDevice Dev, string? Root, SizeProbeOptions Probe, int Delay) CreateDevice(string source, string? id, string? letter, string? path, int? gib, bool allowFixed, bool slow)
    {
        switch (source)
        {
            case "sim":
                var card = FakeScenarios.All[id ?? throw new ArgumentException("Choose a demo card.")].Options;
                return (new FakeBlockDevice(card), null,
                    new SizeProbeOptions { ProbeBytes = 4 << 20, CacheDefeatBytes = 16 << 20, ConfirmBytes = 32 << 20 }, slow ? 30 : 0);
            case "image":
                string file = path ?? throw new ArgumentException("Give an image file path.");
                var img = new FileBlockDevice(file, (long)(gib ?? 1) << 30);
                return (img, null, new SizeProbeOptions { ProbeBytes = 2 << 20, CacheDefeatBytes = 8 << 20, ConfirmBytes = 16 << 20 }, 0);
            case "drive":
                string drive = letter ?? throw new ArgumentException("Choose a drive.");
                var dev = WindowsVolumeDevice.Open(drive, readOnly: false, allowFixed);
                return (dev, drive.TrimEnd('\\') + "\\", new SizeProbeOptions(), 0);
            default:
                throw new ArgumentException("Unknown source.");
        }
    }

    private IResult WithSession(Action<MapSession> action)
    {
        lock (_lock)
        {
            if (_session is null)
            {
                return Results.BadRequest(new { error = "Open a card first." });
            }

            try
            {
                action(_session);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }
    }

    private object CurrentSnapshot()
    {
        lock (_lock)
        {
            return _session is null ? new { stage = "Idle", message = "Choose a card to begin." } : _session.Snapshot();
        }
    }

    private IResult Report(bool html)
    {
        lock (_lock)
        {
            if (_session is null || !_session.Snapshot().Finished)
            {
                return Results.BadRequest(new { error = "The report is available when the test has finished." });
            }

            var (h, l) = _session.BuildReport();
            return html ? Results.Content(h, "text/html; charset=utf-8") : Results.Content(l, "text/plain; charset=utf-8");
        }
    }

    private IResult Recover(RecoverRequest req)
    {
        lock (_lock)
        {
            if (_session is { } cur && !cur.Snapshot().Finished && cur.Completion is not null)
            {
                return Results.BadRequest(new { error = "A test is running." });
            }

            if (req.Confirm != Disclaimer.ConfirmPhrase)
            {
                return Results.BadRequest(new { error = $"Type {Disclaimer.ConfirmPhrase} to confirm." });
            }

            try
            {
                Dispose();
                var (dev, _, _, _) = CreateDevice(req.Source, null, req.Letter, req.Path, req.Gib, req.AllowFixed, false);
                using (dev)
                using (var journal = Journal.Open(req.JournalPath))
                {
                    if (journal.Header.DeviceSize != dev.Size)
                    {
                        return Results.BadRequest(new { error = "This journal belongs to a card of a different size." });
                    }

                    var result = journal.Restore(dev);
                    return Results.Json(new { ok = result.Ok, entries = result.Entries, bytes = result.Bytes, failures = result.Failures });
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }
    }

    private void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _device?.Dispose();
        _device = null;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("Open this address in your browser: " + url);
        }
    }
}
