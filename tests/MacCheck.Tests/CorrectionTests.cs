using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using E4.Dddice;
using MacCheck.App;
using MacCheck.Core;
using Xunit;

namespace MacCheck.Tests;

/// <summary>
/// Regression tests for the corrections after the first macOS runner trial (run 38477574883):
/// honest outcomes (nothing unexecuted or unobserved is PASS), the macOS bridge correction, and row-order evidence.
/// </summary>
public class CorrectionTests
{
    private static string Base(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    private static ResultsDocument Document() => new() { Build = new BuildIdentity("test", "0", "test", "test", "test") };

    // ---- outcomes ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, Answer.Yes, Outcome.UnableToTest)]
    [InlineData(false, Answer.No, Outcome.UnableToTest)]
    [InlineData(false, Answer.NotAsked, Outcome.UnableToTest)]
    [InlineData(true, Answer.NotAsked, Outcome.UnableToTest)]
    [InlineData(true, Answer.Yes, Outcome.Pass)]
    [InlineData(true, Answer.No, Outcome.Fail)]
    [InlineData(true, Answer.NotSure, Outcome.Inconclusive)]
    public void A_check_that_needs_a_person_never_passes_without_one(bool shown, Answer answer, Outcome expected) =>
        Assert.Equal(expected, Outcomes.Observation(shown, answer));

    [Theory]
    [InlineData(0, 0, Outcome.Inconclusive)] // nothing to observe (macOS): no evidence either way
    [InlineData(3, 0, Outcome.Pass)]
    [InlineData(3, 1, Outcome.Fail)]
    [InlineData(0, 1, Outcome.Fail)]
    public void Shutdown_with_nothing_to_observe_is_not_a_pass(int before, int remaining, Outcome expected) =>
        Assert.Equal(expected, Outcomes.Shutdown(before, remaining));

    [Fact]
    public void A_failed_dice_start_makes_the_section_and_the_session_fail()
    {
        var d = Document();
        d.E4.Checks.Add(new CheckItem { Id = "E4-ready", Title = "ready", Outcome = Outcome.Fail });
        foreach (var id in new[] { "E4-bridge", "E4-animation", "E4-rolls", "E4-seen-animation", "E4-seen-faces" })
            d.E4.Checks.Add(new CheckItem { Id = id, Title = id, Outcome = Outcome.UnableToTest });
        d.E4.Checks.Add(new CheckItem { Id = "E4-shutdown", Title = "shutdown", Outcome = Outcomes.Shutdown(0, 0) });
        d.E3.Checks.Add(new CheckItem { Id = "E3-1", Title = "e3", Outcome = Outcome.Pass });
        d.Finish();
        Assert.Equal(Outcome.Fail, d.E4.Outcome);
        Assert.Equal(Outcome.Fail, d.Overall);
    }

    [Fact]
    public void Automatic_passes_with_unobserved_items_are_inconclusive_not_pass()
    {
        var d = Document();
        d.E4.Checks.Add(new CheckItem { Id = "E4-ready", Title = "ready", Outcome = Outcome.Pass });
        d.E4.Checks.Add(new CheckItem { Id = "E4-seen-animation", Title = "seen", Outcome = Outcomes.Observation(true, Answer.NotAsked) });
        d.E3.Checks.Add(new CheckItem { Id = "E3-3-bold", Title = "bold", Outcome = Outcomes.Observation(true, Answer.NotAsked) });
        d.E2.Add(new PdfCaseResult { Case = "c01-d20-simple", Mandatory = true, Outcome = Outcome.Pass });
        d.Finish();
        Assert.Equal(Outcome.Inconclusive, d.E4.Outcome);
        Assert.Equal(Outcome.UnableToTest, d.E3.Outcome);
        Assert.NotEqual(Outcome.Pass, d.Overall);
    }

    [AvaloniaFact]
    public void Dice_observations_cannot_pass_when_no_dice_were_shown()
    {
        CheckSession.DeleteJournal();
        var redactor = new Redactor();
        var session = new CheckSession(redactor, new SessionLog(redactor));
        using var e4 = new E4Runner(session, new Border());
        e4.RecordAnswers(Answer.Yes, Answer.Yes); // even a "Yes" cannot pass: the dice section never showed a roll
        var seen = session.Document.E4.Checks.Where(c => c.Id.StartsWith("E4-seen-")).ToList();
        Assert.Equal(2, seen.Count);
        Assert.All(seen, c => Assert.Equal(Outcome.UnableToTest, c.Outcome));
        Assert.All(seen, c => Assert.Contains("no dice were shown", c.Detail));
        e4.RecordAnswers(Answer.NotAsked, Answer.NotAsked);
        Assert.All(session.Document.E4.Checks.Where(c => c.Id.StartsWith("E4-seen-")), c => Assert.Equal(Outcome.UnableToTest, c.Outcome));
        CheckSession.DeleteJournal();
    }

    // ---- macOS bridge correction ------------------------------------------------------------------------------------

    [Fact]
    public void The_served_page_is_1_0_0_with_its_bridge_swapped_and_one_diagnostic_prelude()
    {
        var original = HostPage.Original();
        var page = HostPage.ForAvalonia();
        Assert.Equal(1, Count(page, PageDiagnostics.Prelude));
        Assert.StartsWith("<!doctype html>\n<html>\n<head>" + PageDiagnostics.Prelude, page.ReplaceLineEndings("\n"));
        Assert.DoesNotContain(HostPage.OriginalBridge, page);
        Assert.Equal(1, Count(page, HostPage.AvaloniaBridge));
        Assert.DoesNotContain("__tfDiag", original);
        // Remove the M1 additions and the page is the 1.0.0 page again, byte for byte.
        Assert.Equal(original, page.Replace(PageDiagnostics.Prelude, "").Replace(HostPage.AvaloniaBridge, HostPage.OriginalBridge));
        Assert.DoesNotContain(PageDiagnostics.SimulateLateBridge, page);
        // The prelude records facts only: it never posts a message or calls the bridge.
        Assert.DoesNotContain("invokeCSharpAction(", PageDiagnostics.Prelude);
        Assert.DoesNotContain("postMessage", PageDiagnostics.Prelude);
    }

    [Fact]
    public void The_development_simulation_hides_the_bridge_before_the_page_records_it()
    {
        var page = HostPage.ForAvalonia(simulateLateBridge: true);
        Assert.True(page.IndexOf(PageDiagnostics.SimulateLateBridge, StringComparison.Ordinal) < page.IndexOf(PageDiagnostics.Prelude, StringComparison.Ordinal));
        Assert.Contains("addEventListener('load'", PageDiagnostics.SimulateLateBridge);
    }

    private static int Count(string s, string part)
    {
        var n = 0;
        for (var i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void The_bridge_check_replays_pageLoaded_only_when_the_bridge_exists_and_the_message_was_lost()
    {
        const string ready = """{"bridge":"function","bridgeAtStart":"undefined","tf":"object","sdk":"function","sdkLoadFailed":false,"origin":"http://127.0.0.1","readyState":"complete","errors":[],"blocked":[]}""";
        var facts = BridgeCheck.Parse(System.Text.Json.JsonSerializer.Serialize(ready)); // InvokeScript may return it JSON-quoted
        Assert.NotNull(facts);
        Assert.True(facts!.BridgeReady);
        Assert.Equal("undefined", facts.BridgeAtStart);
        Assert.True(BridgeCheck.ShouldReplay(facts, pageLoadedReceived: false));
        Assert.False(BridgeCheck.ShouldReplay(facts, pageLoadedReceived: true)); // WebView2/WebKitGTK: the page's own message arrived
        Assert.False(BridgeCheck.ShouldReplay(BridgeCheck.Parse(ready.Replace("\"bridge\":\"function\"", "\"bridge\":\"undefined\"")), false));
        Assert.False(BridgeCheck.ShouldReplay(BridgeCheck.Parse(ready.Replace("\"tf\":\"object\"", "\"tf\":\"undefined\"")), false));
        Assert.False(BridgeCheck.ShouldReplay(BridgeCheck.Parse("not json"), false));
        Assert.Null(BridgeCheck.Parse(null));
        Assert.Contains("invokeCSharpAction=function (at document start: undefined)", facts.ToString());
    }

    [Fact]
    public void Bridge_facts_are_bounded()
    {
        var many = string.Join(",", Enumerable.Range(0, 9).Select(i => $"\"{new string('x', 400)}{i}\""));
        var facts = BridgeCheck.Parse($$"""{"bridge":"function","tf":"object","errors":[{{many}}],"blocked":[{{many}}],"origin":"{{new string('h', 300)}}"}""")!;
        Assert.Equal(5, facts.Errors.Count);
        Assert.All(facts.Errors, e => Assert.True(e.Length <= 161));
        Assert.True(facts.Origin.Length <= 41);
        Assert.Equal("?", facts.Sdk);
    }

    [Fact]
    public void The_probe_and_replay_scripts_carry_no_token_room_or_page_path()
    {
        foreach (var script in new[] { BridgeCheck.ProbeScript, BridgeCheck.ReplayScript })
        {
            Assert.DoesNotContain("token", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("room", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("location.href", script);
            Assert.DoesNotContain("location.pathname", script);
        }
        Assert.Contains("kind: 'pageLoaded'", BridgeCheck.ReplayScript);
    }

    // ---- PDF evidence: row coverage and order ------------------------------------------------------------------------

    [Theory]
    [InlineData(new int[0], 0)]
    [InlineData(new[] { 0, 1, 2, 3 }, 4)]
    [InlineData(new[] { 3, 2, 1, 0 }, 1)]
    [InlineData(new[] { 0, 2, 4, 1, 3, 5 }, 4)]
    public void Longest_in_order_run(int[] positions, int expected) => Assert.Equal(expected, RowReport.LongestIncreasingRun(positions));

    /// <summary>
    /// Preview → pasteboard captures from the first macOS runner trial (macOS 15.7.9, run 38477574883), kept as fixtures:
    /// the verdicts M1 must keep reporting. c05 on Apple Silicon is the silent reordering — it must stay a visible FAIL.
    /// </summary>
    [Theory]
    [InlineData("arm64", "c01-d20-simple", "PASS", "20/20", 0)]
    [InlineData("arm64", "c02-d100-four-columns", "FAIL-flagged", "5/20", 0)]
    [InlineData("arm64", "c05-d66-two-columns", "FAIL-silent", "36/36", 17)]
    [InlineData("arm64", "c07-open-ended-ranges", "FAIL-flagged", "7/7", 0)]
    [InlineData("arm64", "c08-heading-two-result-columns", "PASS", "12/12", 0)]
    [InlineData("arm64", "c03-wrapped-text", "FAIL-flagged", "10/12", 0)]
    [InlineData("arm64", "c04-hyphenation", "FAIL-flagged", "9/10", 0)]
    [InlineData("arm64", "c06-special-characters", "TEXT-DIFF", "8/8", 0)]
    [InlineData("x64", "c01-d20-simple", "PASS", "20/20", 0)]
    [InlineData("x64", "c02-d100-four-columns", "FAIL-flagged", "5/20", 0)]
    [InlineData("x64", "c05-d66-two-columns", "PASS", "36/36", 0)]
    [InlineData("x64", "c07-open-ended-ranges", "FAIL-flagged", "7/7", 0)]
    [InlineData("x64", "c08-heading-two-result-columns", "PASS", "12/12", 0)]
    [InlineData("x64", "c03-wrapped-text", "FAIL-flagged", "10/12", 0)]
    [InlineData("x64", "c04-hyphenation", "FAIL-flagged", "9/10", 0)]
    [InlineData("x64", "c06-special-characters", "TEXT-DIFF", "8/8", 0)]
    public void Preview_captures_from_the_first_runner_trial_keep_their_verdicts(string arch, string caseId, string verdict, string coverage, int outOfOrder)
    {
        var c = PdfCaseCatalog.Load(Platform.CorpusFolder).Single(x => x.Id == caseId);
        var text = File.ReadAllText(Base("preview-runner-fixtures", "macos-15.7.9-" + arch, caseId + ".txt"), new UTF8Encoding(false));
        Assert.True(ClipboardGuard.IsTestContent(c, text)); // the fixture is the synthetic test table, nothing else
        var r = PdfCaseResult.Evaluate(c, text, "fixture", 0);
        Assert.Equal(verdict, r.RawVerdict);
        Assert.Equal(PdfCaseResult.FromVerdict(verdict), r.Outcome);
        Assert.Equal(coverage, r.Rows!.Coverage);
        Assert.Equal(outOfOrder, r.Rows.OutOfOrder);
    }

    [Fact]
    public void Silent_reordering_is_reported_as_a_fail_with_its_own_evidence()
    {
        var c = PdfCaseCatalog.Load(Platform.CorpusFolder).Single(x => x.Id == "c05-d66-two-columns");
        var r = PdfCaseResult.Evaluate(c, File.ReadAllText(Base("preview-runner-fixtures", "macos-15.7.9-arm64", c.Id + ".txt"), new UTF8Encoding(false)), "fixture", 0);
        Assert.Equal(Outcome.Fail, r.Outcome);
        Assert.Equal("17 out of order", r.Rows!.Order);
        Assert.Equal("17 rows reordered — with no parser warning", r.Rows.Silent);
        var d = Document();
        d.E2.Add(r);
        d.Finish();
        Assert.Equal(Outcome.Fail, d.E2Summary.Outcome);
        Assert.Contains("17 rows reordered — with no parser warning", ResultsPackage.Summary(d));
    }

    [Fact]
    public void Warned_problems_are_not_reported_as_silent()
    {
        var c = PdfCaseCatalog.Load(Platform.CorpusFolder).Single(x => x.Id == "c03-wrapped-text");
        var r = PdfCaseResult.Evaluate(c, File.ReadAllText(Base("preview-runner-fixtures", "macos-15.7.9-arm64", c.Id + ".txt"), new UTF8Encoding(false)), "fixture", 0);
        Assert.Equal("FAIL-flagged", r.RawVerdict);
        Assert.True(r.Rows!.Missing > 0 || r.Rows.Extra > 0);
        Assert.Null(r.Rows.Silent);
    }
}
