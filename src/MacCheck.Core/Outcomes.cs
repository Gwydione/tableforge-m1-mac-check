using System.Text.Json.Serialization;

namespace MacCheck.Core;

/// <summary>
/// The four outcomes of every check, section and the session (M1 plan §6.1).
/// PASS: every automatic check passed and every tester confirmation was "Yes".
/// FAIL: an automatic check failed, or the tester said "No" to a direct observation.
/// UNABLE_TO_TEST: a precondition was missing (skipped, could not open, no network, out of time): no evidence either way.
/// INCONCLUSIVE: it ran, but the evidence is incomplete or contradictory ("Not sure", crash before a result, content rejected).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Outcome>))]
public enum Outcome
{
    [JsonStringEnumMemberName("PASS")] Pass,
    [JsonStringEnumMemberName("FAIL")] Fail,
    [JsonStringEnumMemberName("UNABLE_TO_TEST")] UnableToTest,
    [JsonStringEnumMemberName("INCONCLUSIVE")] Inconclusive,
}

/// <summary>A tester's answer to a question about something only a person can see.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Answer>))]
public enum Answer
{
    [JsonStringEnumMemberName("not_asked")] NotAsked,
    [JsonStringEnumMemberName("yes")] Yes,
    [JsonStringEnumMemberName("no")] No,
    [JsonStringEnumMemberName("not_sure")] NotSure,
}

public static class Outcomes
{
    public static string Label(Outcome o) => o switch
    {
        Outcome.Pass => "PASS",
        Outcome.Fail => "FAIL",
        Outcome.UnableToTest => "UNABLE TO TEST",
        _ => "INCONCLUSIVE",
    };

    /// <summary>Automatic result combined with the tester's answer (no answer needed when <paramref name="answer"/> is NotAsked).</summary>
    public static Outcome Combine(bool automaticPass, Answer answer) => (automaticPass, answer) switch
    {
        (false, _) => Outcome.Fail,
        (true, Answer.No) => Outcome.Fail,
        (true, Answer.NotSure) => Outcome.Inconclusive,
        _ => Outcome.Pass,
    };

    /// <summary>
    /// A check that only a person can confirm (for example "the dice tumbled"). It is never PASS without a person's "Yes":
    /// no answer (an automated run) or a missing precondition (nothing was shown) is UNABLE TO TEST.
    /// </summary>
    public static Outcome Observation(bool shown, Answer answer) => (shown, answer) switch
    {
        (false, _) => Outcome.UnableToTest,
        (true, Answer.Yes) => Outcome.Pass,
        (true, Answer.No) => Outcome.Fail,
        (true, Answer.NotSure) => Outcome.Inconclusive,
        _ => Outcome.UnableToTest,
    };

    /// <summary>
    /// The dice view's shutdown check. With no engine helper processes to watch (macOS: WKWebView's helpers are system
    /// services, not children of the app) there is no evidence either way: INCONCLUSIVE, never PASS.
    /// </summary>
    public static Outcome Shutdown(int helpersBefore, int helpersRemaining) =>
        helpersRemaining > 0 ? Outcome.Fail : helpersBefore == 0 ? Outcome.Inconclusive : Outcome.Pass;

    /// <summary>A section is as good as its weakest required item: FAIL > INCONCLUSIVE > UNABLE TO TEST > PASS.</summary>
    public static Outcome Worst(IEnumerable<Outcome> outcomes)
    {
        var list = outcomes.ToList();
        if (list.Count == 0) return Outcome.UnableToTest;
        if (list.Contains(Outcome.Fail)) return Outcome.Fail;
        if (list.Contains(Outcome.Inconclusive)) return Outcome.Inconclusive;
        if (list.Contains(Outcome.UnableToTest)) return list.All(o => o == Outcome.UnableToTest) ? Outcome.UnableToTest : Outcome.Inconclusive;
        return Outcome.Pass;
    }
}
