// Ported from TableForge E2's harness (experiment E2, harness/E2Harness/Program.cs; see PROVENANCE.md):
// the same pipelines, comparison, verdicts and character inventory, generated from that file by text extraction so the
// classification rules are identical. Only visibility, namespace and one record name (Outcome -> ParsedOutcome) were adapted.
//
// Verdicts: PASS = exactly the expected table; TEXT-DIFF = every row, range, set and heading right but some text differs;
// FAIL-flagged = structure wrong and the parser raised a warning/error; FAIL-silent = structure wrong with no warning.
// Pipelines: "parse" is the RAW outcome (text exactly as pasted). "review-cleanup" and "paste-cleanup" are the optional
// TableForge cleanup tools, always reported separately and never used to repair the raw verdict.

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TableForge.Import;

namespace MacCheck.Core;

public sealed record ExpectedEntry(string Range, string Text);
public sealed record ExpectedSet(string Name, List<ExpectedEntry> Entries);
public sealed record Expected(string Case, string TableName, string Dice, List<ExpectedSet> Sets);
public sealed record ParsedOutcome(string TableName, string Dice, List<(string Name, List<(string Range, string Text)> Entries)> Sets, List<ParseIssue> Issues, List<string> SaveErrors);
public sealed record Mismatch(string Kind, string Where, string ExpectedValue, string ActualValue, string Tags);
public sealed record PipelineResult(string Pipeline, string Verdict, bool StructureOk, bool TextOk, int ExpectedRows, int ActualRows,
    int RowsMatched, bool Flagged, int Errors, int Warnings, int Infos, bool WouldSave, List<Mismatch> Mismatches, string Snapshot);

public static class PdfCaseEvaluator
{
    public static readonly string[] Pipelines = ["parse", "review-cleanup", "paste-cleanup"];

    public static List<PipelineResult> EvaluateAll(string raw, Expected exp) => Pipelines.Select(p => Evaluate(p, raw, exp)).ToList();

    public static Dictionary<string, Expected> LoadExpected(string corpus)
    {
        var map = new Dictionary<string, Expected>();
        foreach (var file in Directory.GetFiles(corpus, "expected.json", SearchOption.AllDirectories))
        {
            var j = JsonNode.Parse(File.ReadAllText(file))!;
            var e = new Expected((string)j["case"]!, (string)j["tableName"]!, (string)j["dice"]!,
                j["sets"]!.AsArray().Select(s => new ExpectedSet((string)s!["name"]!,
                    s["entries"]!.AsArray().Select(x => new ExpectedEntry((string)x!["range"]!, (string)x["text"]!)).ToList())).ToList());
            map[e.Case] = e;
        }
        return map;
    }

    // ---- pipelines ------------------------------------------------------------------------------------------------

    /// <summary>
    /// parse          = Paste Table, Interpret: TableTextParser.Parse on the text exactly as captured.
    /// review-cleanup = parse, then on the Review screen: Normalize Text (whole table) and Dehyphenate on every row
    ///                  (the user would only pick rows that need it; applying it to all is the most generous case).
    /// paste-cleanup  = on the Paste screen first: Dehyphenate (whole text, across line breaks), Normalize Text, then Interpret.
    /// </summary>
    public static PipelineResult Evaluate(string pipeline, string raw, Expected exp)
    {
        TableImportDraft draft;
        switch (pipeline)
        {
            case "parse":
                draft = TableTextParser.Parse(raw);
                break;
            case "review-cleanup":
                draft = TableTextParser.Parse(raw);
                draft.TableName = TextCleanup.NormalizeGeneralText(draft.TableName);
                foreach (var set in draft.ResultSets)
                {
                    set.Name = TextCleanup.NormalizeGeneralText(set.Name);
                    foreach (var e in set.Entries)
                    {
                        e.RangeText = TextCleanup.NormalizeRangeText(e.RangeText);
                        e.Text = TextCleanup.NormalizeGeneralText(e.Text);
                        if (TextCleanup.TryDehyphenate(e.Text, out var d)) e.Text = d;
                    }
                }
                break;
            default:
                var t = TextCleanup.TryDehyphenate(raw, out var dh) ? dh : raw;
                draft = TableTextParser.Parse(TextCleanup.NormalizePastedText(t));
                break;
        }

        var saveErrors = new List<string>();
        try
        {
            draft.TryBuildTable(1, out _, out var errs);
            saveErrors = errs;
        }
        catch (Exception ex) { saveErrors.Add("exception: " + ex.Message); }

        var outcome = new ParsedOutcome(draft.TableName, draft.DiceText,
            draft.ResultSets.Select(s => (s.Name, s.Entries.Select(e => (e.RangeText, e.Text)).ToList())).ToList(), draft.Issues, saveErrors);
        return Compare(pipeline, outcome, exp, Snapshot(draft));
    }

    // ---- comparison ------------------------------------------------------------------------------------------------

    static PipelineResult Compare(string pipeline, ParsedOutcome o, Expected exp, string snapshot)
    {
        var mm = new List<Mismatch>();
        var structureOk = true;
        var textOk = true;

        if (o.TableName != exp.TableName)
        {
            var tags = TextTags(exp.TableName, o.TableName);
            mm.Add(new("table-name", "heading", exp.TableName, o.TableName, tags));
            if (tags.Contains("content")) structureOk = false; else textOk = false;
        }
        if (!string.Equals(o.Dice, exp.Dice, StringComparison.OrdinalIgnoreCase)) { mm.Add(new("dice", "heading", exp.Dice, o.Dice, "")); structureOk = false; }
        if (o.Sets.Count != exp.Sets.Count)
        {
            mm.Add(new("set-count", "table", exp.Sets.Count.ToString(), o.Sets.Count.ToString(), ""));
            structureOk = false;
        }

        var expectedRows = exp.Sets.Sum(s => s.Entries.Count);
        var actualRows = o.Sets.Sum(s => s.Entries.Count);
        var matched = 0;
        for (var s = 0; s < exp.Sets.Count; s++)
        {
            var es = exp.Sets[s];
            if (s >= o.Sets.Count) { mm.Add(new("missing-set", $"set{s}", es.Name, "", "")); continue; }
            var (name, entries) = o.Sets[s];
            if (name != es.Name) { mm.Add(new("set-name", $"set{s}", es.Name, name, TextTags(es.Name, name))); structureOk = false; }

            // Rows are matched by range (normalized the way the Review screen's Normalize does), so one missing row does
            // not turn every later row into a mismatch.
            TableForge.Domain.DiceExpression? dice = TableForge.Domain.DiceExpression.TryParse(exp.Dice, out var d) ? d : null;
            string Key(string range) => RangeText.TryParse(range, dice, out var pr, out _) ? $"{pr.Min}..{pr.Max}" : "text:" + TextCleanup.NormalizeRangeText(range);
            var byRange = entries.Select((e, i) => (Key: Key(e.Range), e.Range, e.Text, Index: i)).ToList();
            var used = new HashSet<int>();
            var lastIndex = -1;
            foreach (var (ee, idx) in es.Entries.Select((x, i) => (x, i)))
            {
                var key = Key(ee.Range);
                var hit = byRange.FirstOrDefault(a => a.Key == key && !used.Contains(a.Index));
                if (hit.Key is null)
                {
                    mm.Add(new("missing-row", $"set{s} row {ee.Range}", $"{ee.Range} | {ee.Text}", "", ""));
                    structureOk = false;
                    continue;
                }
                used.Add(hit.Index);
                if (hit.Index < lastIndex) { mm.Add(new("row-order", $"set{s} row {ee.Range}", $"position {idx}", $"position {hit.Index}", "")); structureOk = false; }
                lastIndex = hit.Index;
                if (hit.Range != ee.Range)
                    mm.Add(new("range-spelling", $"set{s} row {ee.Range}", ee.Range, hit.Range, "same range, different characters"));
                if (hit.Text == ee.Text) { matched++; continue; }
                var tags = TextTags(ee.Text, hit.Text);
                mm.Add(new("row-text", $"set{s} row {ee.Range}", ee.Text, hit.Text, tags));
                if (tags.Contains("content")) structureOk = false; // merged rows, lost text, foreign text: not a cosmetic difference
                textOk = false;
            }
            foreach (var extra in byRange.Where(a => !used.Contains(a.Index)))
            {
                mm.Add(new("extra-row", $"set{s} row {extra.Range}", "", $"{extra.Range} | {extra.Text}", ""));
                structureOk = false;
            }
        }
        for (var s = exp.Sets.Count; s < o.Sets.Count; s++)
            mm.Add(new("extra-set", $"set{s}", "", $"{o.Sets[s].Name} ({o.Sets[s].Entries.Count} rows)", ""));

        var errors = o.Issues.Count(i => i.Severity == ParseIssueSeverity.Error);
        var warnings = o.Issues.Count(i => i.Severity == ParseIssueSeverity.Warning);
        var infos = o.Issues.Count(i => i.Severity == ParseIssueSeverity.Info);
        var flagged = errors + warnings > 0;
        var exact = mm.Count(m => m.Kind != "range-spelling") == 0;
        string verdict = exact ? "PASS"
            : structureOk ? "TEXT-DIFF"
            : flagged ? "FAIL-flagged" : "FAIL-silent";
        return new PipelineResult(pipeline, verdict, structureOk, textOk && structureOk, expectedRows, actualRows, matched, flagged,
            errors, warnings, infos, o.SaveErrors.Count == 0, mm, snapshot);
    }

    /// <summary>What kind of difference separates two strings, most specific first; "content" means real text was lost/added.</summary>
    static string TextTags(string expected, string actual)
    {
        var tags = new List<string>();
        if (actual.Any(c => c is >= 'ﬀ' and <= 'ﬆ')) tags.Add("ligature-codepoint");
        if (actual.Contains('�')) tags.Add("replacement-char");
        if (actual.Any(c => c is >= '' and <= '')) tags.Add("private-use-char");
        if (actual.Contains('­')) tags.Add("soft-hyphen");
        if (actual.Contains(' ')) tags.Add("nbsp");
        if (Regex.IsMatch(actual, @"\p{L}- \p{Ll}")) tags.Add("line-break-hyphen");
        if (actual.Any(c => c < ' ' && c != '\n')) tags.Add("control-char");

        string NoSpace(string s) => Regex.Replace(s, @"\s+", "");
        string Fold(string s) => s.Normalize(NormalizationForm.FormKC).Replace("­", "").Replace("-", "").Replace("‐", "");
        if (actual != expected)
        {
            if (NoSpace(actual) == NoSpace(expected)) tags.Add(actual.Length < expected.Length ? "missing-spaces" : "spacing");
            else if (NoSpace(Fold(actual)) == NoSpace(Fold(expected)))
            {
                if (actual.Normalize(NormalizationForm.FormKC) != actual) tags.Add("unicode-compat");
                if (NoSpace(actual.Replace("-", "")) == NoSpace(expected.Replace("-", "")) && actual.Count(c => c == '-') != expected.Count(c => c == '-'))
                    tags.Add(actual.Count(c => c == '-') > expected.Count(c => c == '-') ? "extra-hyphen" : "lost-hyphen");
                if (tags.Count == 0) tags.Add("characters");
            }
            else tags.Add("content");
        }
        return string.Join(",", tags.Distinct());
    }

    // ---- raw capture facts --------------------------------------------------------------------------------------------

    public static JsonObject Inventory(string raw)
    {
        var lines = Regex.Split(raw, @"\r\n|\r|\n");
        var obj = new JsonObject
        {
            ["chars"] = raw.Length,
            ["lines"] = lines.Length,
            ["blankLines"] = lines.Count(l => l.Trim().Length == 0),
            ["lineEnding"] = raw.Contains("\r\n") ? (Regex.IsMatch(raw, "(?<!\r)\n") ? "mixed" : "CRLF") : raw.Contains('\n') ? "LF" : raw.Contains('\r') ? "CR" : "none",
            ["trailingNewline"] = raw.EndsWith('\n') || raw.EndsWith('\r'),
            ["tabs"] = raw.Count(c => c == '\t'),
            ["linesWithTab"] = lines.Count(l => l.Contains('\t')),
            ["linesWithMultiSpace"] = lines.Count(l => l.Trim().Contains("  ")),
            ["trailingSpaceLines"] = lines.Count(l => l.Length > 0 && l.EndsWith(' ')),
            ["replacementChars"] = raw.Count(c => c == '�'),
            ["controlChars"] = raw.Count(c => c < ' ' && c != '\n' && c != '\r' && c != '\t'),
        };
        var na = new JsonObject();
        foreach (var g in raw.Where(c => c > '~').GroupBy(c => c).OrderBy(g => g.Key)) na[$"U+{(int)g.Key:X4}"] = g.Count();
        obj["nonAscii"] = na;
        return obj;
    }

    public static string Visible(string s)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\t') sb.Append("\\t");
            else if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') { sb.Append("⏎(CRLF)"); i++; }
            else if (c == '\n') sb.Append("⏎(LF)");
            else if (c == '\r') sb.Append("⏎(CR)");
            else if (c > '~' || c < ' ') sb.Append($"[U+{(int)c:X4}{Name(c)}]");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static string Name(char c) => c switch
    {
        ' ' => " NBSP", '­' => " SOFT HYPHEN", '�' => " REPLACEMENT",
        _ => char.IsLetter(c) || char.IsPunctuation(c) || char.IsSymbol(c) ? $" {c}" : "",
    };

    public static string Snapshot(TableImportDraft d)
    {
        var sb = new StringBuilder();
        sb.Append($"TABLE {d.TableName} | {d.DiceText}\n");
        foreach (var set in d.ResultSets)
        {
            sb.Append($"SET {(set.Name.Length == 0 ? "(unnamed)" : set.Name)}\n");
            foreach (var e in set.Entries) sb.Append($"  {e.RangeText} | {e.Text}\n");
        }
        foreach (var i in d.Issues)
            sb.Append($"ISSUE {i.Severity} {i.Code}{(i.SourceLine is { } l ? $" line{l}" : "")}: {i.Message}\n");
        return sb.ToString();
    }
}
