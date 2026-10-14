using TableForge.Import;

namespace MacCheck.Core;

/// <summary>
/// Row coverage and order of a raw parse, reported separately from the E2 verdict (which it never changes). Rows are
/// matched to the expected table by range, per result set, exactly as the E2 evaluator matches them. "Out of order" counts
/// the rows not on the longest run that is in the expected order, so an interleaved two-column table counts every row
/// that is out of place, not just the one place where the order goes backwards.
/// </summary>
public sealed record RowReport(int Expected, int Found, int Missing, int Extra, int OutOfOrder, int TextDifferences, bool Warned)
{
    public string Coverage => $"{Found}/{Expected}";

    public string Order => Found == 0 ? "not checked" : OutOfOrder == 0 ? "in order" : $"{OutOfOrder} out of order";

    /// <summary>Row problems the parser gave no warning about (silent reordering, loss or extra rows), or null.</summary>
    public string? Silent
    {
        get
        {
            if (Warned) return null;
            var parts = new List<string>();
            if (OutOfOrder > 0) parts.Add($"{OutOfOrder} rows reordered");
            if (Missing > 0) parts.Add($"{Missing} rows lost");
            if (Extra > 0) parts.Add($"{Extra} extra rows");
            return parts.Count == 0 ? null : string.Join(", ", parts) + " — with no parser warning";
        }
    }

    /// <summary>
    /// Computes the report for the raw text exactly as pasted (the "parse" pipeline's input; parsed again because the
    /// evaluator's result does not carry row positions). <paramref name="warned"/>: the parser raised a warning or error.
    /// </summary>
    public static RowReport ForRaw(string text, Expected exp, bool warned)
    {
        var draft = TableTextParser.Parse(text);
        TableForge.Domain.DiceExpression? dice = TableForge.Domain.DiceExpression.TryParse(exp.Dice, out var d) ? d : null;
        string Key(string range) => RangeText.TryParse(range, dice, out var pr, out _) ? $"{pr.Min}..{pr.Max}" : "text:" + TextCleanup.NormalizeRangeText(range);

        int expected = 0, found = 0, outOfOrder = 0, textDiffs = 0, actual = draft.ResultSets.Sum(s => s.Entries.Count);
        for (var s = 0; s < exp.Sets.Count; s++)
        {
            var es = exp.Sets[s];
            expected += es.Entries.Count;
            if (s >= draft.ResultSets.Count) continue;
            var parsed = draft.ResultSets[s].Entries.Select((e, i) => (Key: Key(e.RangeText), e.Text, Index: i)).ToList();
            var used = new HashSet<int>();
            var positions = new List<int>();
            foreach (var ee in es.Entries)
            {
                var key = Key(ee.Range);
                var hit = parsed.FirstOrDefault(p => p.Key == key && !used.Contains(p.Index));
                if (hit.Key is null) continue;
                used.Add(hit.Index);
                positions.Add(hit.Index);
                if (hit.Text != ee.Text) textDiffs++;
            }
            found += positions.Count;
            outOfOrder += positions.Count - LongestIncreasingRun(positions);
        }
        return new RowReport(expected, found, expected - found, actual - found, outOfOrder, textDiffs, warned);
    }

    /// <summary>Length of the longest strictly increasing subsequence (patience sorting, O(n log n)).</summary>
    public static int LongestIncreasingRun(IReadOnlyList<int> values)
    {
        var tails = new List<int>();
        foreach (var v in values)
        {
            var i = tails.BinarySearch(v);
            if (i < 0) i = ~i;
            if (i == tails.Count) tails.Add(v); else tails[i] = v;
        }
        return tails.Count;
    }
}
