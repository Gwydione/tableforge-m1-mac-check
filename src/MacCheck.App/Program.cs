using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using MacCheck.Core;

namespace MacCheck.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);
}

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var redactor = new Redactor();
            var log = new SessionLog(redactor);
            var session = new CheckSession(redactor, log);
            log.Write($"start: {session.Document.Build.App} {session.Document.Build.Version} ({session.Document.Build.BuildId}, {session.Document.Build.Runtime})");

            var selfTest = Array.IndexOf(args, "--selftest");
            if (selfTest >= 0)
            {
                // Development and CI only: runs every section without a person, then writes the results package.
                var outDir = selfTest + 1 < args.Length ? args[selfTest + 1] : Path.Combine(AppContext.BaseDirectory, "selftest");
                var window = new MainWindow(session);
                desktop.MainWindow = window;
                window.Opened += async (_, _) => { Environment.ExitCode = await new SelfTest(window, session).RunAsync(outDir, args.Contains("--no-dice")); window.Close(); };
            }
            else
            {
                if (CheckSession.HasUnfinishedJournal && session.Resume()) log.Write("start: resumed an unfinished run");
                desktop.MainWindow = new MainWindow(session);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
