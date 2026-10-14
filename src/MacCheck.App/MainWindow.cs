using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using E3.Core;
using E3.Editor;
using MacCheck.Core;
using TableForge.Domain;
using FormattedText = E3.Editor.FormattedText;

namespace MacCheck.App;

/// <summary>
/// TableForge Mac Check: one window that walks the tester through consent, the PDF copy test (E2), the dice test (E4),
/// the editing test (E3), the optional Safari test (E1) and saving one results file. Every screen can be skipped; nothing
/// asks the tester to fix anything.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly CheckSession _s;
    private readonly List<(string Id, Func<Control> Build)> _pages = [];
    private readonly ContentControl _body = new();
    private readonly TextBlock _progress = new() { Foreground = Brushes.DimGray, FontSize = 13 };
    private readonly ProgressBar _bar = new() { Height = 6, Minimum = 0 };
    private readonly List<PdfCase> _cases;
    private bool _doOptionalPdfs;
    private E4Runner? _e4;
    private E1SafariServer? _e1;

    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#2F5D8A"));

    public MainWindow(CheckSession session)
    {
        _s = session;
        Title = "TableForge Mac Check";
        Width = 900;
        Height = 760;
        MinWidth = 760;
        MinHeight = 620;
        _cases = PdfCaseCatalog.Load(Platform.CorpusFolder);

        _pages.Add(("welcome", Welcome));
        _pages.Add(("e2-intro", E2Intro));
        foreach (var c in _cases.Where(c => c.Mandatory)) _pages.Add(($"e2-{c.Id}", () => PdfPage(c)));
        _pages.Add(("e2-optional", E2OptionalChoice));
        foreach (var c in _cases.Where(c => !c.Mandatory)) _pages.Add(($"e2-{c.Id}", () => PdfPage(c)));
        _pages.Add(("e2-done", E2Done));
        _pages.Add(("e4", E4Page));
        _pages.Add(("e3-intro", E3Intro));
        foreach (var step in E3Steps.All) _pages.Add((step.Id, () => E3Page(step)));
        _pages.Add(("e1", E1Page));
        _pages.Add(("finish", Finish));
        _bar.Maximum = _pages.Count - 1;

        Content = new DockPanel
        {
            Margin = new Thickness(24, 16),
            Children =
            {
                new StackPanel { [DockPanel.DockProperty] = Dock.Top, Spacing = 6, Margin = new Thickness(0, 0, 0, 12),
                    Children = { new TextBlock { Text = "TableForge Mac Check", FontSize = 22, FontWeight = FontWeight.SemiBold }, _progress, _bar } },
                new ScrollViewer { Content = _body },
            },
        };
        Opened += (_, _) => Show(_s.PageIndex);
        Closing += (_, _) => { _e4?.Dispose(); _e1?.Dispose(); };
    }

    // ---- navigation ---------------------------------------------------------------------------------------------------

    private void Show(int index)
    {
        index = Math.Clamp(index, 0, _pages.Count - 1);
        // Optional PDFs are only shown if the tester chose them.
        while (!_doOptionalPdfs && _pages[index].Id.StartsWith("e2-c", StringComparison.Ordinal) && _cases.Any(c => $"e2-{c.Id}" == _pages[index].Id && !c.Mandatory)) index++;
        _s.PageIndex = index;
        _s.Save();
        _progress.Text = $"Step {index + 1} of {_pages.Count}";
        _bar.Value = index;
        _body.Content = _pages[index].Build();
        _s.Log.Write($"screen: {_pages[index].Id}");
    }

    private void Next() => Dispatcher.UIThread.Post(() => Show(_s.PageIndex + 1));

    private static Button Btn(string text, string id, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(16, 8), Margin = new Thickness(0, 0, 10, 0), FontSize = 15 };
        if (primary) { b.Background = Accent; b.Foreground = Brushes.White; }
        AutomationProperties.SetAutomationId(b, id);
        return b;
    }

    private static TextBlock Para(string text, double size = 15, IBrush? color = null, FontWeight? weight = null) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Foreground = color ?? Brushes.Black, FontWeight = weight ?? FontWeight.Normal, Margin = new Thickness(0, 0, 0, 10) };

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 19, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 10), TextWrapping = TextWrapping.Wrap };

    private static StackPanel Row(params Control[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) }; foreach (var i in items) p.Children.Add(i); return p; }

    /// <summary>Yes / No / Not sure radio buttons; <paramref name="changed"/> gets the answer.</summary>
    private static StackPanel AnswerRow(string groupName, Action<Answer> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(0, 4, 0, 10) };
        foreach (var (label, a) in new[] { ("Yes", Answer.Yes), ("No", Answer.No), ("Not sure", Answer.NotSure) })
        {
            var r = new RadioButton { Content = label, GroupName = groupName, FontSize = 15 };
            AutomationProperties.SetAutomationId(r, $"{groupName}-{label}");
            r.IsCheckedChanged += (_, _) => { if (r.IsChecked == true) changed(a); };
            row.Children.Add(r);
        }
        return row;
    }

    // ---- welcome and consent ------------------------------------------------------------------------------------------

    private Control Welcome()
    {
        var agree = new CheckBox { Content = "I agree to run these tests.", FontSize = 15 };
        var sys = new CheckBox { Content = "Include my Mac's macOS version and whether it has an Apple or Intel chip (recommended).", FontSize = 15, IsChecked = true };
        var model = new CheckBox { Content = "Also include the Mac's model code (for example “Mac14,2”). Optional.", FontSize = 15 };
        var start = Btn("Start", "Start", primary: true);
        start.IsEnabled = false;
        agree.IsCheckedChanged += (_, _) => start.IsEnabled = agree.IsChecked == true;
        start.Click += (_, _) =>
        {
            var c = _s.Document.Consent;
            c.TestsAgreed = true;
            c.SystemInfoAgreed = sys.IsChecked == true;
            c.ModelAgreed = model.IsChecked == true && c.SystemInfoAgreed;
            if (c.SystemInfoAgreed) _s.Document.System = SystemFacts.Read(c.ModelAgreed);
            _s.Event($"consent: tests yes, system info {(c.SystemInfoAgreed ? "yes" : "no")}, model {(c.ModelAgreed ? "yes" : "no")}");
            Next();
        };
        var quit = Btn("Quit", "Quit");
        quit.Click += (_, _) => Close();
        return new StackPanel
        {
            Children =
            {
                Heading("Welcome — thank you for helping!"),
                Para("This app checks whether parts of TableForge (a tabletop-game table tool) work on a Mac. It takes about 15–20 minutes. Nothing here can harm your Mac or your files."),
                Para("You will: copy three short tables from PDF files into the app, watch some 3D dice roll, try a few editing keys, and then save one results file to send back."),
                Para("If anything doesn't work, that's useful information: just press “Skip” and carry on. You never need to fix anything.", color: Accent),
                Heading("What this app does and doesn't do"),
                Para("• It opens only the PDF files that come with it, in Preview.\n• It reads pasted text only when you paste into its box, and keeps it only if it is the test table.\n• The dice test connects to dddice.com (the dice service), its dice software at cdn.dddice.com and its live connection service, using a temporary anonymous guest — no account, no login.\n• It does not read your documents, contacts, photos or settings, and it sends nothing anywhere by itself.\n• At the end it shows you what it will save. You decide whether to send the file.", 14),
                agree, sys, model,
                Row(start, quit),
                Para($"Version {_s.Document.Build.Version} · build {_s.Document.Build.BuildId}", 12, Brushes.Gray),
            },
        };
    }

    // ---- E2: PDF copy ---------------------------------------------------------------------------------------------------

    private Control E2Intro()
    {
        var next = Btn("Begin", "E2Begin", primary: true);
        next.Click += (_, _) => Next();
        return new StackPanel
        {
            Children =
            {
                Heading("Part 1 — Copying tables from PDFs (about 5 minutes)"),
                Para("For each table: press “Open in Preview”. In Preview press ⌘A (Command+A, select all), then ⌘C (Command+C, copy). Come back to this window, click in the big box and press ⌘V (Command+V, paste). Then press “Record this paste”."),
                Para("If your Mac asks whether this app may paste, choose “Allow Paste”.", color: Accent),
                Para("Three tables are required; five more are optional if you have time."),
                Row(next),
            },
        };
    }

    private Control PdfPage(PdfCase c)
    {
        var started = Stopwatch.StartNew();
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 230, FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 12, PlaceholderText = "Click here and press ⌘V to paste the table" };
        AutomationProperties.SetAutomationId(box, "PasteBox");
        var status = Para("", 15, Accent, FontWeight.SemiBold);
        var open = Btn("Open in Preview", "OpenPdf", primary: true);
        open.Click += (_, _) =>
        {
            var error = Platform.OpenPdf(c.PdfFile);
            _s.Log.Write($"e2 {c.Id}: open in Preview {(error is null ? "ok" : "failed: " + error)}");
            if (error is not null) status.Text = "The PDF could not be opened. Press “Skip”.";
        };
        var record = Btn("Record this paste", "RecordPaste", primary: true);
        var skip = Btn("Skip — can't do this", "SkipPdf");
        var next = Btn("Next", "NextPdf", primary: true);
        next.IsVisible = false;
        record.Click += (_, _) =>
        {
            var r = RecordPdf(c, box.Text ?? "", "Avalonia TextBox paste (⌘V)", started.Elapsed.TotalSeconds);
            if (r.ContentRejected) { status.Text = "That doesn't look like this table. Copy it from Preview again (⌘A, ⌘C) and paste here — or press Skip."; box.Text = ""; return; }
            status.Text = "Recorded — thank you.";
            record.IsVisible = skip.IsVisible = open.IsVisible = false;
            box.IsReadOnly = true;
            next.IsVisible = true;
        };
        skip.Click += (_, _) => { SkipPdf(c, "tester skipped"); Next(); };
        next.Click += (_, _) => Next();
        var n = _cases.Where(x => x.Mandatory == c.Mandatory).ToList().IndexOf(c) + 1;
        return new StackPanel
        {
            Children =
            {
                Heading($"{(c.Mandatory ? "Table" : "Optional table")} {n} of {(c.Mandatory ? 3 : 5)}: {c.Title}"),
                Para($"File: {Path.GetFileName(c.PdfFile)} — {c.WhatItTests}", 14, Brushes.DimGray),
                Para("1. Press “Open in Preview”.   2. In Preview: ⌘A then ⌘C.   3. Come back here, click in the box, press ⌘V.   4. Press “Record this paste”."),
                Row(open),
                box,
                status,
                Row(record, skip, next),
            },
        };
    }

    /// <summary>The one place pasted text is accepted: kept only if it is the test table (ClipboardGuard), then evaluated raw.</summary>
    public PdfCaseResult RecordPdf(PdfCase c, string text, string method, double seconds)
    {
        if (!ClipboardGuard.IsTestContent(c, text))
        {
            var rejected = new PdfCaseResult { Case = c.Id, Mandatory = c.Mandatory, Outcome = Outcome.Inconclusive, ContentRejected = true, PasteMethod = method, Note = "pasted text was not the test table; not stored" };
            _s.Log.Write($"e2 {c.Id}: pasted text rejected (not the test table; {text.Length} chars, not stored)");
            if (!_s.Document.E2.Any(r => r.Case == c.Id)) { _s.Document.E2.Add(rejected); _s.Save(); }
            return rejected;
        }
        var result = PdfCaseResult.Evaluate(c, text, method, seconds);
        _s.Document.E2.RemoveAll(r => r.Case == c.Id);
        _s.Document.E2.Add(result);
        _s.Captures[result.CaptureFile!] = text;
        _s.Log.Write($"e2 {c.Id}: {result.RawVerdict} ({result.ParsedRows}/{result.ExpectedRows} rows; cleanups {string.Join(", ", result.CleanupVerdicts.Select(kv => kv.Key + " " + kv.Value))})");
        _s.Save();
        return result;
    }

    public void SkipPdf(PdfCase c, string why)
    {
        if (_s.Document.E2.Any(r => r.Case == c.Id && !r.ContentRejected)) return;
        _s.Document.E2.RemoveAll(r => r.Case == c.Id);
        _s.Document.E2.Add(new PdfCaseResult { Case = c.Id, Mandatory = c.Mandatory, Outcome = Outcome.UnableToTest, Note = why });
        _s.Save();
    }

    private Control E2OptionalChoice()
    {
        var yes = Btn("Yes, do 5 more (about 3 minutes)", "OptionalYes", primary: true);
        var no = Btn("No, continue to the dice", "OptionalNo");
        yes.Click += (_, _) => { _doOptionalPdfs = true; _s.Event("optional PDFs: yes"); Next(); };
        no.Click += (_, _) => { _doOptionalPdfs = false; _s.Event("optional PDFs: no"); Next(); };
        return new StackPanel { Children = { Heading("Optional: five more tables?"), Para("They test harder layouts. Only if you have a few minutes to spare."), Row(yes, no) } };
    }

    private Control E2Done()
    {
        var next = Btn("Continue", "E2DoneNext", primary: true);
        next.Click += (_, _) => Next();
        return new StackPanel
        {
            Children =
            {
                Heading("Part 1 done"),
                Para("Did your Mac ever ask whether this app may paste (an “Allow Paste” message)?"),
                AnswerRow("pastePrompt", a => { _s.Document.Questions["Mac asked to allow pasting"] = a.ToString(); _s.Save(); }),
                Row(next),
            },
        };
    }

    // ---- E4: dice -------------------------------------------------------------------------------------------------------

    private Control E4Page()
    {
        var panel = new Border { Height = 280, Background = new SolidColorBrush(Color.Parse("#F4F4F4")), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 6, 0, 6) };
        var status = Para("", 15, Accent, FontWeight.SemiBold);
        var start = Btn("Start the dice test", "StartDice", primary: true);
        var skip = Btn("Skip — can't do this", "SkipDice");
        var next = Btn("Next", "DiceNext", primary: true);
        var questions = new StackPanel { IsVisible = false };
        Answer animated = Answer.NotAsked, faces = Answer.NotAsked;
        next.IsVisible = false;
        questions.Children.Add(Para("Did you see the dice tumble and then land?"));
        questions.Children.Add(AnswerRow("animated", a => { animated = a; next.IsVisible = animated != Answer.NotAsked && faces != Answer.NotAsked; }));
        var facesLabel = Para("Look at the dice now showing. Do they match the result written below them?");
        var lastResult = Para("", 17, Brushes.Black, FontWeight.SemiBold);
        questions.Children.Add(facesLabel);
        questions.Children.Add(lastResult);
        questions.Children.Add(AnswerRow("faces", a => { faces = a; next.IsVisible = animated != Answer.NotAsked && faces != Answer.NotAsked; }));

        start.Click += async (_, _) =>
        {
            start.IsVisible = false;
            _e4 ??= new E4Runner(_s, panel);
            _e4.Progress += t => Dispatcher.UIThread.Post(() => status.Text = t);
            try { await _e4.RunAutomaticAsync(); }
            catch (Exception ex) { _s.Event($"e4 stopped: {ex.GetType().Name}"); }
            status.Text = "Please answer the two questions below.";
            lastResult.Text = _e4.LastResult ?? "(no result)";
            questions.IsVisible = true;
        };
        skip.Click += async (_, _) =>
        {
            _s.Document.E4.Note ??= "tester skipped";
            if (_s.Document.E4.Checks.Count == 0) _s.Document.E4.Checks.Add(new CheckItem { Id = "E4", Title = "dice test", Outcome = Outcome.UnableToTest, Detail = "skipped" });
            if (_e4 is not null) await _e4.ShutdownAsync();
            _s.Save();
            Next();
        };
        next.Click += async (_, _) => { _e4?.RecordAnswers(animated, faces); if (_e4 is not null) await _e4.ShutdownAsync(); Next(); };
        return new StackPanel
        {
            Children =
            {
                Heading("Part 2 — 3D dice (about 3 minutes)"),
                Para("Press “Start”. The app connects to dddice.com with a temporary anonymous guest and rolls dice by itself for about a minute. Please keep this window in front and watch the grey area."),
                Row(start, skip),
                status,
                panel,
                questions,
                Row(next),
            },
        };
    }

    // ---- E3: editing ----------------------------------------------------------------------------------------------------

    private Control E3Intro()
    {
        var next = Btn("Begin", "E3Begin", primary: true);
        next.Click += (_, _) => Next();
        var probe = new StackPanel { Opacity = 0.01, Height = 1 };
        var page = new StackPanel
        {
            Children =
            {
                Heading("Part 3 — Editing text (about 4 minutes)"),
                Para("Six short tasks in a text box. Each one tells you exactly which keys to press. After each, press “Check”, and answer the question if there is one."),
                Para("Mac keys used: ⌘ is the Command key, ⌥ is the Option key, ⇧ is Shift."),
                Row(next),
                probe,
            },
        };
        page.AttachedToVisualTree += async (_, _) => await LigatureCaretCheckAsync(probe);
        return page;
    }

    /// <summary>
    /// Automatic: walks the caret through "fire flame offer" with the arrow key, in a stock text box and in the E3 editor
    /// (ligatures off). E3 found Linux fonts made fi/fl/ff one caret stop; this checks the Mac's system font. The key
    /// events are delivered to the text box inside this app only (never the system's keyboard).
    /// </summary>
    public async Task LigatureCaretCheckAsync(Panel host)
    {
        if (_s.Document.E3.Checks.Any(c => c.Id == "E3-auto-ligature-caret")) return;
        const string text = "fire flame offer";
        var walks = new List<string>();
        var fixedOk = false;
        foreach (var stock in new[] { true, false })
        {
            var box = new TextBox { Text = text, Width = 300 };
            if (!stock) _ = new TextBoxFormattingController(box, new FormattedTextSession(text));
            host.Children.Add(box);
            await Task.Delay(150);
            box.CaretIndex = 0;
            var seen = new List<int> { box.CaretIndex };
            for (var i = 0; i < text.Length; i++)
            {
                box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right, Source = box });
                seen.Add(box.CaretIndex);
            }
            host.Children.Remove(box);
            var ok = seen.SequenceEqual(Enumerable.Range(0, text.Length + 1));
            if (!stock) fixedOk = ok;
            var moved = seen.Distinct().Count() > 1;
            walks.Add($"{(stock ? "stock TextBox (comparison only)" : "E3 editor")}: {(ok ? "one stop per letter" : moved ? string.Join(" ", seen) : "caret did not move — not measured")}");
        }
        _s.Document.E3.Checks.Add(new CheckItem { Id = "E3-auto-ligature-caret", Title = "Caret moves one letter at a time through fi/fl/ff (automatic)", AutomaticPass = fixedOk, Outcome = fixedOk ? Outcome.Pass : Outcome.Fail, Detail = string.Join("; ", walks) });
        _s.Log.Write("e3 ligature caret: " + string.Join("; ", walks));
        _s.Save();
    }

    private Control E3Page(E3Step step)
    {
        var started = Stopwatch.StartNew();
        var (box, session, controller, preview) = CreateEditor(step);
        var messages = new List<string>();
        controller.MessageChanged += () => messages.Add(controller.Message);
        var status = Para("", 15, Accent, FontWeight.SemiBold);
        var check = Btn("Check", "E3Check", primary: true);
        var skip = Btn("Skip — can't do this", "E3Skip");
        var next = Btn("Next", "E3Next", primary: true);
        next.IsVisible = false;
        var question = new StackPanel { IsVisible = false };
        var answer = Answer.NotAsked;
        if (step.Question is { } q)
        {
            question.Children.Add(Para(q));
            question.Children.Add(AnswerRow(step.Id, a => { answer = a; next.IsVisible = true; }));
        }
        CheckItem? recorded = null;
        check.Click += (_, _) =>
        {
            recorded = CheckE3(step, session, controller, messages, started.Elapsed.TotalSeconds);
            status.Text = recorded.AutomaticPass ? "Recorded." : "Recorded (it didn't come out as expected — that's fine, it's useful to know).";
            check.IsVisible = skip.IsVisible = false;
            if (step.Question is null) next.IsVisible = true; else question.IsVisible = true;
        };
        skip.Click += (_, _) => { SkipE3(step, "tester skipped"); Next(); };
        next.Click += (_, _) =>
        {
            if (recorded is not null && step.Question is not null) { recorded.TesterAnswer = answer; recorded.Outcome = Outcomes.Combine(recorded.AutomaticPass, answer); _s.Save(); }
            Next();
        };
        var number = E3Steps.All.ToList().IndexOf(step) + 1;
        var page = new StackPanel
        {
            Children =
            {
                Heading($"Editing task {number} of {E3Steps.All.Count}: {step.Title}"),
                Para(step.Instruction, 16),
                box,
                new TextBlock { Text = "Preview (how it looks in TableForge):", Foreground = Brushes.DimGray, Margin = new Thickness(0, 10, 0, 2) },
                new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Padding = new Thickness(10), Child = preview },
                status,
                question,
                Row(check, skip, next),
            },
        };
        page.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => { box.Focus(); box.CaretIndex = step.StartCaret; });
        return page;
    }

    public static (TextBox Box, FormattedTextSession Session, TextBoxFormattingController Controller, TextBlock Preview) CreateEditor(E3Step step)
    {
        var box = new TextBox { TextWrapping = TextWrapping.Wrap, FontSize = 18, MinHeight = 60 };
        AutomationProperties.SetAutomationId(box, "EditorBox");
        var session = E3Steps.SessionFor(step);
        var controller = new TextBoxFormattingController(box, session);
        var preview = new TextBlock { FontSize = 20, TextWrapping = TextWrapping.Wrap };
        session.Changed += () => FormattedText.Show(preview, session.Segments);
        FormattedText.Show(preview, session.Segments);
        return (box, session, controller, preview);
    }

    public CheckItem CheckE3(E3Step step, FormattedTextSession session, TextBoxFormattingController controller, IReadOnlyList<string> messages, double seconds)
    {
        var (pass, detail) = step.Check(new E3Observation(session.Text, session.Styles, messages, controller.InferenceFallbacks));
        var item = new CheckItem { Id = step.Id, Title = step.Title, AutomaticPass = pass, Outcome = pass ? Outcome.Pass : Outcome.Fail, Detail = _s.Redactor.Clean(detail), Seconds = Math.Round(seconds, 1) };
        if (step.Question is not null && pass) item.Outcome = Outcome.Inconclusive; // until the tester answers
        _s.Document.E3.Checks.RemoveAll(c => c.Id == step.Id);
        _s.Document.E3.Checks.Add(item);
        _s.Log.Write($"e3 {step.Id}: auto {(pass ? "pass" : "fail")} — {detail}; messages [{string.Join(" | ", messages)}]");
        _s.Save();
        return item;
    }

    public void SkipE3(E3Step step, string why)
    {
        _s.Document.E3.Checks.RemoveAll(c => c.Id == step.Id);
        _s.Document.E3.Checks.Add(new CheckItem { Id = step.Id, Title = step.Title, Outcome = Outcome.UnableToTest, Detail = why });
        _s.Save();
    }

    // ---- E1: Safari (optional) --------------------------------------------------------------------------------------------

    private Control E1Page()
    {
        var status = Para("", 15, Accent, FontWeight.SemiBold);
        var open = Btn("Open the dice test in Safari", "OpenSafari", primary: true);
        var skip = Btn("Skip this optional test", "SkipSafari");
        var next = Btn("Next", "SafariNext", primary: true);
        var question = new StackPanel { IsVisible = false };
        var answer = Answer.NotAsked;
        next.IsVisible = false;
        question.Children.Add(Para("Did you see dice rolling in Safari?"));
        question.Children.Add(AnswerRow("safariDice", a => { answer = a; next.IsVisible = true; }));
        var rolls = new List<string>();
        string? error = null;
        bool done = false, secure = false;
        open.Click += (_, _) =>
        {
            _e1 ??= new E1SafariServer();
            _e1.EventReceived += ev => Dispatcher.UIThread.Post(() =>
            {
                var kind = ev.GetProperty("kind").GetString();
                _s.Log.Write($"e1 event {kind}");
                if (kind == "e1:start") secure = ev.TryGetProperty("secure", out var sc) && sc.GetBoolean();
                if (kind == "e1:roll") rolls.Add($"{ev.GetProperty("values")} in {ev.GetProperty("ms")} ms");
                if (kind == "e1:error") error = ev.TryGetProperty("message", out var m) ? m.GetString() : "error";
                if (kind == "e1:done") done = true;
                status.Text = done ? "Safari finished. Close the Safari tab, then answer below." : error is not null ? "Safari reported a problem. Close the tab and answer below." : $"Safari: {kind?.Replace("e1:", "")}…";
                if (done || error is not null) question.IsVisible = true;
            });
            var err = Platform.OpenInSafari(_e1.PageUri);
            status.Text = err is null ? "Safari is opening… watch the dice there, then come back." : "Safari could not be opened.";
            if (err is not null) question.IsVisible = true;
            open.IsVisible = false;
        };
        skip.Click += (_, _) => { _s.Document.E1.Checks.Clear(); _s.Document.E1.Note = "optional test not run"; _s.Save(); Next(); };
        next.Click += (_, _) =>
        {
            var auto = done && error is null && rolls.Count == 3 && secure;
            _s.Document.E1.Checks.Clear();
            _s.Document.E1.Checks.Add(new CheckItem { Id = "E1-safari", Title = "1.0.0 dice page in Safari: 3 rolls settled", AutomaticPass = auto, TesterAnswer = answer, Outcome = Outcomes.Combine(auto, answer), Detail = $"secure {secure}; rolls {string.Join("; ", rolls)}{(error is null ? "" : "; error " + error)}" });
            _s.Save();
            Next();
        };
        return new StackPanel
        {
            Children =
            {
                Heading("Optional — dice in Safari (about 2 minutes)"),
                Para("Only if you have time. Safari opens a page that rolls three dice by itself (again with a temporary anonymous dddice guest). Watch it, close the Safari tab, then come back here."),
                Row(open, skip),
                status,
                question,
                Row(next),
            },
        };
    }

    // ---- finish -----------------------------------------------------------------------------------------------------------

    private Control Finish()
    {
        _e4?.Dispose();
        _e4 = null;
        _s.Document.Finish();
        var d = _s.Document;
        var summary = new StackPanel();
        void Line(string label, Outcome o) => summary.Children.Add(Para($"{label}: {Outcomes.Label(o)}", 15, o == Outcome.Pass ? Brushes.DarkGreen : o == Outcome.Fail ? Brushes.DarkRed : Brushes.DimGray));
        Line("PDF copy (3 required tables)", d.E2Summary.Outcome);
        Line("3D dice in the app", d.E4.Outcome);
        Line("Editing text", d.E3.Outcome);
        if (d.E1.Checks.Count > 0) Line("Dice in Safari (optional)", d.E1.Outcome);
        var agree = new CheckBox { Content = "I agree to save these results to a file that I will send back.", FontSize = 15 };
        var save = Btn("Save results…", "SaveResults", primary: true);
        save.IsEnabled = false;
        agree.IsCheckedChanged += (_, _) => save.IsEnabled = agree.IsChecked == true;
        var status = Para("", 15, Accent, FontWeight.SemiBold);
        var quit = Btn("Quit", "QuitFinal");
        quit.Click += (_, _) => Close();
        save.Click += async (_, _) =>
        {
            _s.Document.Consent.SaveAgreed = true;
            var name = $"TableForge-Mac-Check-Results-{DateTime.Now:yyyy-MM-dd}.zip";
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save TableForge Mac Check results",
                    SuggestedFileName = name,
                    SuggestedStartLocation = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Desktop),
                    FileTypeChoices = [new FilePickerFileType("Zip archive") { Patterns = ["*.zip"] }],
                });
                if (file is null) { status.Text = "Not saved. Press “Save results…” to try again."; return; }
                await using (var stream = await file.OpenWriteAsync()) _s.WritePackage(stream);
                CheckSession.DeleteJournal();
                status.Text = $"Saved as “{file.Name}”. Please send this file back. You can now quit, and move the app to the Bin if you like.";
                save.IsVisible = agree.IsVisible = false;
            }
            catch (Exception ex) { status.Text = "Saving failed: " + _s.Redactor.Clean(ex.Message); _s.Log.Write("save failed: " + ex.GetType().Name); }
        };
        return new StackPanel
        {
            Children =
            {
                Heading("All done — thank you!"),
                summary,
                Para("The results file contains: the test results, the text you copied from the supplied PDFs, small pictures of the test dice, and a log without your name, your Mac's name or file locations. Nothing else from this Mac.", 14),
                agree,
                Row(save, quit),
                status,
            },
        };
    }
}
