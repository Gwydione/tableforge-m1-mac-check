namespace MacCheck.Core;

/// <summary>
/// What TableForge 1.0.0's parser made of the same PDFs copied from other readers in E2 (raw parse verdicts, generated
/// from E2's results.json). A Mac result is read against these: a FAIL that every browser engine also gets (c02) is a
/// parser limitation, not a Mac problem.
/// </summary>
public static class E2Baselines
{
    public static readonly Dictionary<string, Dictionary<string, string>> Verdicts = new()
    {
        ["c01-d20-simple"] = new() { ["Windows Chrome"] = "PASS", ["Windows Edge"] = "PASS", ["Linux Chromium"] = "PASS", ["Linux Firefox"] = "PASS", ["Linux Evince"] = "FAIL-flagged" },
        ["c02-d100-four-columns"] = new() { ["Windows Chrome"] = "FAIL-flagged", ["Windows Edge"] = "PASS", ["Linux Chromium"] = "FAIL-flagged", ["Linux Firefox"] = "FAIL-flagged", ["Linux Evince"] = "FAIL-flagged" },
        ["c03-wrapped-text"] = new() { ["Windows Chrome"] = "PASS", ["Windows Edge"] = "PASS", ["Linux Chromium"] = "PASS", ["Linux Firefox"] = "PASS", ["Linux Evince"] = "FAIL-flagged" },
        ["c04-hyphenation"] = new() { ["Windows Chrome"] = "TEXT-DIFF", ["Windows Edge"] = "TEXT-DIFF", ["Linux Chromium"] = "TEXT-DIFF", ["Linux Firefox"] = "TEXT-DIFF", ["Linux Evince"] = "FAIL-flagged" },
        ["c05-d66-two-columns"] = new() { ["Windows Chrome"] = "PASS", ["Windows Edge"] = "PASS", ["Linux Chromium"] = "PASS", ["Linux Firefox"] = "PASS", ["Linux Evince"] = "FAIL-flagged" },
        ["c06-special-characters"] = new() { ["Windows Chrome"] = "PASS", ["Windows Edge"] = "PASS", ["Linux Chromium"] = "PASS", ["Linux Firefox"] = "PASS", ["Linux Evince"] = "FAIL-flagged" },
        ["c07-open-ended-ranges"] = new() { ["Windows Chrome"] = "PASS", ["Windows Edge"] = "FAIL-flagged", ["Linux Chromium"] = "PASS", ["Linux Firefox"] = "PASS", ["Linux Evince"] = "FAIL-flagged" },
        ["c08-heading-two-result-columns"] = new() { ["Windows Chrome"] = "PASS", ["Windows Edge"] = "FAIL-flagged", ["Linux Chromium"] = "PASS", ["Linux Firefox"] = "PASS", ["Linux Evince"] = "FAIL-flagged" },
    };

    /// <summary>Readers whose E2 verdict equals <paramref name="verdict"/> for this case.</summary>
    public static List<string> SameAs(string caseId, string verdict) =>
        Verdicts.TryGetValue(caseId, out var byReader) ? byReader.Where(kv => kv.Value == verdict).Select(kv => kv.Key).ToList() : [];
}
