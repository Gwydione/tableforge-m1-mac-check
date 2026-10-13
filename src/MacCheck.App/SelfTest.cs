using System.Diagnostics;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacCheck.Core;

namespace MacCheck.App;

/// <summary>
/// DEVELOPMENT / CI ONLY (never shown to the tester): runs every section of Mac Check without a person.
///  - E2: text captured by E2 on other systems (--fixtures DIR) goes through the same RecordPdf path; plus a privacy check
///    that unrelated text is refused and not stored. Preview and the Mac pasteboard are NOT exercised.
///  - E3: key and text-input events are delivered to the editor inside this window (never the operating system's input),
///    with the platform's command key. Dead-key composition cannot be simulated this way.
///  - E4: the real dice section against the live dddice service (anonymous guest), unless --no-dice.
///  - E1: the Safari page server is exercised over loopback HTTP (no browser is opened).
/// Writes results.zip and results.json to the output folder. Exit code 0 when the run completed.
/// </summary>
public sealed class SelfTest(MainWindow window, CheckSession session)
{
    public async Task<int> RunAsync(string outDir, bool noDice)
    {
        Directory.CreateDirectory(outDir);
        var args = Environment.GetCommandLineArgs();
        var fixtures = Array.IndexOf(args, "--fixtures") is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var host = new StackPanel { Margin = new Thickness(20) };
        var border = new Border { Height = 260 };
        host.Children.Add(new TextBlock { Text = "Mac Check self-test (development)", FontSize = 18 });
        host.Children.Add(border);
        window.Content = new ScrollViewer { Content = host };
        session.Document.Consent.TestsAgreed = session.Document.Consent.SystemInfoAgreed = session.Document.Consent.SaveAgreed = true;
        session.Document.System = SystemFacts.Read(includeModel: false);
        session.Event("selftest: started" + (fixtures is null ? " (no E2 fixtures)" : ""));
        try
        {
            E2(fixtures);
            await E3(host);
            if (!noDice)
            {
                using var hosts = Environment.GetEnvironmentVariable("MACCHECK_LOG_HOSTS") == "1" && OperatingSystem.IsWindows() ? new MacCheck.Core.DevOnly.HostLoggingProxy() : null;
                E4Runner.DevProxyPort = hosts?.Port;
                using var e4 = new E4Runner(session, border);
                await e4.RunAutomaticAsync();
                e4.RecordAnswers(Answer.Yes, Answer.Yes);
                await e4.ShutdownAsync();
                session.Document.Questions["selftest"] = "tester answers were filled in automatically (Yes)";
                if (hosts is not null) session.Document.Questions["selftest: hosts the embedded browser connected to (via proxy)"] = string.Join(", ", hosts.Hosts);
            }
            else session.Document.E4.Note = "not run (--no-dice)";
            await E1();
        }
        catch (Exception ex) { session.Event($"selftest: stopped by {ex.GetType().Name}: {ex.Message}"); }

        await using (var zip = File.Create(Path.Combine(outDir, "results.zip"))) session.WritePackage(zip);
        await File.WriteAllTextAsync(Path.Combine(outDir, "results.json"), System.Text.Json.JsonSerializer.Serialize(session.Document, ResultsDocument.Json));
        CheckSession.DeleteJournal();
        session.Log.Write($"selftest: finished, overall {Outcomes.Label(session.Document.Overall)}");
        return 0;
    }

    private void E2(string? fixtures)
    {
        var cases = PdfCaseCatalog.Load(Platform.CorpusFolder);
        // Privacy: something that is not the test table must be refused and never stored.
        var rejected = window.RecordPdf(cases[0], "My shopping list: eggs, milk, the bank PIN is 1234", "selftest", 0);
        session.Document.Questions["selftest privacy: unrelated paste refused"] = (rejected.ContentRejected && !session.Captures.Values.Any(v => v.Contains("shopping"))).ToString();
        foreach (var c in cases)
        {
            var file = fixtures is null ? null : Path.Combine(fixtures, c.Id + ".txt");
            if (file is null || !File.Exists(file)) { window.SkipPdf(c, "selftest: no fixture"); continue; }
            var text = File.ReadAllText(file, new System.Text.UTF8Encoding(false));
            window.RecordPdf(c, text, "selftest fixture " + Path.GetFileName(Path.GetDirectoryName(file)), 0);
        }
    }

    private async Task E3(StackPanel host)
    {
        var probe = new StackPanel();
        host.Children.Add(probe);
        await window.LigatureCaretCheckAsync(probe);
        var hotkeys = (window.GetPlatformSettings() ?? Application.Current?.PlatformSettings)?.HotkeyConfiguration;
        var cmd = hotkeys?.CommandModifiers ?? KeyModifiers.Control;
        foreach (var step in E3Steps.All)
        {
            var (box, editorSession, controller, preview) = MainWindow.CreateEditor(step);
            var messages = new List<string>();
            controller.MessageChanged += () => messages.Add(controller.Message);
            host.Children.Add(box);
            host.Children.Add(preview);
            await Settle();
            box.Focus();
            box.CaretIndex = step.StartCaret;
            switch (step.Id)
            {
                case "E3-1-typing": await Type(box, "The goblin flees"); break;
                case "E3-2-cursor": for (var k = 0; k < 5; k++) Key(box, Avalonia.Input.Key.Left); await Type(box, "quickly "); break;
                case "E3-3-bold": Select(box, 4, 10); Key(box, Avalonia.Input.Key.B, cmd); break;
                case "E3-4-italic": Select(box, 11, 18); Key(box, Avalonia.Input.Key.I, cmd); break;
                case "E3-5-undo":
                    Select(box, 19, 24); Key(box, Avalonia.Input.Key.B, cmd);
                    Key(box, Avalonia.Input.Key.Z, cmd);
                    Key(box, Avalonia.Input.Key.Z, cmd | KeyModifiers.Shift);
                    break;
                case "E3-6-accent": box.CaretIndex = (box.Text ?? "").Length; await Type(box, " café"); break;
            }
            await Settle();
            var item = window.CheckE3(step, editorSession, controller, messages, 0);
            if (step.Question is not null) { item.TesterAnswer = Answer.Yes; item.Outcome = Outcomes.Combine(item.AutomaticPass, Answer.Yes); }
            host.Children.Remove(box);
            host.Children.Remove(preview);
        }
        session.Save();
    }

    private async Task E1()
    {
        using var server = new E1SafariServer();
        using var http = new HttpClient();
        var page = await http.GetStringAsync(server.PageUri);
        var got = new TaskCompletionSource<string>();
        server.EventReceived += e => got.TrySetResult(e.GetProperty("kind").GetString()!);
        var post = await http.PostAsync(new Uri(server.PageUri, "event"), new StringContent("""{"kind":"e1:start","secure":true}"""));
        var bad = await http.PostAsync(new Uri(server.PageUri, "event"), new StringContent("""{"kind":"open-file","path":"/etc/passwd"}"""));
        var kind = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ok = page.Contains("window.__e1post(m)") && !page.Contains("chrome.webview.postMessage(m)") && post.IsSuccessStatusCode && kind == "e1:start" && !bad.IsSuccessStatusCode && server.Rejected == 1;
        session.Document.E1.Checks.Add(new CheckItem { Id = "E1-server-selftest", Title = "Safari page server: serves the page, accepts known events, refuses others (no browser opened)", AutomaticPass = ok, Outcome = ok ? Outcome.Pass : Outcome.Fail, Detail = $"page {page.Length} chars; unknown event refused {!bad.IsSuccessStatusCode}" });
        session.Save();
    }

    // ---- in-window input helpers (events to this window's controls only) ---------------------------------------------

    private static async Task Settle()
    {
        for (var k = 0; k < 5; k++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(40); }
    }

    private static async Task Type(TextBox box, string text)
    {
        foreach (var c in text)
        {
            box.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = c.ToString(), Source = box });
            await Settle(); // TextChanged is posted; let each edit land before the next, as with real typing
        }
    }

    private static void Key(TextBox box, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = box });

    private static void Select(TextBox box, int start, int end)
    {
        box.SelectionStart = start;
        box.SelectionEnd = end;
    }
}
