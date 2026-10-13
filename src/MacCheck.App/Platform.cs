using System.Diagnostics;

namespace MacCheck.App;

/// <summary>Where things are, and the only two outside programs Mac Check ever starts: Preview (a supplied PDF) and Safari (optional E1).</summary>
public static class Platform
{
    /// <summary>In the .app bundle the corpus sits in Contents/Resources/corpus; in a development build next to the executable.</summary>
    public static string CorpusFolder
    {
        get
        {
            var bundle = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "corpus"));
            return Directory.Exists(bundle) ? bundle : Path.Combine(AppContext.BaseDirectory, "corpus");
        }
    }

    /// <summary>Private working folder for the crash-recovery journal; deleted after the results are saved.</summary>
    public static string WorkFolder
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "TableForgeMacCheck");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Opens one of the bundled PDFs in Preview (macOS), or the default viewer elsewhere (development only).</summary>
    public static string? OpenPdf(string path)
    {
        if (!File.Exists(path)) return "the PDF file is missing from the app";
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("/usr/bin/open", ["-a", "Preview", path]) { UseShellExecute = false })?.Dispose();
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Opens the optional E1 page in Safari (macOS) or the default browser elsewhere.</summary>
    public static string? OpenInSafari(Uri uri)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("/usr/bin/open", ["-a", "Safari", uri.ToString()]) { UseShellExecute = false })?.Dispose();
            else Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
