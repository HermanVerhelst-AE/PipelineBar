using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PipelineBar;

[JsonConverter(typeof(JsonStringEnumConverter<RunState>))]
public enum RunState
{
    Succeeded, Failed, PartiallySucceeded, Canceled,
    InProgress, NotStarted, Postponed, Waiting, Cancelling, None,
}

public static class RunStateInfo
{
    /// Leest een status of resultaat uit de API ("inProgress", "partiallySucceeded"…).
    public static RunState Parse(string? value) =>
        Enum.TryParse<RunState>(value, ignoreCase: true, out var s) ? s : RunState.None;

    public static bool IsRunning(this RunState s) => s is RunState.InProgress or RunState.NotStarted
        or RunState.Postponed or RunState.Waiting or RunState.Cancelling;

    /// Kleurt of telt mee in het systeemvakicoon, en kan dus genegeerd worden.
    public static bool NeedsAttention(this RunState s) =>
        s.IsRunning() || s is RunState.Failed or RunState.PartiallySucceeded;

    /// Segoe Fluent Icons / Segoe MDL2 Assets per status.
    public static string Glyph(this RunState s) => s switch
    {
        RunState.Succeeded => "",            // CompletedSolid
        RunState.Failed => "",               // StatusErrorFull
        RunState.PartiallySucceeded => "",   // Warning
        RunState.Canceled or RunState.Cancelling => "",   // Blocked
        RunState.InProgress => "",           // Sync
        RunState.NotStarted or RunState.Postponed => "",  // Recent
        RunState.Waiting => "",              // Shield
        _ => "",                             // StatusCircleRing
    };

    public static string Label(this RunState s) => s switch
    {
        RunState.Succeeded => "Geslaagd",
        RunState.Failed => "Mislukt",
        RunState.PartiallySucceeded => "Deels geslaagd",
        RunState.Canceled => "Geannuleerd",
        RunState.InProgress => "Bezig",
        RunState.NotStarted => "In wachtrij",
        RunState.Postponed => "Uitgesteld",
        RunState.Waiting => "Wacht op goedkeuring",
        RunState.Cancelling => "Wordt geannuleerd",
        _ => "Onbekend",
    };

    /// Lager = hoger in de lijst.
    public static int Priority(this RunState s) => s switch
    {
        RunState.Failed => 0,
        RunState.PartiallySucceeded => 1,
        RunState.Waiting => 2,
        RunState.InProgress or RunState.Cancelling => 3,
        RunState.NotStarted or RunState.Postponed => 4,
        RunState.Canceled => 5,
        RunState.Succeeded => 6,
        _ => 7,
    };
}

public enum RunKind { Build, Release }

public sealed record PipelineRun(
    string Key,          // stabiel per pipeline en branch (of release-omgeving)
    int RunId,
    string Project,
    string Name,
    RunKind Kind,
    RunState State,
    string Number,
    string Branch,
    string RequestedBy,
    DateTimeOffset? Date,
    string? Url)
{
    public string Detail
    {
        get
        {
            var text = string.Join(" op ", new[] { Number, Branch }.Where(p => p.Length > 0));
            if (RequestedBy.Length > 0) text += text.Length == 0 ? RequestedBy : $", door {RequestedBy}";
            return text;
        }
    }
}

public sealed record ProjectGroup(string Project, IReadOnlyList<PipelineRun> Runs);

/// Laatst geziene toestand per pipeline, om wijzigingen te detecteren.
public sealed record Snapshot(int RunId, RunState State, RunState? LastResult);

/// Een genegeerde run; vervalt zodra de pipeline een andere run of status heeft.
public sealed record Dismissal(int RunId, RunState State)
{
    public bool Matches(PipelineRun run) => run.RunId == RunId && run.State == State;
}

/// Voorkeuren in %APPDATA%\PipelineBar\settings.json.
public sealed class Settings
{
    public string Organization { get; set; } = "";
    public string Projects { get; set; } = "";
    public string PipelineFilter { get; set; } = "";
    public bool IncludeReleases { get; set; } = true;
    public bool NotifyOnStart { get; set; } = true;
    public bool NotifyOnSuccess { get; set; } = true;
    public int RefreshMinutes { get; set; } = 1;
    public int MaxAgeDays { get; set; }          // 0 = enkel vandaag
    public bool OnlyMine { get; set; } = true;

    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PipelineBar");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Current { get; } = Load();

    private static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public IReadOnlyList<string> ProjectList => Projects
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public static class Format
{
    public static string ShortRelative(DateTimeOffset date, DateTimeOffset now)
    {
        var secs = (long)(now - date).TotalSeconds;
        if (secs < 60) return "nu";
        if (secs < 3600) return $"{secs / 60} min";
        if (secs < 86_400) return $"{secs / 3600} u";
        return $"{secs / 86_400} d";
    }
}
