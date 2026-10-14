using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PipelineBar;

/// Ververst periodiek op de UI-thread; elke await keert daar terug.
public sealed class PipelineStore
{
    public IReadOnlyList<PipelineRun> Runs { get; private set; } = [];
    public IReadOnlyList<string> Errors { get; private set; } = [];
    public string? Problem { get; private set; }
    public DateTimeOffset? LastUpdated { get; private set; }
    public bool IsRefreshing { get; private set; }

    /// Na elke wijziging van de toestand hierboven.
    public event Action? Changed;

    private readonly Notifier _notifier;
    private CancellationTokenSource? _loop;
    private (string Organization, string Id)? _me;
    private bool _refreshAgain;
    private static readonly string SnapshotPath = Path.Combine(Settings.Folder, "snapshot.json");
    private static readonly string DismissedPath = Path.Combine(Settings.Folder, "dismissed.json");
    private Dictionary<string, Dismissal> _dismissed = LoadDismissed();

    public PipelineStore(Notifier notifier) => _notifier = notifier;

    private static Settings Prefs => Settings.Current;

    // Afgeleide waarden

    /// Runs die niet genegeerd zijn; enkel die bepalen het icoon en de tellers.
    public IReadOnlyList<PipelineRun> ActiveRuns => Runs.Where(r => !IsDismissed(r)).ToList();

    public int FailedCount => ActiveRuns.Count(r => r.State == RunState.Failed);
    public int RunningCount => ActiveRuns.Count(r => r.State.IsRunning());
    public int DismissibleCount => ActiveRuns.Count(r => r.State.NeedsAttention());

    /// Samengevatte status voor het systeemvakicoon; null = niet geconfigureerd of fout.
    public RunState? OverallState
    {
        get
        {
            if (Problem is not null) return null;
            if (FailedCount > 0) return RunState.Failed;
            if (ActiveRuns.Any(r => r.State == RunState.Waiting)) return RunState.Waiting;
            if (RunningCount > 0) return RunState.InProgress;
            if (ActiveRuns.Any(r => r.State == RunState.PartiallySucceeded)) return RunState.PartiallySucceeded;
            return Runs.Count == 0 ? RunState.None : RunState.Succeeded;
        }
    }

    public int? BadgeCount => FailedCount > 0 ? FailedCount : RunningCount > 0 ? RunningCount : null;

    public string Summary
    {
        get
        {
            if (Problem is not null) return "Niet verbonden";
            if (FailedCount == 1) return "1 pipeline mislukt";
            if (FailedCount > 1) return $"{FailedCount} pipelines mislukt";
            if (RunningCount == 1) return "1 pipeline bezig";
            if (RunningCount > 1) return $"{RunningCount} pipelines bezig";
            if (Runs.Count == 0) return "Geen recente runs";
            if (Runs.Any(IsDismissed)) return "Niets nieuws";
            return "Alles geslaagd";
        }
    }

    public IReadOnlyList<ProjectGroup> Groups => Runs
        .GroupBy(r => r.Project)
        .Select(g => new ProjectGroup(g.Key, g
            .OrderBy(IsDismissed)   // genegeerde runs onderaan
            .ThenBy(r => r.State.Priority())
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(r => r.Date ?? DateTimeOffset.MinValue)   // nieuwste branch eerst
            .ToList()))
        .OrderBy(g => g.Project, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    // Negeren

    public bool IsDismissed(PipelineRun run) => _dismissed.GetValueOrDefault(run.Key)?.Matches(run) == true;

    public void Dismiss(PipelineRun run)
    {
        _dismissed[run.Key] = new Dismissal(run.RunId, run.State);
        SaveDismissed();
    }

    public void Undismiss(PipelineRun run)
    {
        _dismissed.Remove(run.Key);
        SaveDismissed();
    }

    public void DismissAll()
    {
        foreach (var run in ActiveRuns.Where(r => r.State.NeedsAttention()))
            _dismissed[run.Key] = new Dismissal(run.RunId, run.State);
        SaveDismissed();
    }

    /// Vergeet negeringen van pipelines die intussen een andere run of status hebben.
    private void PruneDismissed(List<PipelineRun> current)
    {
        var byKey = current.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
        var stale = _dismissed.Where(d => byKey.TryGetValue(d.Key, out var run) && !d.Value.Matches(run))
            .Select(d => d.Key).ToList();
        if (stale.Count == 0) return;
        foreach (var key in stale) _dismissed.Remove(key);
        SaveDismissed(notify: false);
    }

    private static Dictionary<string, Dismissal> LoadDismissed()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Dismissal>>(File.ReadAllText(DismissedPath)) ?? []; }
        catch { return []; }
    }

    private void SaveDismissed(bool notify = true)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(DismissedPath, JsonSerializer.Serialize(_dismissed));
        }
        catch { }
        if (notify) Changed?.Invoke();
    }

    // Verversen

    public void Restart()
    {
        _loop?.Cancel();
        _me = null;   // token kan gewijzigd zijn
        _loop = new CancellationTokenSource();
        _ = Loop(_loop.Token);
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Refresh();
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, Prefs.RefreshMinutes)), ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    public async Task Refresh()
    {
        if (IsRefreshing) { _refreshAgain = true; return; }   // instellingen kunnen net gewijzigd zijn
        var org = Prefs.Organization.Trim();
        if (org.Length == 0)
        {
            SetProblem("Stel je Azure DevOps-organisatie in.");
            return;
        }
        var token = CredentialStore.Read(org);
        if (string.IsNullOrEmpty(token))
        {
            SetProblem($"Voeg een Personal Access Token toe voor {org}.");
            return;
        }

        IsRefreshing = true;
        Changed?.Invoke();
        try
        {
            var client = new AzureDevOpsClient(org, token, Math.Max(0, Prefs.MaxAgeDays));
            var includeReleases = Prefs.IncludeReleases;

            if (Prefs.OnlyMine)
            {
                if (_me?.Organization != org) _me = (org, await client.CurrentUserId());
                client.RequestedFor = _me?.Id;
            }
            var projects = Prefs.ProjectList.ToList();
            if (projects.Count == 0) projects = await client.Projects();

            var results = await Task.WhenAll(projects.Select(async project =>
            {
                try
                {
                    var result = await client.Builds(project);
                    if (includeReleases)
                    {
                        // Geen klassieke releases of geen Release-rechten: builds tonen we toch.
                        try { result.AddRange(await client.Deployments(project)); } catch { }
                    }
                    return (project, runs: result, error: (Exception?)null);
                }
                catch (AdoException e) when (e.Kind == AdoErrorKind.Unauthorized) { throw; }
                catch (Exception e) { return (project, runs: new List<PipelineRun>(), error: e); }
            }));

            var collected = ApplyFilter(results.SelectMany(r => r.runs).ToList());
            DetectChanges(collected);
            PruneDismissed(collected);
            Runs = collected;
            Errors = results.Where(r => r.error is not null)
                .Select(r => $"{r.project}: {Describe(r.error!)}").Order().ToList();
            Problem = null;
            LastUpdated = DateTimeOffset.Now;
        }
        catch (AdoException e) when (e.Kind == AdoErrorKind.Unauthorized)
        {
            Problem = e.Message;
        }
        catch (Exception e)
        {
            Problem = $"Azure DevOps is niet bereikbaar: {Describe(e)}";
        }
        finally
        {
            IsRefreshing = false;
            Changed?.Invoke();
        }

        if (_refreshAgain)
        {
            _refreshAgain = false;
            await Refresh();
        }
    }

    private void SetProblem(string text)
    {
        Problem = text;
        Runs = [];
        Changed?.Invoke();
    }

    private static string Describe(Exception e) => e switch
    {
        TaskCanceledException => "time-out",
        AdoException => e.Message,
        _ => e.GetBaseException().Message,
    };

    private static List<PipelineRun> ApplyFilter(List<PipelineRun> runs)
    {
        var pattern = Prefs.PipelineFilter;
        if (string.IsNullOrEmpty(pattern)) return runs;
        try
        {
            var rx = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            return runs.Where(r => rx.IsMatch(r.Name)).ToList();
        }
        catch (ArgumentException) { return runs; }   // ongeldige expressie: niet filteren
    }

    // Wijzigingen en meldingen

    private void DetectChanges(List<PipelineRun> current)
    {
        Dictionary<string, Snapshot>? stored = null;
        try { stored = JsonSerializer.Deserialize<Dictionary<string, Snapshot>>(File.ReadAllText(SnapshotPath)); }
        catch { }
        var seeded = stored is not null;          // eerste run: enkel toestand vastleggen
        var snapshot = stored ?? [];
        var events = new List<Notifier.Event>();

        foreach (var run in current)
        {
            var prev = snapshot.GetValueOrDefault(run.Key);
            if (seeded && EventFor(run, prev) is { } e) events.Add(e);
            var last = run.State.IsRunning() ? prev?.LastResult : run.State;
            snapshot[run.Key] = new Snapshot(run.RunId, run.State, last);
        }

        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(snapshot));
        }
        catch { }
        _notifier.Post(events);
    }

    private static Notifier.Event? EventFor(PipelineRun run, Snapshot? prev)
    {
        if (prev is null) return null;   // nieuwe pipeline: geen melding
        var sameRun = prev.RunId == run.RunId;
        if (sameRun && prev.State == run.State) return null;

        string title;
        if (run.State.IsRunning())
        {
            if (run.State == RunState.Waiting) title = "Wacht op goedkeuring";
            else if (!sameRun && Prefs.NotifyOnStart) title = "Gestart";
            else return null;
        }
        else
        {
            switch (run.State)
            {
                case RunState.Succeeded:
                    if (prev.LastResult is RunState.Failed or RunState.PartiallySucceeded) title = "Hersteld";
                    else if (Prefs.NotifyOnSuccess) title = "Geslaagd";
                    else return null;
                    break;
                case RunState.Failed: title = "Mislukt"; break;
                case RunState.PartiallySucceeded: title = "Deels geslaagd"; break;
                case RunState.Canceled: title = "Geannuleerd"; break;
                default: return null;
            }
        }

        return new Notifier.Event(
            Title: $"{title}: {run.Name}",
            Subtitle: run.Project,
            Body: run.Detail.Length == 0 ? run.State.Label() : run.Detail,
            Url: run.Url,
            State: run.State);
    }
}
