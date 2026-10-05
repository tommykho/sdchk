// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SdChk.Core;

public sealed class RunLog
{
    private readonly List<string> _lines = new();
    private readonly DateTime _start = DateTime.UtcNow;

    public IReadOnlyList<string> Lines => _lines;

    public void Info(string message) =>
        _lines.Add($"[{(DateTime.UtcNow - _start).TotalSeconds,8:0.0}s] {message}");
}

public sealed class RunReport
{
    public string ToolVersion { get; init; } = typeof(RunReport).Assembly.GetName().Version?.ToString() ?? "0.1.0";

    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public string Device { get; init; } = "";

    public string Mode { get; init; } = "";

    public SizeResult? Size { get; init; }

    public SpeedResult? Speed { get; init; }
}

public static class ReportWriter
{
    private static string F(double v, string fmt = "0.0") => v.ToString(fmt, CultureInfo.InvariantCulture);

    public static string BuildLog(RunReport r, RunLog log)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"sdchk {r.ToolVersion} - result log");
        sb.AppendLine($"Time (UTC): {r.TimestampUtc:u}");
        sb.AppendLine($"Device: {r.Device}");
        sb.AppendLine($"Mode: {r.Mode}");
        sb.AppendLine();
        sb.AppendLine(Disclaimer.Text);
        sb.AppendLine();
        foreach (var line in log.Lines)
        {
            sb.AppendLine(line);
        }

        sb.AppendLine();
        if (r.Size is { } s)
        {
            sb.AppendLine($"SIZE: {s.Verdict.ToString().ToUpperInvariant()} - {s.Summary}");
            sb.AppendLine($"  claimed {SizeProbe.Fmt(s.ClaimedSize)}; estimated real {(s.EstimatedRealSize is { } e ? SizeProbe.Fmt(e) : "n/a")}; wrap-around {(s.WrapAround ? "yes" : "no")}");
            sb.AppendLine($"  probes {s.Probes.Count} x {s.ProbeBytes >> 20} MiB, scan reads {s.ScanReads}, alias hits {s.AliasHitCount}, written {s.BytesWritten >> 20} MiB");
            foreach (var h in s.AliasHits.Take(10))
            {
                sb.AppendLine($"  alias: data written for offset {h.FoundOffset} was found at offset {h.ReadOffset}");
            }

            foreach (var n in s.Notes)
            {
                sb.AppendLine($"  note: {n}");
            }
        }

        if (r.Speed is { } sp)
        {
            sb.AppendLine($"SPEED: {sp.Verdict.ToString().ToUpperInvariant()} - {sp.Summary}");
            foreach (var p in sp.Positions)
            {
                sb.AppendLine($"  {p.Label} @ {p.Offset >> 20} MiB: write min {F(p.Write.Min)} p5 {F(p.Write.P5)} median {F(p.Write.Median)} max {F(p.Write.Max)} avg {F(p.Write.Mean)} MB/s; read median {F(p.Read.Median)} avg {F(p.Read.Mean)} MB/s");
                if (p.DropAfterBytes is { } d)
                {
                    sb.AppendLine($"    write speed dropped after {d >> 20} MiB");
                }
            }

            if (sp.Random is { } rr)
            {
                sb.AppendLine($"  random 4 KiB Q1: write {F(rr.WriteIops, "0")} IOPS (p95 {F(rr.WriteP95Ms)} ms), read {F(rr.ReadIops, "0")} IOPS (p95 {F(rr.ReadP95Ms)} ms)");
                sb.AppendLine($"  indicative A1 numbers: {YesNo(sp.MeetsA1)}; A2 numbers: {YesNo(sp.MeetsA2)}");
            }

            foreach (var n in sp.Notes)
            {
                sb.AppendLine($"  note: {n}");
            }
        }

        return sb.ToString();
    }

    private static string YesNo(bool? v) => v is null ? "n/a" : v.Value ? "yes" : "no";

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string BuildHtml(RunReport r, string logText)
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        string Badge(Verdict v) => $"<span class=\"badge {v.ToString().ToLowerInvariant()}\">{v.ToString().ToUpperInvariant()}</span>";
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine("<title>sdchk result</title><style>");
        sb.AppendLine("body{font-family:Segoe UI,system-ui,sans-serif;margin:24px auto;max-width:880px;padding:0 16px;color:#1a1a1a;background:#fff}");
        sb.AppendLine("h1{font-size:1.5rem}h2{font-size:1.15rem;margin-top:1.6rem}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:6px 8px;text-align:left;font-size:.92rem}");
        sb.AppendLine(".warn{background:#fff4d6;border:1px solid #e0b000;padding:10px 12px;border-radius:6px}.badge{padding:2px 10px;border-radius:12px;color:#fff;font-weight:600}");
        sb.AppendLine(".pass{background:#1b7f3b}.fail{background:#b3261e}.suspect{background:#b26a00}.inconclusive{background:#555}pre{background:#f4f4f4;padding:10px;overflow:auto;font-size:.8rem}");
        sb.AppendLine("@media(prefers-color-scheme:dark){body{background:#161616;color:#eee}td,th{border-color:#444}pre{background:#222}.warn{background:#3a3000}}</style></head><body>");
        sb.AppendLine("<h1>sdchk result</h1>");
        sb.AppendLine($"<p>Tool version {E(r.ToolVersion)} &middot; {r.TimestampUtc:u} &middot; {E(r.Device)} &middot; mode {E(r.Mode)}</p>");
        sb.AppendLine($"<p class=\"warn\">{E(Disclaimer.Text)}</p>");

        if (r.Size is { } s)
        {
            sb.AppendLine($"<h2>Size {Badge(s.Verdict)}</h2><p>{E(s.Summary)}</p><table>");
            sb.AppendLine($"<tr><th>Claimed capacity</th><td>{E(SizeProbe.Fmt(s.ClaimedSize))}</td></tr>");
            sb.AppendLine($"<tr><th>Estimated real capacity</th><td>{(s.EstimatedRealSize is { } e ? E(SizeProbe.Fmt(e)) : "n/a")}</td></tr>");
            sb.AppendLine($"<tr><th>Wrap-around detected</th><td>{(s.WrapAround ? "yes" : "no")}</td></tr>");
            sb.AppendLine($"<tr><th>Probes</th><td>{s.Probes.Count} x {s.ProbeBytes >> 20} MiB; {s.ScanReads} scan reads; cache flush {s.CacheDefeatBytes >> 20} MiB</td></tr>");
            sb.AppendLine($"<tr><th>Misplaced blocks found</th><td>{s.AliasHitCount}</td></tr></table>");
            if (s.AliasHits.Count > 0)
            {
                sb.AppendLine("<h3>Examples of misplaced data</h3><table><tr><th>Found at offset</th><th>Was written for offset</th></tr>");
                foreach (var h in s.AliasHits.Take(10))
                {
                    sb.AppendLine($"<tr><td>{h.ReadOffset}</td><td>{h.FoundOffset}</td></tr>");
                }

                sb.AppendLine("</table>");
            }

            sb.AppendLine("<h3>Probes</h3><table><tr><th>Offset (MiB)</th><th>Good</th><th>Corrupted</th><th>Changed</th><th>Blank</th><th>Unreadable</th></tr>");
            foreach (var p in s.Probes)
            {
                sb.AppendLine($"<tr><td>{p.Offset >> 20}</td><td>{p.Good}</td><td>{p.Corrupted}</td><td>{p.Changed}</td><td>{p.Blank}</td><td>{p.Unreadable + (p.WriteFailed ? 1 : 0)}</td></tr>");
            }

            sb.AppendLine("</table>");
            foreach (var n in s.Notes)
            {
                sb.AppendLine($"<p>Note: {E(n)}</p>");
            }
        }

        if (r.Speed is { } sp)
        {
            sb.AppendLine($"<h2>Speed {Badge(sp.Verdict)}</h2><p>{E(sp.Summary)}</p>");
            sb.AppendLine("<table><tr><th>Position</th><th>Write min</th><th>Write p5</th><th>Write median</th><th>Write max</th><th>Read median</th><th>Write drop</th></tr>");
            foreach (var p in sp.Positions)
            {
                string drop = p.DropAfterBytes is { } d ? $"after {d >> 20} MiB" : "none seen";
                sb.AppendLine($"<tr><td>{E(p.Label)}</td><td>{F(p.Write.Min)}</td><td>{F(p.Write.P5)}</td><td>{F(p.Write.Median)}</td><td>{F(p.Write.Max)}</td><td>{F(p.Read.Median)}</td><td>{drop}</td></tr>");
            }

            sb.AppendLine("</table><p>All figures MB/s (1 MB = 1,000,000 bytes).</p>");
            foreach (var p in sp.Positions.Where(x => x.WriteSeries.Count > 1))
            {
                sb.AppendLine(Chart(p));
            }

            if (sp.Random is { } rr)
            {
                sb.AppendLine($"<p>Random 4 KiB, queue depth 1: write {F(rr.WriteIops, "0")} IOPS, read {F(rr.ReadIops, "0")} IOPS. Indicative A1 numbers: {YesNo(sp.MeetsA1)}; A2: {YesNo(sp.MeetsA2)}.</p>");
            }

            foreach (var n in sp.Notes)
            {
                sb.AppendLine($"<p>Note: {E(n)}</p>");
            }
        }

        sb.AppendLine($"<h2>Log</h2><pre>{E(logText)}</pre><p>SHA-256 of result.log: <code>{Sha256(logText)}</code></p></body></html>");
        return sb.ToString();
    }

    private static string Chart(PositionSpeed p)
    {
        const int w = 600, h = 120;
        double max = Math.Max(1, p.WriteSeries.Max());
        var pts = p.WriteSeries.Select((v, i) =>
            $"{F(i * (w - 1.0) / Math.Max(1, p.WriteSeries.Count - 1), "0.#")},{F(h - 4 - v / max * (h - 8), "0.#")}");
        return $"<p>Write speed over time, {WebUtility.HtmlEncode(p.Label)} (peak {F(max)} MB/s)</p>" +
               $"<svg viewBox=\"0 0 {w} {h}\" width=\"100%\" role=\"img\" aria-label=\"write speed over time\"><rect width=\"{w}\" height=\"{h}\" fill=\"none\" stroke=\"#888\"/>" +
               $"<polyline fill=\"none\" stroke=\"#3a7bd5\" stroke-width=\"2\" points=\"{string.Join(' ', pts)}\"/></svg>";
    }

    public static (string Html, string Log) Write(string directory, RunReport report, RunLog log)
    {
        Directory.CreateDirectory(directory);
        string logText = BuildLog(report, log);
        string html = BuildHtml(report, logText);
        string logPath = Path.Combine(directory, "result.log");
        string htmlPath = Path.Combine(directory, "result.html");
        File.WriteAllText(logPath, logText, Encoding.UTF8);
        File.WriteAllText(htmlPath, html, Encoding.UTF8);
        return (htmlPath, logPath);
    }
}
