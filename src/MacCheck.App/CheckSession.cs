using System.Text.Json;
using MacCheck.Core;

namespace MacCheck.App;

/// <summary>
/// Everything recorded during one run: the results document, the captured test-PDF text, dice pictures and the log.
/// After every recorded result the document and captures are written to a journal in the private temp folder, so a crash
/// loses at most the current step; the journal is deleted once the results file has been saved.
/// </summary>
public sealed class CheckSession
{
    public CheckSession(Redactor redactor, SessionLog log)
    {
        Redactor = redactor;
        Log = log;
        Document = new ResultsDocument { Build = BuildIdentity.Current() };
    }

    public Redactor Redactor { get; }
    public SessionLog Log { get; }
    public ResultsDocument Document { get; private set; }
    public Dictionary<string, string> Captures { get; private set; } = [];
    public Dictionary<string, byte[]> Images { get; } = [];
    public int PageIndex { get; set; }

    private static string JournalPath => Path.Combine(Platform.WorkFolder, "journal.json");

    private sealed record Journal(ResultsDocument Document, Dictionary<string, string> Captures, int PageIndex);

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new Journal(Document, Captures, PageIndex), ResultsDocument.Json);
            File.WriteAllText(JournalPath, json);
        }
        catch (Exception ex) { Log.Write($"journal: not saved ({ex.GetType().Name})"); }
    }

    /// <summary>A journal left by a run that never reached "Save results" (the app closed or crashed).</summary>
    public static bool HasUnfinishedJournal => File.Exists(JournalPath);

    public bool Resume()
    {
        try
        {
            var j = JsonSerializer.Deserialize<Journal>(File.ReadAllText(JournalPath), ResultsDocument.Json);
            if (j is null) return false;
            Document = j.Document;
            Captures = j.Captures;
            PageIndex = j.PageIndex;
            Document.Events.Add($"{DateTime.UtcNow:u}: resumed after the app closed unexpectedly (at screen {PageIndex})");
            return true;
        }
        catch (Exception) { return false; }
    }

    public static void DeleteJournal()
    {
        try { if (Directory.Exists(Platform.WorkFolder)) Directory.Delete(Platform.WorkFolder, recursive: true); } catch (Exception) { }
    }

    public void Event(string text)
    {
        Document.Events.Add($"{DateTime.UtcNow:u}: {Redactor.Clean(text)}");
        Log.Write("event: " + text);
        Save();
    }

    public void WritePackage(Stream output)
    {
        Document.Finish();
        ResultsPackage.Write(output, Document, Captures, Images, Log.Lines, Redactor);
    }
}
