using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MacCheck.Core;

/// <summary>One supplied test PDF (from E2's corpus) and what it should parse to.</summary>
public sealed record PdfCase(string Id, string PdfFile, string Title, bool Mandatory, string WhatItTests, Expected Expected);

/// <summary>
/// The E2 corpus as Mac Check uses it. Three mandatory cases — a straightforward d20 (c01), a four-column d100 (c02) and a
/// two-column d66 (c05) — then the other five as optional. The order of the optional ones follows how much they told E2
/// apart readers: open-ended ranges and heading/two columns (Edge failed both), wrapped text, hyphenation, characters.
/// </summary>
public static class PdfCaseCatalog
{
    public static readonly string[] MandatoryIds = ["c01-d20-simple", "c02-d100-four-columns", "c05-d66-two-columns"];
    public static readonly string[] OptionalIds =
        ["c07-open-ended-ranges", "c08-heading-two-result-columns", "c03-wrapped-text", "c04-hyphenation", "c06-special-characters"];

    private static readonly Dictionary<string, string> Purpose = new()
    {
        ["c01-d20-simple"] = "A plain d20 list: number, then result.",
        ["c02-d100-four-columns"] = "A d100 table printed in four columns side by side.",
        ["c05-d66-two-columns"] = "A d66 table (11–66) in two columns.",
        ["c07-open-ended-ranges"] = "Ranges like −4 or less and 25+ with a real minus sign.",
        ["c08-heading-two-result-columns"] = "A heading plus two result columns (text and modifier).",
        ["c03-wrapped-text"] = "Results that wrap onto two or three lines.",
        ["c04-hyphenation"] = "Words broken across lines with hyphens.",
        ["c06-special-characters"] = "Ligatures, curly quotes, accents and symbols.",
    };

    public static List<PdfCase> Load(string corpusFolder)
    {
        var expected = PdfCaseEvaluator.LoadExpected(corpusFolder);
        var list = new List<PdfCase>();
        foreach (var id in MandatoryIds.Concat(OptionalIds))
        {
            var exp = expected[id];
            list.Add(new PdfCase(id, Path.Combine(corpusFolder, id, id + ".pdf"), exp.TableName, MandatoryIds.Contains(id), Purpose[id], exp));
        }
        return list;
    }
}

/// <summary>
/// Privacy rule for clipboard text: only text that is recognisably the supplied test PDF is ever kept. The table's title
/// must appear in what was pasted (case, quote style and spacing ignored); anything else is thrown away unstored, and only
/// the fact that something unexpected was pasted is recorded.
/// </summary>
public static class ClipboardGuard
{
    public const int MaxLength = 20_000;

    public static bool IsTestContent(PdfCase pdfCase, string? text) =>
        text is { Length: > 0 and <= MaxLength } && Fold(text).Contains(Fold(pdfCase.Title), StringComparison.Ordinal);

    private static string Fold(string s)
    {
        s = s.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture)
             .Replace('’', '\'').Replace('‘', '\'').Replace('`', '\'').Replace('´', '\'');
        return Regex.Replace(s, @"\s+", " ");
    }
}

/// <summary>What was recorded for one PDF.</summary>
public sealed class PdfCaseResult
{
    public required string Case { get; init; }
    public required bool Mandatory { get; init; }
    public Outcome Outcome { get; set; } = Outcome.UnableToTest;
    /// <summary>The E2 verdict of the RAW text exactly as pasted (PASS / TEXT-DIFF / FAIL-flagged / FAIL-silent).</summary>
    public string? RawVerdict { get; set; }
    /// <summary>The optional cleanup pipelines, reported separately; never used to change <see cref="Outcome"/>.</summary>
    public Dictionary<string, string> CleanupVerdicts { get; set; } = [];
    public int? ExpectedRows { get; set; }
    public int? ParsedRows { get; set; }
    public int? RowsMatched { get; set; }
    public List<string> Mismatches { get; set; } = [];
    public List<string> ParserIssues { get; set; } = [];
    public JsonObject? Characters { get; set; }
    public string? PasteMethod { get; set; }
    public bool ContentRejected { get; set; }
    public string? Note { get; set; }
    public double? Seconds { get; set; }
    /// <summary>File name of the captured text inside the results package (test content only).</summary>
    public string? CaptureFile { get; set; }
    /// <summary>E2 readers that produced the same raw verdict for this PDF (context for reading the Mac result).</summary>
    public List<string> SameVerdictAsE2Readers { get; set; } = [];

    /// <summary>
    /// Mapping to the four outcomes. PASS = the raw paste parses to exactly the expected table. TEXT-DIFF (structure right,
    /// some characters differ) is INCONCLUSIVE for the session and keeps its E2 verdict for analysis. FAIL-flagged and
    /// FAIL-silent are FAIL.
    /// </summary>
    public static Outcome FromVerdict(string verdict) => verdict switch
    {
        "PASS" => Outcome.Pass,
        "TEXT-DIFF" => Outcome.Inconclusive,
        _ => Outcome.Fail,
    };

    public static PdfCaseResult Evaluate(PdfCase c, string pasted, string pasteMethod, double seconds)
    {
        var results = PdfCaseEvaluator.EvaluateAll(pasted, c.Expected);
        var raw = results.Single(r => r.Pipeline == "parse");
        return new PdfCaseResult
        {
            Case = c.Id,
            Mandatory = c.Mandatory,
            Outcome = FromVerdict(raw.Verdict),
            RawVerdict = raw.Verdict,
            CleanupVerdicts = results.Where(r => r.Pipeline != "parse").ToDictionary(r => r.Pipeline, r => r.Verdict),
            ExpectedRows = raw.ExpectedRows,
            ParsedRows = raw.ActualRows,
            RowsMatched = raw.RowsMatched,
            Mismatches = raw.Mismatches.Take(40).Select(m => $"{m.Kind} {m.Where}: expected «{PdfCaseEvaluator.Visible(m.ExpectedValue)}» got «{PdfCaseEvaluator.Visible(m.ActualValue)}»{(m.Tags.Length > 0 ? $" [{m.Tags}]" : "")}").ToList(),
            ParserIssues = raw.Snapshot.Split('\n').Where(l => l.StartsWith("ISSUE ", StringComparison.Ordinal)).Take(40).ToList(),
            Characters = PdfCaseEvaluator.Inventory(pasted),
            PasteMethod = pasteMethod,
            Seconds = Math.Round(seconds, 1),
            CaptureFile = $"e2/{c.Id}.txt",
            SameVerdictAsE2Readers = E2Baselines.SameAs(c.Id, raw.Verdict),
        };
    }
}
