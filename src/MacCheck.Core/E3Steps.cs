using E3.Core;
using TableForge.Domain;

namespace MacCheck.Core;

/// <summary>
/// One guided editing step: where the editor starts, what the tester is asked to do (with macOS keys), how the result is
/// checked automatically, and the question only a person can answer (null when none is needed).
/// </summary>
public sealed record E3Step(
    string Id,
    string Title,
    string StartText,
    TextStyles StartStyles,
    int StartCaret,
    string Instruction,
    string? Question,
    Func<E3Observation, (bool Pass, string Detail)> Check);

/// <summary>What the editor looked like when the tester pressed "Check", plus the editor's command messages during the step.</summary>
public sealed record E3Observation(string Text, TextStyles Styles, IReadOnlyList<string> Messages, int InferenceFallbacks);

public static class E3Steps
{
    private const string Sentence = "The goblin quickly flees";
    private static TextStyles Bold(int start, int len, int textLength) => TextStyles.FromRuns([new StyleRun(start, len, TextStyle.Bold)], textLength);

    /// <summary>"goblin" bold, "quickly" italic — the known state that steps 5 and 6 start from.</summary>
    public static TextStyles Formatted => TextStyles.FromRuns([new StyleRun(4, 6, TextStyle.Bold), new StyleRun(11, 7, TextStyle.Italic)], Sentence.Length);

    public static IReadOnlyList<E3Step> All { get; } =
    [
        new("E3-1-typing", "Typing text", "", TextStyles.Empty, 0,
            "Click in the box and type exactly:   The goblin flees",
            null,
            o => (o.Text.TrimEnd() == "The goblin flees", $"text «{o.Text}»")),

        new("E3-2-cursor", "Moving the cursor", "The goblin flees", TextStyles.Empty, 16,
            "Use the arrow keys to put the cursor just before the word “flees” (Option+Left Arrow jumps a whole word). Then type:   quickly   and a space.",
            null,
            o => (o.Text.TrimEnd() == Sentence, $"text «{o.Text}»")),

        new("E3-3-bold", "Selecting a word and making it bold", Sentence, TextStyles.Empty, Sentence.Length,
            "Select the word “goblin” (double-click it, or hold Shift and use the arrow keys). Press Command+B.",
            "Does the preview under the box show “goblin” in bold?",
            o => (o.Text == Sentence && o.Styles.Equals(Bold(4, 6, Sentence.Length)), $"formatting {o.Styles}; text «{o.Text}»")),

        new("E3-4-italic", "Making a word italic", Sentence, Bold(4, 6, Sentence.Length), Sentence.Length,
            "Select the word “quickly”. Press Command+I.",
            "Does the preview show “quickly” in italic, and “goblin” still bold?",
            o => (o.Text == Sentence && o.Styles.Equals(Formatted), $"formatting {o.Styles}; text «{o.Text}»")),

        new("E3-5-undo", "Undo and redo", Sentence, Formatted, Sentence.Length,
            "Select the word “flees” and press Command+B. Then press Command+Z (undo) — “flees” goes back to normal. Then press Shift+Command+Z (redo) — it is bold again.",
            "Did “flees” become bold, go back to normal, and become bold again?",
            o =>
            {
                var undo = o.Messages.Contains("Undo done.");
                var redo = o.Messages.Contains("Redo done.");
                var expected = TextStyles.FromRuns([new StyleRun(4, 6, TextStyle.Bold), new StyleRun(11, 7, TextStyle.Italic), new StyleRun(19, 5, TextStyle.Bold)], Sentence.Length);
                return (undo && redo && o.Text == Sentence && o.Styles.Equals(expected), $"undo seen {undo}, redo seen {redo}; formatting {o.Styles}");
            }),

        new("E3-6-accent", "Typing an accented letter", Sentence, Formatted, Sentence.Length,
            "Click at the very end of the text and type a space and the word   café   (on a Mac keyboard: press Option+E, then E, to get é).",
            null,
            // Compared in composed form, so é typed as e + combining accent still counts; which form arrived is recorded.
            o => (o.Text.TrimEnd().Normalize(System.Text.NormalizationForm.FormC) == Sentence + " café" && o.Styles.Equals(TextStyles.FromRuns(Formatted.Runs, o.Text.Length)),
                  $"text «{o.Text}» (é as {(o.Text.Contains('é') ? "one character U+00E9" : o.Text.Contains("é") ? "e + combining accent" : "missing")}); formatting {o.Styles}; edits needing fallback {o.InferenceFallbacks}")),
    ];

    /// <summary>A session for a step, with an empty undo history.</summary>
    public static FormattedTextSession SessionFor(E3Step step) => new(step.StartText, step.StartStyles);
}
