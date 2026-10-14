using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform;
using E4.App;
using E4.Dddice;
using MacCheck.Core;
using TableForge.Dice;
using TableForge.Domain;

namespace MacCheck.App;

/// <summary>
/// The E4 section: dddice inside the app through Avalonia's NativeWebView (WKWebView on macOS), anonymous guest only.
/// Uses E4's port of TableForge 1.0.0's roller unchanged (validated bridge, navigation lock and escape guard, 1.0.0 roll
/// tracker) and TableForge 1.0.0's own DddiceDiceProvider, so TableForge — not dddice — computes every result.
/// The page can only send the 1.0.0 message kinds into the tracker; it cannot reach files, the clipboard or the results.
/// </summary>
public sealed class E4Runner : IDisposable
{
    private readonly CheckSession _session;
    private readonly Border _panel;
    private readonly DiagnosticLog _diag = new();
    private readonly DddiceRest _rest = new();
    private readonly AvaloniaWebViewDddiceRoller _roller;
    private readonly DddiceDiceProvider _provider;
    private readonly string _profile = Path.Combine(Platform.WorkFolder, "browser");

    public E4Runner(CheckSession session, Border panel)
    {
        _session = session;
        _panel = panel;
        _diag.LineAdded += line => session.Log.Write("e4 " + Private(SanitizeDiceLog(line[10..])));
        _roller = new AvaloniaWebViewDddiceRoller(v => { _panel.Child = v; return Task.CompletedTask; }, v => { if (ReferenceEquals(_panel.Child, v)) _panel.Child = null; }, Configure, _rest, _diag);
        _provider = new DddiceDiceProvider(_roller);
    }

    public event Action<string>? Progress;

    /// <summary>
    /// Removes the anonymous dddice room code from dice log lines (E4's roller logs "session: … (room XXXX, …)").
    /// Not personal data, but it identifies the session's room and is not needed in results.
    /// </summary>
    public static string SanitizeDiceLog(string line) =>
        System.Text.RegularExpressions.Regex.Replace(line, @"\broom [A-Za-z0-9_\-]+", "room <redacted>");

    /// <summary>Development self-test only: route the WebView2 browser through a host-logging proxy on this port.</summary>
    public static int? DevProxyPort { get; set; }

    /// <summary>Development self-test only (WebView2): start the browser with WebGL switched off, to exercise the graphics-failure path.</summary>
    public static bool DevDisableWebGl { get; set; }
    private SectionResult Section => _session.Document.E4;

    private void Configure(NativeWebView view) => view.EnvironmentRequested += (_, e) =>
    {
        e.EnableDevTools = false;
        switch (e)
        {
            case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                apple.NonPersistentDataStore = true; // nothing (cookies, storage, cache) is kept after the app closes
                break;
            case WindowsWebView2EnvironmentRequestedEventArgs w:
                w.UserDataFolder = Path.Combine(_profile, "webview2"); // development runs only; deleted with the work folder
                w.AdditionalBrowserArguments = "--disable-features=CalculateNativeWinOcclusion" + (DevDisableWebGl ? " --disable-3d-apis" : "")
                    + (DevProxyPort is { } port ? $" --proxy-server=http://127.0.0.1:{port}" : "");
                break;
            case LinuxWpeWebViewEnvironmentRequestedEventArgs wpe:
                wpe.DataDirectory = Path.Combine(_profile, "wpe");
                wpe.CacheDirectory = Path.Combine(_profile, "wpe-cache");
                break;
        }
    };

    private CheckItem Add(string id, string title, bool pass, string? detail, Answer answer = Answer.NotAsked)
    {
        var item = new CheckItem { Id = id, Title = title, AutomaticPass = pass, TesterAnswer = answer, Detail = detail, Outcome = Outcomes.Combine(pass, answer) };
        Section.Checks.RemoveAll(c => c.Id == id);
        Section.Checks.Add(item);
        _session.Log.Write($"check {id}: {Outcomes.Label(item.Outcome)} {detail}");
        _session.Save();
        return item;
    }

    private void Say(string text) { Progress?.Invoke(text); _session.Log.Write("e4: " + text); }

    /// <summary>The automatic part (about 1–2 minutes). Every step records its own result; nothing stops the whole run.</summary>
    public async Task RunAutomaticAsync()
    {
        Section.Checks.Clear();
        async Task Step(string name, Func<Task> body)
        {
            try { await body(); }
            catch (Exception ex) { Add(name, name, false, $"{ex.GetType().Name}: {_session.Redactor.Clean(ex.Message)}"); }
        }

        Say("Connecting to dddice (temporary anonymous guest)…");
        var ready = false;
        await Step("E4-ready", async () =>
        {
            var t = Stopwatch.StartNew();
            string Page() => "; page: " + Private(_roller.LastBridgeFacts?.ToString() ?? "no bridge check result");
            try { await _provider.PrepareAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                StartFailure = _session.Redactor.Clean(ex.Message);
                Add("E4-ready", "Dice view created inside the app and dddice ready", false,
                    $"{ex.GetType().Name}: {_session.Redactor.Clean(ex.Message)}; {AvaloniaWebViewDddiceRoller.Describe(_roller.AdapterInfo)}" + _session.Redactor.Clean(Page()));
                return;
            }
            ready = _roller.IsReady;
            Add("E4-ready", "Dice view created inside the app and dddice ready", ready,
                $"{AvaloniaWebViewDddiceRoller.Describe(_roller.AdapterInfo)}, ready in {t.Elapsed.TotalSeconds:0.0} s" + _session.Redactor.Clean(Page()));
        });
        if (!ready)
        {
            StartFailure ??= "dddice did not become ready.";
            // The page and its bridge do not depend on dddice's 3D engine: when the page loaded with a bridge, check both
            // directions and the graphics anyway, so a graphics failure is told apart from a bridge failure.
            MacCheck.Core.BridgeCheck.Facts? facts = null;
            try { facts = MacCheck.Core.BridgeCheck.Parse(await Eval(MacCheck.Core.BridgeCheck.ProbeScript)); } catch (Exception) { }
            var pageUp = facts is { Bridge: "function" };
            if (pageUp)
            {
                await Step("E4-bridge", BridgeChecks);
                await Step("E4-graphics", GraphicsCheck);
            }
            Section.Note = $"dddice did not become ready ({StartFailure}); the remaining dice checks could not run";
            foreach (var id in (pageUp ? [] : new[] { "E4-bridge", "E4-graphics" }).Concat(["E4-animation", "E4-rolls", "E4-integrity", "E4-navigation", "E4-reopen"]))
                Section.Checks.Add(new CheckItem { Id = id, Title = id, Outcome = Outcome.UnableToTest, Detail = "dddice not ready" });
            return;
        }
        await InstallProbe();

        await Step("E4-bridge", BridgeChecks);
        await Step("E4-graphics", GraphicsCheck);

        Say("Rolling 2d20 — watch the dice…");
        await Step("E4-animation", async () =>
        {
            var before = await Frames();
            var t = Stopwatch.StartNew();
            var roll = _provider.RollDetailedAsync(DiceExpression.Parse("2d20"), CancellationToken.None);
            foreach (var at in new[] { 600, 1100 }) { var wait = at - (int)t.ElapsedMilliseconds; if (wait > 0) await Task.Delay(wait); await Sample($"e4/dice-{at}ms.png"); }
            var r = await roll;
            var ms = t.ElapsedMilliseconds;
            await Task.Delay(300);
            await Sample("e4/dice-settled.png");
            var frames = await Frames() - before;
            Add("E4-animation", "The dice were drawn and animated (canvas frames during the roll)", frames >= 30 && ms >= 700,
                $"{frames} frames in {ms} ms; 2d20 = {r.Final} (faces {string.Join(" ", r.Faces.Select(f => f.Value))})");
        });

        Say("Rolling a few kinds of dice…");
        await Step("E4-rolls", async () =>
        {
            var lines = new List<string>();
            var ok = true;
            foreach (var (label, dice) in new (string, DiceExpression)[] { ("d20", DiceExpression.Parse("d20")), ("2d6+1", DiceExpression.Parse("2d6+1")), ("3d8-2", DiceExpression.Parse("3d8-2")), ("d100", DiceExpression.Parse("d100")), ("d66", DiceExpression.D66), ("d6", DiceExpression.Parse("d6")), ("d6", DiceExpression.Parse("d6")), ("d6", DiceExpression.Parse("d6")) })
            {
                var r = await _provider.RollDetailedAsync(dice, CancellationToken.None);
                var values = r.Faces.Select(f => f.Value).ToList();
                var independent = dice.IsD66 ? values[0] * 10 + values[1]
                    : dice.Sides == 100 ? Enumerable.Range(0, values.Count / 2).Sum(i => (values[2 * i] % 10) * 10 + values[2 * i + 1]) + dice.Modifier
                    : values.Sum() + dice.Modifier;
                var good = r.Faces.Select(f => f.Type).SequenceEqual(DddiceDiceMapping.ToDddice(dice.Base)) && dice.IsLegal(r.Final) && independent == r.Final;
                ok &= good;
                lines.Add($"{label}: {string.Join(",", values)} → {r.Final}{(good ? "" : " MISMATCH")}");
            }
            Add("E4-rolls", "Each result matches the dice faces (TableForge recomputes every roll)", ok, string.Join("; ", lines));
        });

        Say("Checking duplicate and late messages are ignored…");
        await Step("E4-integrity", async () =>
        {
            await _provider.RollDetailedAsync(DiceExpression.Parse("d8"), CancellationToken.None);
            var replay = _roller.LastFinishedJson!;
            var ignored = _roller.IgnoredMessages;
            await Inject(replay); await Inject(replay);
            var dupOk = _roller.IgnoredMessages - ignored == 2;
            var started = WaitFor("roll:started");
            var next = _provider.RollDetailedAsync(DiceExpression.Parse("d8"), CancellationToken.None);
            await started;
            ignored = _roller.IgnoredMessages;
            await Inject(replay);
            await next;
            var staleOk = _roller.IgnoredMessages - ignored >= 1;
            started = WaitFor("roll:started");
            var wrong = _provider.RollDetailedAsync(DiceExpression.Parse("2d6"), CancellationToken.None);
            await started;
            await Inject(JsonSerializer.Serialize(new { kind = "roll:finished", roll = new { uuid = "mac-check", externalId = _roller.CurrentExternalId, equation = "1d20", total = 20, values = new[] { new { type = "d20", value = 20 } } } }));
            var refused = false;
            try { await wrong; } catch (DddiceException) { refused = true; }
            await _provider.PrepareAsync(CancellationToken.None); // a refused roll means a fresh page, as in TableForge 1.0.0
            await InstallProbe();
            Add("E4-integrity", "Duplicate, stale and mismatched dice messages are never applied (simulated through the real bridge)", dupOk && staleOk && refused,
                $"duplicates ignored {dupOk}, stale ignored {staleOk}, wrong dice refused {refused}");
        });

        Say("Checking the dice page cannot leave its page…");
        await Step("E4-navigation", async () =>
        {
            var escapesBefore = _roller.EscapedNavigations;
            await Eval("setTimeout(() => { location.href = 'https://navigation-test.invalid/'; }, 0); 'ok'");
            await Task.Delay(1500);
            var where = await Eval("location.href") ?? "";
            var stayed = where.Contains("/dddice-host.html");
            var escaped = _roller.EscapedNavigations > escapesBefore;
            await Eval("String(window.open('https://navigation-test.invalid/') === null)");
            await Task.Delay(800);
            Add("E4-navigation", "The dice page cannot navigate away or open windows", stayed && !escaped,
                stayed ? "navigation cancelled" : escaped ? "ESCAPED (the engine ignored the cancel); guard stopped trusting the page" : $"page at {(where.Length > 40 ? where[..40] : where)}");
            if (!_roller.IsReady) { await _provider.PrepareAsync(CancellationToken.None); await InstallProbe(); }
        });

        Say("Closing and reopening the dice view…");
        await Step("E4-reopen", async () =>
        {
            var before = ProcessTree.Descendants(Environment.ProcessId);
            _roller.CloseBrowser();
            await Task.Delay(2500);
            var after = ProcessTree.Descendants(Environment.ProcessId);
            await _provider.PrepareAsync(CancellationToken.None);
            await InstallProbe();
            var r = await _provider.RollDetailedAsync(DiceExpression.Parse("2d6"), CancellationToken.None);
            Add("E4-reopen", "The dice view closes and reopens, and rolls again", _roller.IsReady && r.Faces.Count == 2,
                $"child processes {before.Count} → {after.Count} after closing ({string.Join(",", before.Select(p => p.Name).Distinct())}); then 2d6 = {r.Final}");
        });
        Say("Last roll — compare the dice with the result shown.");
        var last = await _provider.RollDetailedAsync(DiceExpression.Parse("3d6"), CancellationToken.None);
        LastResult = $"3d6 = {last.Final}  (dice: {string.Join(", ", last.Faces.Select(f => f.Value))})";
        Say("Done.");
    }

    public string? LastResult { get; private set; }

    /// <summary>Why the dice did not start (the tester sees it), or null when they did.</summary>
    public string? StartFailure { get; private set; }

    /// <summary>App → page (a script result) and page → app (a message through the real bridge); secure context.</summary>
    private async Task BridgeChecks()
    {
        var sum = await Eval("6 * 7");
        var env = await Eval("location.protocol + ' secure=' + window.isSecureContext");
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(string k, string j) { if (k == "warmSkipped" && j.Contains("mac-check-ping")) got.TrySetResult(); }
        _roller.PageMessage += H;
        await Eval("window.invokeCSharpAction({ kind: 'warmSkipped', message: 'mac-check-ping' })");
        var echoed = await Task.WhenAny(got.Task, Task.Delay(5000)) == got.Task;
        _roller.PageMessage -= H;
        Add("E4-bridge", "App ↔ page messages both ways; secure context", sum == "42" && echoed && env?.Contains("secure=true") == true,
            $"app→page {sum}; page→app {(echoed ? "received" : "missing")}; {env}");
    }

    /// <summary>
    /// WebGL2 as dddice's 3D engine needs it (a working context, not just an object), from the page's own probe, which is
    /// independent of whether dddice started. The renderer name shows whether a GPU or a software renderer is exposed.
    /// </summary>
    private async Task GraphicsCheck()
    {
        var gl = MacCheck.Core.BridgeCheck.Parse(await Eval(MacCheck.Core.BridgeCheck.ProbeScript))?.Gl;
        Add("E4-graphics", "3D graphics (WebGL2) work in the dice view", gl is { Usable: true }, Private(gl?.ToString() ?? "no answer from the page"));
    }

    /// <summary>The renderer (GPU) name only with the tester's consent to share system information.</summary>
    private string Private(string text) => _session.Document.Consent.SystemInfoAgreed ? text : MacCheck.Core.BridgeCheck.WithholdRenderer(text);

    /// <summary>
    /// The tester's two observations, recorded as their own checks. Only a person's "Yes" after dice were actually shown
    /// can pass them; with no roll shown, or no answer (automated run), they are UNABLE TO TEST.
    /// </summary>
    public void RecordAnswers(Answer animated, Answer facesMatch)
    {
        var shown = LastResult is not null;
        string? Why(Answer a) => !shown ? "no dice were shown (the dice section did not complete)" : a == Answer.NotAsked ? "needs a person watching the screen" : null;
        Put(new CheckItem { Id = "E4-seen-animation", Title = "Tester saw the dice tumble and land", AutomaticPass = shown, TesterAnswer = animated,
            Detail = Why(animated), Outcome = Outcomes.Observation(shown, animated) });
        Put(new CheckItem { Id = "E4-seen-faces", Title = "Tester: the dice faces match the result shown", AutomaticPass = shown, TesterAnswer = facesMatch,
            Detail = string.Join("; ", new[] { Why(facesMatch), LastResult }.Where(s => s is not null)), Outcome = Outcomes.Observation(shown, facesMatch) });
    }

    private void Put(CheckItem item)
    {
        Section.Checks.RemoveAll(c => c.Id == item.Id);
        Section.Checks.Add(item);
        _session.Log.Write($"check {item.Id}: {Outcomes.Label(item.Outcome)} {item.Detail}");
        _session.Save();
    }

    /// <summary>
    /// Closes the dice view (end of section) and waits up to 6 s for the browser engine's helper processes started by this
    /// app to exit. On macOS, WKWebView's helpers are system XPC services rather than children of the app, so the count
    /// is expected to be 0 there before and after; the names are recorded either way.
    /// </summary>
    public async Task ShutdownAsync()
    {
        try
        {
            var self = Path.GetFileName(Environment.ProcessPath ?? "");
            static bool Engine(ProcessTree.Proc p, string self) => !string.Equals(p.Name, self, StringComparison.OrdinalIgnoreCase);
            var before = ProcessTree.Descendants(Environment.ProcessId).Where(p => Engine(p, self)).ToList();
            _roller.CloseBrowser();
            var remaining = before;
            for (var i = 0; i < 24 && remaining.Count > 0; i++)
            {
                await Task.Delay(250);
                remaining = ProcessTree.Descendants(Environment.ProcessId).Where(p => Engine(p, self)).ToList();
            }
            var outcome = Outcomes.Shutdown(before.Count, remaining.Count);
            Put(new CheckItem { Id = "E4-shutdown", Title = "Dice view closed and its helper processes exited", AutomaticPass = outcome == Outcome.Pass, Outcome = outcome,
                Detail = $"engine helper processes {before.Count} → {remaining.Count} ({string.Join(",", before.Select(p => p.Name).Distinct())})"
                    + (before.Count == 0 ? "; no helper processes are visible to the app, so this cannot be confirmed here" : "") });
        }
        catch (Exception ex) { Add("E4-shutdown", "Dice view closed and its helper processes exited", false, ex.GetType().Name); }
    }

    // ---- helpers (as in E4's ScenarioRunner) ---------------------------------------------------------------------------

    private async Task<string?> Eval(string script)
    {
        if (_roller.View is null) return null;
        var raw = await _roller.View.InvokeScript(script);
        if (raw is { Length: > 1 } && raw[0] == '"') { try { return JsonSerializer.Deserialize<string>(raw); } catch (JsonException) { } }
        return raw;
    }

    private Task Inject(string json) => Eval($"window.invokeCSharpAction({json})");

    private Task WaitFor(string kind)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(string k, string json) { if (k == kind && _roller.CurrentExternalId is { } id && json.Contains(id)) { tcs.TrySetResult(); _roller.PageMessage -= H; } }
        _roller.PageMessage += H;
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async Task InstallProbe() => await Eval("""
        (() => { const r = (typeof dd !== 'undefined') && dd && dd.renderer; if (!r) return 'no renderer'; if (r.__mc) return 'already';
          const st = window.__mc = { frames: 0, want: false, png: null }; const orig = r.render.bind(r);
          r.render = (s, c) => { orig(s, c); if (r.getRenderTarget && r.getRenderTarget() !== null) return; st.frames++;
            if (st.want) { st.want = false; st.png = r.domElement.toDataURL('image/png'); } };
          r.__mc = true; return 'installed'; })()
        """);

    private async Task<int> Frames() => int.TryParse(await Eval("String(window.__mc ? window.__mc.frames : -1)"), out var n) ? n : -1;

    /// <summary>One rendered frame of the dice canvas (page pixels only: no screen capture, no permission needed).</summary>
    private async Task Sample(string name)
    {
        await Eval("window.__mc && (window.__mc.png = null, window.__mc.want = true); 'ok'");
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(50);
            var png = await Eval("window.__mc && window.__mc.png ? window.__mc.png : ''");
            if (png is { Length: > 30 } && png.StartsWith("data:image/png;base64,"))
            {
                var bytes = Convert.FromBase64String(png[(png.IndexOf(',') + 1)..]);
                if (bytes.Length < 3_000_000) _session.Images[name] = bytes;
                return;
            }
        }
    }

    public void Dispose()
    {
        try { _roller.Dispose(); } catch (Exception) { }
        _rest.Dispose();
    }
}
