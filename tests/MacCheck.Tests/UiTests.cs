using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacCheck.App;
using MacCheck.Core;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(MacCheck.Tests.TestAppBuilder))]

namespace MacCheck.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<MacCheck.App.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Headless UI tests: the real wizard window and the real E3 editor, driven through Avalonia's input system (headless,
/// so with the Windows/Linux command key Ctrl — not ⌘, and not the macOS keyboard or pasteboard).
/// </summary>
public class UiTests
{
    private static (MainWindow Window, CheckSession Session) Open()
    {
        CheckSession.DeleteJournal();
        var redactor = new Redactor();
        var session = new CheckSession(redactor, new SessionLog(redactor));
        var w = new MainWindow(session);
        w.Show();
        Flush();
        return (w, session);
    }

    private static void Flush() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }

    private static T Find<T>(Window w, string id) where T : Control =>
        w.GetVisualDescendants().OfType<T>().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == id && c.IsEffectivelyVisible)
        ?? throw new Xunit.Sdk.XunitException($"no visible {typeof(T).Name} '{id}'");

    private static void Click(Window w, string id)
    {
        Find<Button>(w, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Flush();
    }

    private static string Heading(Window w) => w.GetVisualDescendants().OfType<TextBlock>().First(t => t.FontSize == 19 && t.IsEffectivelyVisible).Text ?? ""; // screen headings (the window title is 22)

    [AvaloniaFact]
    public void Nothing_starts_until_the_tester_agrees_and_system_facts_follow_consent()
    {
        var (w, s) = Open();
        Assert.False(Find<Button>(w, "Start").IsEnabled);
        w.GetVisualDescendants().OfType<CheckBox>().First().IsChecked = true; // "I agree to run these tests."
        Flush();
        Assert.True(Find<Button>(w, "Start").IsEnabled);
        Click(w, "Start");
        Assert.True(s.Document.Consent.TestsAgreed);
        Assert.NotNull(s.Document.System); // the system-info box is ticked by default
        Assert.StartsWith("Part 1", Heading(w));
        w.Close();
    }

    [AvaloniaFact]
    public void A_pasted_table_is_recorded_unrelated_text_is_refused_and_a_PDF_can_be_skipped()
    {
        var (w, s) = Open();
        w.GetVisualDescendants().OfType<CheckBox>().First().IsChecked = true;
        Flush();
        Click(w, "Start");
        Click(w, "E2Begin");
        var box = Find<TextBox>(w, "PasteBox");
        box.Text = "Shopping: milk, eggs";
        Flush();
        Click(w, "RecordPaste");
        Assert.True(s.Document.E2.Single().ContentRejected);
        Assert.Empty(s.Captures); // never stored
        box.Text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "e2-fixtures", "windows", "chrome", "c01-d20-simple.txt"));
        Flush();
        Click(w, "RecordPaste");
        Assert.Equal(Outcome.Pass, s.Document.E2.Single(r => r.Case == "c01-d20-simple").Outcome);
        Assert.Single(s.Captures);
        Click(w, "NextPdf");
        Click(w, "SkipPdf"); // c02
        Assert.Equal(Outcome.UnableToTest, s.Document.E2.Single(r => r.Case == "c02-d100-four-columns").Outcome);
        Assert.Contains("Shipwreck Finds", Heading(w)); // c05 next
        w.Close();
    }

    [AvaloniaFact]
    public void The_wizard_can_be_walked_to_the_end_by_skipping_everything()
    {
        var (w, s) = Open();
        w.GetVisualDescendants().OfType<CheckBox>().First().IsChecked = true;
        Flush();
        Click(w, "Start");
        Click(w, "E2Begin");
        for (var i = 0; i < 3; i++) Click(w, "SkipPdf");
        Click(w, "OptionalNo");
        Click(w, "E2DoneNext");
        Click(w, "SkipDice");
        Click(w, "E3Begin");
        for (var i = 0; i < 6; i++) Click(w, "E3Skip");
        Click(w, "SkipSafari");
        Assert.StartsWith("All done", Heading(w));
        s.Document.Finish();
        Assert.Equal(Outcome.UnableToTest, s.Document.E2Summary.Outcome);
        Assert.Equal(Outcome.UnableToTest, s.Document.E4.Outcome);
        w.Close();
    }

    [AvaloniaFact]
    public void The_E3_steps_pass_when_done_with_the_keyboard_through_the_real_editor()
    {
        var (w, s) = Open();
        foreach (var step in E3Steps.All)
        {
            var (box, editor, controller, preview) = MainWindow.CreateEditor(step);
            var messages = new List<string>();
            controller.MessageChanged += () => messages.Add(controller.Message);
            w.Content = new StackPanel { Children = { box, preview } };
            Flush();
            box.Focus();
            box.CaretIndex = step.StartCaret;
            Flush();
            void Type(string t) { foreach (var c in t) { w.KeyTextInput(c.ToString()); Flush(); } }
            void Press(PhysicalKey k, RawInputModifiers m = RawInputModifiers.None) { w.KeyPressQwerty(k, m); w.KeyReleaseQwerty(k, m); Flush(); }
            void Sel(int a, int b) { box.SelectionStart = a; box.SelectionEnd = b; Flush(); }
            switch (step.Id)
            {
                case "E3-1-typing": Type("The goblin flees"); break;
                case "E3-2-cursor": for (var i = 0; i < 5; i++) Press(PhysicalKey.ArrowLeft); Type("quickly "); break;
                case "E3-3-bold": Sel(4, 10); Press(PhysicalKey.B, RawInputModifiers.Control); break;
                case "E3-4-italic": Sel(11, 18); Press(PhysicalKey.I, RawInputModifiers.Control); break;
                case "E3-5-undo":
                    Sel(19, 24); Press(PhysicalKey.B, RawInputModifiers.Control);
                    Press(PhysicalKey.Z, RawInputModifiers.Control);
                    Press(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
                    break;
                case "E3-6-accent": box.CaretIndex = (box.Text ?? "").Length; Type(" café"); break;
            }
            var item = w.CheckE3(step, editor, controller, messages, 0);
            Assert.True(item.AutomaticPass, $"{step.Id}: {item.Detail}");
        }
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_automatic_ligature_caret_check_records_one_stop_per_letter_for_the_E3_editor()
    {
        var (w, s) = Open();
        var host = new StackPanel();
        w.Content = host;
        Flush();
        await w.LigatureCaretCheckAsync(host);
        var item = s.Document.E3.Checks.Single(c => c.Id == "E3-auto-ligature-caret");
        Assert.Equal(Outcome.Pass, item.Outcome);
        Assert.Contains("E3 editor: one stop per letter", item.Detail);
        w.Close();
    }

    [AvaloniaFact]
    public void After_a_crash_the_next_launch_resumes_with_the_earlier_results()
    {
        var (w, s) = Open();
        w.GetVisualDescendants().OfType<CheckBox>().First().IsChecked = true;
        Flush();
        Click(w, "Start");
        Click(w, "E2Begin");
        Find<TextBox>(w, "PasteBox").Text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "e2-fixtures", "windows", "chrome", "c01-d20-simple.txt"));
        Flush();
        Click(w, "RecordPaste");
        // Simulated crash: the window goes away without saving results.
        Assert.True(CheckSession.HasUnfinishedJournal);
        var redactor = new Redactor();
        var again = new CheckSession(redactor, new SessionLog(redactor));
        Assert.True(again.Resume());
        Assert.Equal(Outcome.Pass, again.Document.E2.Single().Outcome);
        Assert.Single(again.Captures);
        Assert.Contains(again.Document.Events, e => e.Contains("resumed"));
        CheckSession.DeleteJournal();
        Assert.False(CheckSession.HasUnfinishedJournal);
        w.Close();
    }
}
