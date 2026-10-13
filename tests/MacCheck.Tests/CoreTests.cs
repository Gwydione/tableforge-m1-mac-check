using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using E3.Core;
using MacCheck.App;
using MacCheck.Core;
using TableForge.Domain;
using Xunit;

namespace MacCheck.Tests;

/// <summary>Model, privacy and packaging tests — no UI. They say nothing about how anything behaves on macOS.</summary>
public class CoreTests
{
    private static string Base(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    // ---- provenance ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_copied_TableForge_file_still_matches_its_v1_0_0_git_blob()
    {
        var rows = File.ReadAllLines(Base("PROVENANCE.md")).Where(l => l.StartsWith("| src/MacCheck.Core/TableForge/")).ToList();
        Assert.Equal(26, rows.Count);
        foreach (var row in rows)
        {
            var cells = row.Split('|', StringSplitOptions.TrimEntries);
            var rel = cells[1]["src/MacCheck.Core/TableForge/".Length..];
            var expected = cells[3]["blob ".Length..];
            var bytes = File.ReadAllBytes(Base("tf-copies", rel));
            var blob = Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"), .. bytes]));
            Assert.True(expected == blob, $"{rel}: {blob} != {expected}");
        }
    }

    // ---- E2 -------------------------------------------------------------------------------------------------------------

    public static IEnumerable<TheoryDataRow<string, string, string>> E2Captures()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Base("e2-fixtures", "E2-results.json")));
        foreach (var row in doc.RootElement.EnumerateArray())
            yield return new(row.GetProperty("env").GetString()!, row.GetProperty("reader").GetString()!, row.GetProperty("case").GetString()!);
    }

    [Theory]
    [MemberData(nameof(E2Captures))]
    public void The_ported_evaluator_gives_exactly_E2s_verdicts_for_every_E2_capture(string env, string reader, string caseId)
    {
        var expected = PdfCaseEvaluator.LoadExpected(Platform.CorpusFolder)[caseId];
        var text = File.ReadAllText(Base("e2-fixtures", env, reader, caseId + ".txt"), new UTF8Encoding(false));
        using var doc = JsonDocument.Parse(File.ReadAllText(Base("e2-fixtures", "E2-results.json")));
        var oracle = doc.RootElement.EnumerateArray().Single(r => r.GetProperty("env").GetString() == env && r.GetProperty("reader").GetString() == reader && r.GetProperty("case").GetString() == caseId);
        foreach (var result in PdfCaseEvaluator.EvaluateAll(text, expected))
        {
            Assert.Equal(oracle.GetProperty(result.Pipeline).GetProperty("verdict").GetString(), result.Verdict);
            Assert.Equal(oracle.GetProperty(result.Pipeline).GetProperty("actualRows").GetInt32(), result.ActualRows);
        }
    }

    [Fact]
    public void The_embedded_E2_baselines_equal_E2s_recorded_verdicts()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Base("e2-fixtures", "E2-results.json")));
        var names = new Dictionary<(string, string), string> { [("windows", "chrome")] = "Windows Chrome", [("windows", "edge")] = "Windows Edge", [("linux", "chromium")] = "Linux Chromium", [("linux", "firefox")] = "Linux Firefox", [("linux", "evince")] = "Linux Evince" };
        var n = 0;
        foreach (var row in doc.RootElement.EnumerateArray())
            if (names.TryGetValue((row.GetProperty("env").GetString()!, row.GetProperty("reader").GetString()!), out var label))
            {
                Assert.Equal(row.GetProperty("parse").GetProperty("verdict").GetString(), E2Baselines.Verdicts[row.GetProperty("case").GetString()!][label]);
                n++;
            }
        Assert.Equal(40, n);
    }

    [Fact]
    public void Three_mandatory_PDFs_d20_d100_and_d66_and_five_optional_ones_all_ship_with_expectations()
    {
        var cases = PdfCaseCatalog.Load(Platform.CorpusFolder);
        Assert.Equal(["c01-d20-simple", "c02-d100-four-columns", "c05-d66-two-columns"], cases.Where(c => c.Mandatory).Select(c => c.Id));
        Assert.Equal(5, cases.Count(c => !c.Mandatory));
        Assert.All(cases, c => Assert.True(File.Exists(c.PdfFile), c.PdfFile));
        Assert.Equal(["d20", "d100", "d66"], cases.Where(c => c.Mandatory).Select(c => c.Expected.Dice));
    }

    [Fact]
    public void Only_text_that_is_the_test_table_is_accepted_from_a_paste()
    {
        var cases = PdfCaseCatalog.Load(Platform.CorpusFolder);
        foreach (var c in cases)
        {
            var own = File.ReadAllText(Base("e2-fixtures", "windows", "chrome", c.Id + ".txt"));
            Assert.True(ClipboardGuard.IsTestContent(c, own), c.Id);
            foreach (var other in cases.Where(o => o.Id != c.Id))
                Assert.False(ClipboardGuard.IsTestContent(other, own), $"{c.Id} accepted as {other.Id}");
        }
        Assert.False(ClipboardGuard.IsTestContent(cases[0], "Dear bank, my password is hunter2"));
        Assert.False(ClipboardGuard.IsTestContent(cases[0], ""));
        Assert.False(ClipboardGuard.IsTestContent(cases[0], "Roadside Encounters" + new string('x', ClipboardGuard.MaxLength)));
        // Curly vs straight apostrophe in the title (c06 "Alchemist’s Shelf") and different spacing still match.
        var c06 = cases.Single(c => c.Id == "c06-special-characters");
        Assert.True(ClipboardGuard.IsTestContent(c06, "D8  ALCHEMIST'S\nSHELF\n1 ..."));
    }

    [Fact]
    public void Raw_verdict_decides_the_outcome_and_cleanups_are_reported_separately()
    {
        Assert.Equal(Outcome.Pass, PdfCaseResult.FromVerdict("PASS"));
        Assert.Equal(Outcome.Inconclusive, PdfCaseResult.FromVerdict("TEXT-DIFF"));
        Assert.Equal(Outcome.Fail, PdfCaseResult.FromVerdict("FAIL-flagged"));
        Assert.Equal(Outcome.Fail, PdfCaseResult.FromVerdict("FAIL-silent"));
        var c = PdfCaseCatalog.Load(Platform.CorpusFolder).Single(x => x.Id == "c04-hyphenation");
        var r = PdfCaseResult.Evaluate(c, File.ReadAllText(Base("e2-fixtures", "linux", "firefox", "c04-hyphenation.txt")), "test", 1);
        Assert.Equal("TEXT-DIFF", r.RawVerdict);
        Assert.Equal(["review-cleanup", "paste-cleanup"], r.CleanupVerdicts.Keys);
        Assert.Equal(Outcome.Inconclusive, r.Outcome); // a better cleanup verdict never upgrades the raw outcome
    }

    // ---- outcomes -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Outcomes_combine_automatic_results_with_tester_answers_and_sections_take_the_worst()
    {
        Assert.Equal(Outcome.Pass, Outcomes.Combine(true, Answer.Yes));
        Assert.Equal(Outcome.Pass, Outcomes.Combine(true, Answer.NotAsked));
        Assert.Equal(Outcome.Fail, Outcomes.Combine(true, Answer.No));
        Assert.Equal(Outcome.Inconclusive, Outcomes.Combine(true, Answer.NotSure));
        Assert.Equal(Outcome.Fail, Outcomes.Combine(false, Answer.Yes));
        Assert.Equal(Outcome.Fail, Outcomes.Worst([Outcome.Pass, Outcome.Fail, Outcome.Inconclusive]));
        Assert.Equal(Outcome.Inconclusive, Outcomes.Worst([Outcome.Pass, Outcome.UnableToTest]));
        Assert.Equal(Outcome.UnableToTest, Outcomes.Worst([Outcome.UnableToTest]));
        Assert.Equal(Outcome.UnableToTest, Outcomes.Worst([]));
    }

    // ---- E3 step checks (model level) ------------------------------------------------------------------------------------

    [Fact]
    public void Each_E3_step_accepts_the_intended_result_and_rejects_a_wrong_one()
    {
        var steps = E3Steps.All.ToDictionary(s => s.Id);
        Assert.Equal(6, steps.Count);
        E3Observation O(string text, TextStyles styles, params string[] messages) => new(text, styles, messages, 0);
        const string s = "The goblin quickly flees";
        Assert.True(steps["E3-1-typing"].Check(O("The goblin flees", TextStyles.Empty)).Pass);
        Assert.False(steps["E3-1-typing"].Check(O("The gobln flees", TextStyles.Empty)).Pass);
        Assert.True(steps["E3-2-cursor"].Check(O(s, TextStyles.Empty)).Pass);
        Assert.False(steps["E3-2-cursor"].Check(O("The goblin fleesquickly ", TextStyles.Empty)).Pass);
        var bold = TextStyles.FromRuns([new StyleRun(4, 6, TextStyle.Bold)], s.Length);
        Assert.True(steps["E3-3-bold"].Check(O(s, bold)).Pass);
        Assert.False(steps["E3-3-bold"].Check(O(s, TextStyles.FromRuns([new StyleRun(4, 5, TextStyle.Bold)], s.Length))).Pass);
        Assert.True(steps["E3-4-italic"].Check(O(s, E3Steps.Formatted)).Pass);
        var redone = TextStyles.FromRuns([new StyleRun(4, 6, TextStyle.Bold), new StyleRun(11, 7, TextStyle.Italic), new StyleRun(19, 5, TextStyle.Bold)], s.Length);
        Assert.True(steps["E3-5-undo"].Check(O(s, redone, "Made the selection bold.", "Undo done.", "Redo done.")).Pass);
        Assert.False(steps["E3-5-undo"].Check(O(s, redone, "Made the selection bold.")).Pass); // undo/redo never used
        Assert.True(steps["E3-6-accent"].Check(O(s + " café", TextStyles.FromRuns(E3Steps.Formatted.Runs, s.Length + 5))).Pass);
        Assert.True(steps["E3-6-accent"].Check(O(s + " café", TextStyles.FromRuns(E3Steps.Formatted.Runs, s.Length + 6))).Pass);
        Assert.False(steps["E3-6-accent"].Check(O(s + " cafe", TextStyles.FromRuns(E3Steps.Formatted.Runs, s.Length + 5))).Pass);
    }

    // ---- privacy --------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_redactor_removes_home_user_computer_paths_and_secrets()
    {
        var r = new Redactor();
        r.AddSecret("tok_SECRET_123456");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirty = $"home {home}\\x user {Environment.UserName} pc {Environment.MachineName} mac /Users/alice/Desktop/file linux /home/bob/x token tok_SECRET_123456";
        var clean = r.Clean(dirty);
        Assert.DoesNotContain(home, clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", clean);
        Assert.DoesNotContain("bob", clean);
        Assert.DoesNotContain("tok_SECRET", clean);
    }

    [Fact]
    public void The_dddice_room_code_never_reaches_the_log()
    {
        var line = E4Runner.SanitizeDiceLog("session: dddice session (room FAKE0RM, theme dddice-bees, guest)");
        Assert.DoesNotContain("FAKE0RM", line);
        Assert.Contains("room <redacted>", line);
    }

    [Fact]
    public void System_facts_never_include_names_or_paths()
    {
        var facts = SystemFacts.Read(includeModel: true);
        var all = string.Join(" ", facts.Keys.Concat(facts.Values));
        Assert.DoesNotContain(Environment.UserName, all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), all, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("osVersion", facts.Keys);
        Assert.Contains("processArchitecture", facts.Keys);
    }

    [Fact]
    public void The_results_package_holds_only_the_documented_files_and_no_identifiers()
    {
        var redactor = new Redactor();
        var doc = new ResultsDocument { Build = BuildIdentity.Current() };
        doc.E2.Add(new PdfCaseResult { Case = "c01-d20-simple", Mandatory = true, Outcome = Outcome.Pass, RawVerdict = "PASS", CaptureFile = "e2/c01-d20-simple.txt" });
        doc.E3.Checks.Add(new CheckItem { Id = "E3-1", Title = "t", Outcome = Outcome.Pass, Detail = $"path {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}" });
        var ms = new MemoryStream();
        ResultsPackage.Write(ms, doc, new Dictionary<string, string> { ["e2/c01-d20-simple.txt"] = "D20 ROADSIDE ENCOUNTERS\n1 A pedlar" },
            new Dictionary<string, byte[]> { ["e4/dice-settled.png"] = [1, 2, 3] }, [$"user {Environment.UserName}"], redactor);
        ms.Position = 0;
        using var zip = new ZipArchive(ms);
        Assert.Equal(["README.txt", "e2/c01-d20-simple.txt", "e4/dice-settled.png", "log.txt", "results.json", "results.md"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        foreach (var e in zip.Entries.Where(e => !e.FullName.EndsWith(".png")))
        {
            var text = new StreamReader(e.Open()).ReadToEnd();
            Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
        }
        using var json = JsonDocument.Parse(new StreamReader(zip.GetEntry("results.json")!.Open()).ReadToEnd());
        Assert.Equal("TableForgeMacCheck", json.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("formatVersion").GetInt32());
    }

    // ---- E1 server -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_Safari_page_server_serves_one_page_and_accepts_only_known_events()
    {
        using var server = new E1SafariServer();
        using var http = new HttpClient();
        var page = await http.GetStringAsync(server.PageUri);
        Assert.Contains("window.__e1post(m)", page);
        Assert.DoesNotContain("chrome.webview.postMessage(m)", page); // (a comment in the 1.0.0 page still names it)
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(new Uri(server.PageUri, "../other.html"))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{server.Port}/e1.html")).StatusCode);
        var got = new List<string>();
        server.EventReceived += e => got.Add(e.GetProperty("kind").GetString()!);
        Assert.True((await http.PostAsync(new Uri(server.PageUri, "event"), new StringContent("""{"kind":"e1:done"}"""))).IsSuccessStatusCode);
        Assert.False((await http.PostAsync(new Uri(server.PageUri, "event"), new StringContent("""{"kind":"read-file"}"""))).IsSuccessStatusCode);
        Assert.False((await http.PostAsync(new Uri(server.PageUri, "event"), new StringContent("not json"))).IsSuccessStatusCode);
        // Oversized: refused (413), or the connection is dropped before the body is read — either way never accepted.
        try { Assert.False((await http.PostAsync(new Uri(server.PageUri, "event"), new StringContent("{\"kind\":\"e1:done\",\"pad\":\"" + new string('x', 20000) + "\"}"))).IsSuccessStatusCode); }
        catch (HttpRequestException) { }
        Assert.Equal(["e1:done"], got);
    }

    [Fact]
    public void No_source_file_contains_a_URL_other_than_dddice_and_loopback()
    {
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var urls = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".html")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//")))   // URLs in code and pages, not in comments
            .SelectMany(l => Regex.Matches(l, @"https?://[A-Za-z0-9.\-]+").Select(m => m.Value))
            .Distinct().Order().ToList();
        var allowed = new[] { "https://dddice.com", "https://cdn.dddice.com", "http://127.0.0.1", "https://tableforge-app.local", "https://navigation-test.invalid", "http://absolute" };
        Assert.All(urls, u => Assert.Contains(u, allowed));
    }
}
