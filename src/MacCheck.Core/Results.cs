using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacCheck.Core;

public sealed class CheckItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public Outcome Outcome { get; set; } = Outcome.UnableToTest;
    public bool AutomaticPass { get; set; }
    public Answer TesterAnswer { get; set; } = Answer.NotAsked;
    public string? Detail { get; set; }
    public double? Seconds { get; set; }
}

public sealed class SectionResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public bool Optional { get; init; }
    public Outcome Outcome { get; set; } = Outcome.UnableToTest;
    public string? Note { get; set; }
    public List<CheckItem> Checks { get; set; } = [];
    public void Recompute() => Outcome = Outcomes.Worst(Checks.Select(c => c.Outcome));
}

/// <summary>The one file the tester returns (as results.json inside the results zip). Format "TableForgeMacCheck" v1.</summary>
public sealed class ResultsDocument
{
    public string Format { get; init; } = "TableForgeMacCheck";
    public int FormatVersion { get; init; } = 1;
    public required BuildIdentity Build { get; init; }
    public string StartedUtc { get; set; } = DateTime.UtcNow.ToString("u");
    public string? FinishedUtc { get; set; }
    public ConsentRecord Consent { get; set; } = new();
    public Dictionary<string, string>? System { get; set; }
    public Outcome Overall { get; set; } = Outcome.UnableToTest;
    public string? OverallNote { get; set; }
    public List<PdfCaseResult> E2 { get; set; } = [];
    public SectionResult E2Summary { get; set; } = new() { Id = "E2", Title = "PDF copy from Preview" };
    public SectionResult E4 { get; set; } = new() { Id = "E4", Title = "dddice dice in the app" };
    public SectionResult E3 { get; set; } = new() { Id = "E3", Title = "Editing result text" };
    public SectionResult E1 { get; set; } = new() { Id = "E1", Title = "dddice dice in Safari (optional)", Optional = true };
    public List<string> Events { get; set; } = [];
    public Dictionary<string, string> Questions { get; set; } = [];

    public void Finish()
    {
        FinishedUtc = DateTime.UtcNow.ToString("u");
        E2Summary.Checks = E2.Select(r => new CheckItem
        {
            Id = r.Case, Title = (r.Mandatory ? "" : "(optional) ") + r.Case, Outcome = r.Outcome, AutomaticPass = r.Outcome == Outcome.Pass,
            Detail = r.ContentRejected ? "pasted text was not the test PDF; not stored" : r.RawVerdict,
        }).ToList();
        // Mandatory PDFs decide E2; optional ones are evidence but cannot pull the section down from not-tested.
        var mandatory = E2.Where(r => r.Mandatory).Select(r => r.Outcome).ToList();
        E2Summary.Outcome = Outcomes.Worst(mandatory);
        E4.Recompute();
        E3.Recompute();
        if (E1.Checks.Count > 0) E1.Recompute();
        Overall = Outcomes.Worst([E2Summary.Outcome, E4.Outcome, E3.Outcome]);
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class ConsentRecord
{
    public bool TestsAgreed { get; set; }
    public bool SystemInfoAgreed { get; set; }
    public bool ModelAgreed { get; set; }
    public bool SaveAgreed { get; set; }
}

/// <summary>Which build produced the results: version, build id and the TableForge 1.0.0 source it embeds.</summary>
public sealed record BuildIdentity(string App, string Version, string BuildId, string TableForgeSource, string Runtime)
{
    public static BuildIdentity Current()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(BuildIdentity).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var meta = typeof(BuildIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        return new BuildIdentity("TableForge Mac Check", info.Split('+')[0], meta.GetValueOrDefault("BuildId") ?? "dev",
            "TableForge v1.0.0 (6415232) — see PROVENANCE.md", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
    }
}

/// <summary>Writes the return package: results.json, results.md, captured test text, dice images and the redacted log.</summary>
public static class ResultsPackage
{
    public static void Write(Stream output, ResultsDocument doc, IReadOnlyDictionary<string, string> captures,
        IReadOnlyDictionary<string, byte[]> images, IEnumerable<string> logLines, Redactor redactor)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Text(string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Text("results.json", redactor.Clean(JsonSerializer.Serialize(doc, ResultsDocument.Json)));
        Text("results.md", redactor.Clean(Summary(doc)));
        foreach (var (name, content) in captures) Text(name, content); // test-PDF text only (ClipboardGuard)
        foreach (var (name, bytes) in images)
        {
            using var s = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
            s.Write(bytes);
        }
        Text("log.txt", string.Join('\n', logLines.Select(redactor.Clean)) + "\n");
        Text("README.txt", "TableForge Mac Check results. Contains only test results, the text copied from the supplied test PDFs, " +
                           "pictures of the test dice, and a log without names or file locations. Nothing else from this Mac.\n");
    }

    public static string Summary(ResultsDocument d)
    {
        var sb = new StringBuilder();
        sb.Append($"# TableForge Mac Check — results\n\nBuild {d.Build.Version} ({d.Build.BuildId}, {d.Build.Runtime}). Started {d.StartedUtc}, finished {d.FinishedUtc ?? "—"}.\n\n");
        sb.Append($"**Overall: {Outcomes.Label(d.Overall)}**{(d.OverallNote is { } n ? $" — {n}" : "")}\n\n");
        if (d.System is { } sys) sb.Append("System: ").Append(string.Join(", ", sys.Select(kv => $"{kv.Key} {kv.Value}"))).Append("\n\n");
        sb.Append($"## E2 PDF copy from Preview: {Outcomes.Label(d.E2Summary.Outcome)}\n\n");
        sb.Append("Result = the raw parse of exactly what was pasted. \"Same as (E2)\" lists the readers tested in E2 that gave the same verdict for that PDF: a FAIL shared with the browser engines is a known parser limitation, not a Mac problem (see docs/EXPECTED-RESULTS.md).\n\n");
        sb.Append("| PDF | Required | Result | Raw parse | Rows | Same as (E2) |\n|---|---|---|---|---|---|\n");
        foreach (var r in d.E2)
            sb.Append($"| {r.Case} | {(r.Mandatory ? "yes" : "optional")} | {Outcomes.Label(r.Outcome)} | {r.RawVerdict ?? (r.ContentRejected ? "pasted text not the test PDF (not stored)" : "—")} | {r.ParsedRows?.ToString() ?? "—"}/{r.ExpectedRows?.ToString() ?? "—"} | {(r.RawVerdict is null ? "" : r.SameVerdictAsE2Readers.Count == 0 ? "none — new behaviour" : string.Join(", ", r.SameVerdictAsE2Readers))} |\n");
        foreach (var s in new[] { d.E4, d.E3, d.E1 })
        {
            if (s.Optional && s.Checks.Count == 0) continue;
            sb.Append($"\n## {s.Id} {s.Title}: {Outcomes.Label(s.Outcome)}{(s.Note is { } sn ? $" — {sn}" : "")}\n\n| Check | Result | Tester | Detail |\n|---|---|---|---|\n");
            foreach (var c in s.Checks)
                sb.Append($"| {c.Title} | {Outcomes.Label(c.Outcome)} | {(c.TesterAnswer == Answer.NotAsked ? "" : c.TesterAnswer.ToString())} | {c.Detail?.Replace("|", "/").Replace("\n", " ")} |\n");
        }
        if (d.Questions.Count > 0)
        {
            sb.Append("\n## Tester answers\n\n");
            foreach (var (q, a) in d.Questions) sb.Append($"- {q}: {a}\n");
        }
        if (d.Events.Count > 0)
        {
            sb.Append("\n## Events\n\n");
            foreach (var e in d.Events) sb.Append($"- {e}\n");
        }
        return sb.ToString();
    }
}
