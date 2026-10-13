// MacCheck.Analyze <results.zip | backup.json> [corpusDir] [E2-results.json]
// Prints a Markdown report: per case, the verdict of the returned text (raw and with TableForge's cleanups), plus what
// E2 measured for the same PDF in other readers, so a Mac result can be read against Windows/Linux baselines.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MacCheck.Core;

var input = args.Length > 0 ? args[0] : throw new ArgumentException("usage: MacCheck.Analyze <results.zip|backup.json> [corpusDir] [E2-results.json]");
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var corpus = args.Length > 1 ? args[1] : Path.Combine(root, "corpus");
var e2 = args.Length > 2 ? args[2] : Path.Combine(root, "tests", "MacCheck.Tests", "e2-fixtures", "E2-results.json");
var expected = PdfCaseEvaluator.LoadExpected(corpus);

var captures = new Dictionary<string, string>();
string source;
if (input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
{
    using var zip = ZipFile.OpenRead(input);
    foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("e2/") && e.FullName.EndsWith(".txt")))
        captures[Path.GetFileNameWithoutExtension(e.Name)] = new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd();
    using var json = JsonDocument.Parse(new StreamReader(zip.GetEntry("results.json")!.Open()).ReadToEnd());
    source = $"results package, build {json.RootElement.GetProperty("build").GetProperty("buildId")}, system {(json.RootElement.TryGetProperty("system", out var s) ? s.ToString() : "not shared")}";
}
else
{
    using var json = JsonDocument.Parse(File.ReadAllText(input));
    foreach (var c in json.RootElement.GetProperty("cases").EnumerateObject()) captures[c.Name] = c.Value.GetProperty("text").GetString()!;
    source = $"Safari backup page (REDUCED: no app), user agent {(json.RootElement.GetProperty("userAgent").ValueKind == JsonValueKind.String ? json.RootElement.GetProperty("userAgent").GetString() : "not shared")}";
}

using var baselineDoc = File.Exists(e2) ? JsonDocument.Parse(File.ReadAllText(e2)) : null;
string Baseline(string id) => baselineDoc is null ? "" : string.Join(", ", baselineDoc.RootElement.EnumerateArray()
    .Where(r => r.GetProperty("case").GetString() == id && r.GetProperty("env").GetString() is "windows" or "linux")
    .Select(r => $"{r.GetProperty("env").GetString()}/{r.GetProperty("reader").GetString()} {r.GetProperty("parse").GetProperty("verdict").GetString()}"));

var sb = new StringBuilder($"# Mac Check E2 analysis\n\nSource: {source}\n\n| Case | Raw parse | Review cleanup | Paste cleanup | Rows | Line endings | Non-ASCII | E2 baselines (raw) |\n|---|---|---|---|---|---|---|---|\n");
foreach (var id in PdfCaseCatalog.MandatoryIds.Concat(PdfCaseCatalog.OptionalIds))
{
    if (!captures.TryGetValue(id, out var text)) { sb.Append($"| {id} | not captured | | | | | | {Baseline(id)} |\n"); continue; }
    var r = PdfCaseEvaluator.EvaluateAll(text, expected[id]);
    var inv = PdfCaseEvaluator.Inventory(text);
    sb.Append($"| {id} | {r[0].Verdict} | {r[1].Verdict} | {r[2].Verdict} | {r[0].ActualRows}/{r[0].ExpectedRows} | {inv["lineEnding"]} | {inv["nonAscii"]?.ToJsonString()} | {Baseline(id)} |\n");
}
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine(sb.ToString());
