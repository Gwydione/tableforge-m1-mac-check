// MacCheck.Analyze <results.zip | backup.json | captures-folder> [--label TEXT] [--corpus DIR] [--e2 E2-results.json]
// MacCheck.Analyze --compare <A> <B> [--label-a TEXT] [--label-b TEXT] [--corpus DIR]
// Prints a Markdown report: per case, the verdict of the returned text (raw and with TableForge's cleanups), row coverage
// and order of the raw parse (and any row problem the parser did not warn about), plus what E2 measured for the same PDF
// in other readers, so a Mac result can be read against Windows/Linux baselines. A captures folder holds <case>.txt files
// (for example the runner's Preview captures). --compare puts two sources side by side (for example Apple Silicon vs Intel).
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MacCheck.Core;

string[] valued = ["--label", "--corpus", "--e2", "--label-a", "--label-b"];
string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var positional = args.Where((a, i) => !a.StartsWith("--") && (i == 0 || !valued.Contains(args[i - 1]))).ToList();
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var corpus = Opt("--corpus") ?? Path.Combine(root, "corpus");
var e2 = Opt("--e2") ?? Path.Combine(root, "tests", "MacCheck.Tests", "e2-fixtures", "E2-results.json");
var expected = PdfCaseEvaluator.LoadExpected(corpus);
var ids = PdfCaseCatalog.MandatoryIds.Concat(PdfCaseCatalog.OptionalIds).ToList();
Console.OutputEncoding = Encoding.UTF8;

(Dictionary<string, string> Captures, string Source) Load(string input, string? label)
{
    var captures = new Dictionary<string, string>();
    string source;
    if (Directory.Exists(input))
    {
        foreach (var id in ids)
            if (File.Exists(Path.Combine(input, id + ".txt"))) captures[id] = File.ReadAllText(Path.Combine(input, id + ".txt"), new UTF8Encoding(false));
        source = $"captures folder{(label is null ? "" : ", " + label)}";
    }
    else if (input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
    {
        using var zip = ZipFile.OpenRead(input);
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("e2/") && e.FullName.EndsWith(".txt")))
            captures[Path.GetFileNameWithoutExtension(e.Name)] = new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd();
        using var json = JsonDocument.Parse(new StreamReader(zip.GetEntry("results.json")!.Open()).ReadToEnd());
        source = $"results package{(label is null ? "" : ", " + label)}, build {json.RootElement.GetProperty("build").GetProperty("buildId")}, system {(json.RootElement.TryGetProperty("system", out var s) ? s.ToString() : "not shared")}";
    }
    else
    {
        using var json = JsonDocument.Parse(File.ReadAllText(input));
        foreach (var c in json.RootElement.GetProperty("cases").EnumerateObject()) captures[c.Name] = c.Value.GetProperty("text").GetString()!;
        source = $"Safari backup page (REDUCED: no app){(label is null ? "" : ", " + label)}, user agent {(json.RootElement.GetProperty("userAgent").ValueKind == JsonValueKind.String ? json.RootElement.GetProperty("userAgent").GetString() : "not shared")}";
    }
    return (captures, source);
}

(string Verdict, PipelineResult[] All, RowReport Rows)? Evaluate(Dictionary<string, string> captures, string id)
{
    if (!captures.TryGetValue(id, out var text)) return null;
    var r = PdfCaseEvaluator.EvaluateAll(text, expected[id]).ToArray();
    return (r[0].Verdict, r, RowReport.ForRaw(text, expected[id], r[0].Flagged));
}

var sb = new StringBuilder();
if (args.Contains("--compare"))
{
    if (positional.Count < 2) throw new ArgumentException("usage: MacCheck.Analyze --compare <A> <B> [--label-a TEXT] [--label-b TEXT]");
    var (la, lb) = (Opt("--label-a") ?? "A", Opt("--label-b") ?? "B");
    var a = Load(positional[0], la);
    var b = Load(positional[1], lb);
    sb.Append($"# Mac Check E2 comparison\n\n{la}: {a.Source}\n\n{lb}: {b.Source}\n\n");
    sb.Append($"| Case | {la} raw | {la} rows found, order | {lb} raw | {lb} rows found, order | Same text | Same verdict |\n|---|---|---|---|---|---|---|\n");
    foreach (var id in ids)
    {
        var ra = Evaluate(a.Captures, id);
        var rb = Evaluate(b.Captures, id);
        static string Cell((string Verdict, PipelineResult[] All, RowReport Rows)? r) => r is not { } v ? "not captured | —"
            : $"{v.Verdict} | {v.Rows.Coverage}, {v.Rows.Order}{(v.Rows.Silent is { } s ? $" (**{s}**)" : "")}";
        var sameText = a.Captures.TryGetValue(id, out var ta) && b.Captures.TryGetValue(id, out var tb) ? (ta == tb ? "yes" : "**no**") : "—";
        var sameVerdict = ra is null || rb is null ? "—" : ra.Value.Verdict == rb.Value.Verdict ? "yes" : "**no**";
        sb.Append($"| {id} | {Cell(ra)} | {Cell(rb)} | {sameText} | {sameVerdict} |\n");
    }
}
else
{
    if (positional.Count < 1) throw new ArgumentException("usage: MacCheck.Analyze <results.zip|backup.json|captures-folder> [--label TEXT] [--corpus DIR] [--e2 FILE]");
    var (captures, source) = Load(positional[0], Opt("--label"));
    using var baselineDoc = File.Exists(e2) ? JsonDocument.Parse(File.ReadAllText(e2)) : null;
    string Baseline(string id) => baselineDoc is null ? "" : string.Join(", ", baselineDoc.RootElement.EnumerateArray()
        .Where(r => r.GetProperty("case").GetString() == id && r.GetProperty("env").GetString() is "windows" or "linux")
        .Select(r => $"{r.GetProperty("env").GetString()}/{r.GetProperty("reader").GetString()} {r.GetProperty("parse").GetProperty("verdict").GetString()}"));

    sb.Append($"# Mac Check E2 analysis\n\nSource: {source}\n\n| Case | Raw parse | Review cleanup | Paste cleanup | Rows | Rows found | Row order | Not warned about | Line endings | Non-ASCII | E2 baselines (raw) |\n|---|---|---|---|---|---|---|---|---|---|---|\n");
    foreach (var id in ids)
    {
        if (Evaluate(captures, id) is not { } r) { sb.Append($"| {id} | not captured | | | | | | | | | {Baseline(id)} |\n"); continue; }
        var inv = PdfCaseEvaluator.Inventory(captures[id]);
        sb.Append($"| {id} | {r.All[0].Verdict} | {r.All[1].Verdict} | {r.All[2].Verdict} | {r.All[0].ActualRows}/{r.All[0].ExpectedRows} | {r.Rows.Coverage} | {r.Rows.Order} | {r.Rows.Silent ?? ""} | {inv["lineEnding"]} | {inv["nonAscii"]?.ToJsonString()} | {Baseline(id)} |\n");
    }
}
Console.WriteLine(sb.ToString());
