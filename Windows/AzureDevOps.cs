using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace PipelineBar;

public enum AdoErrorKind { Unauthorized, NotFound, Http }

public sealed class AdoException(AdoErrorKind kind, int code = 0) : Exception(kind switch
{
    AdoErrorKind.Unauthorized => "Token ongeldig, verlopen of zonder leesrechten.",
    AdoErrorKind.NotFound => "Niet gevonden. Controleer de naam van organisatie of project.",
    _ => $"Azure DevOps gaf foutcode {code}.",
})
{
    public AdoErrorKind Kind { get; } = kind;
}

// API-antwoorden

internal sealed class AdoList<T> { public List<T> Value { get; set; } = []; }
internal sealed class AdoProject { public string Name { get; set; } = ""; }
internal sealed class AdoLink { public string? Href { get; set; } }
internal sealed class AdoLinks { public AdoLink? Web { get; set; } }
internal sealed class AdoIdentity { public string? Id { get; set; } public string? DisplayName { get; set; } }
internal sealed class AdoConnectionData { public AdoIdentity? AuthenticatedUser { get; set; } }
internal sealed class AdoRef { public int? Id { get; set; } public string? Name { get; set; } }

internal sealed class AdoBuild
{
    public int Id { get; set; }
    public string? Status { get; set; }
    public string? Result { get; set; }
    public AdoRef? Definition { get; set; }
    public string? BuildNumber { get; set; }
    public string? SourceBranch { get; set; }
    /// Bij een trigger vanuit een andere repo staat hier de branch die de run startte.
    public Dictionary<string, JsonElement>? TriggerInfo { get; set; }
    public AdoIdentity? RequestedFor { get; set; }
    public DateTimeOffset? QueueTime { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? FinishTime { get; set; }
    [JsonPropertyName("_links")] public AdoLinks? Links { get; set; }
}

internal sealed class AdoRelease
{
    public string? Name { get; set; }
    [JsonPropertyName("_links")] public AdoLinks? Links { get; set; }
}

internal sealed class AdoDeployment
{
    public int Id { get; set; }
    public string? DeploymentStatus { get; set; }
    public string? OperationStatus { get; set; }
    public int? DefinitionEnvironmentId { get; set; }
    public AdoRef? ReleaseDefinition { get; set; }
    public AdoRef? ReleaseEnvironment { get; set; }
    public AdoRelease? Release { get; set; }
    public AdoIdentity? RequestedFor { get; set; }
    public DateTimeOffset? QueuedOn { get; set; }
    public DateTimeOffset? StartedOn { get; set; }
    public DateTimeOffset? CompletedOn { get; set; }
}

/// Azure geeft tot 7 decimalen en soms 0001-01-01 voor "nog niet"; dat laatste wordt null.
internal sealed class LenientDateConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) return null;
        return DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture,
                                       DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1900 ? d : null;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}

// Client

public sealed class AzureDevOpsClient(string organization, string token, int maxAgeDays)
{
    /// Identiteits-id; indien gezet enkel runs die deze gebruiker startte.
    public string? RequestedFor { get; set; }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new LenientDateConverter() },
    };

    private string Since
    {
        get
        {
            var start = maxAgeDays == 0
                ? new DateTimeOffset(DateTime.Today)
                : DateTimeOffset.Now.AddDays(-maxAgeDays);
            return start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);

    private async Task<T> Get<T>(string baseUrl, string path, Dictionary<string, string> query,
                                 string apiVersion = "7.1")
    {
        var qs = string.Join("&", query.Append(new("api-version", apiVersion))
            .Select(kv => $"{Esc(kv.Key)}={Esc(kv.Value)}"));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}{path}?{qs}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var res = await Http.SendAsync(req);
        switch (res.StatusCode)
        {
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                throw new AdoException(AdoErrorKind.Unauthorized);
            case HttpStatusCode.NotFound:
                throw new AdoException(AdoErrorKind.NotFound);
            case var code when !res.IsSuccessStatusCode:
                throw new AdoException(AdoErrorKind.Http, (int)code);
        }
        // Een ongeldig token geeft soms 203 met een HTML-aanmeldpagina.
        if (!(res.Content.Headers.ContentType?.MediaType ?? "").Contains("json"))
            throw new AdoException(AdoErrorKind.Unauthorized);

        await using var stream = await res.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<T>(stream, Json)
            ?? throw new AdoException(AdoErrorKind.Http, (int)res.StatusCode);
    }

    private string DevBase => $"https://dev.azure.com/{Esc(organization)}";
    private string VsrmBase => $"https://vsrm.dev.azure.com/{Esc(organization)}";

    public async Task<List<string>> Projects()
    {
        var list = await Get<AdoList<AdoProject>>(DevBase, "/_apis/projects", new() { ["$top"] = "500" });
        return list.Value.Select(p => p.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// Id van de gebruiker achter het token.
    public async Task<string> CurrentUserId()
    {
        var data = await Get<AdoConnectionData>(DevBase, "/_apis/connectionData", [], "7.1-preview");
        return data.AuthenticatedUser?.Id ?? throw new AdoException(AdoErrorKind.Unauthorized);
    }

    private bool IsMine(AdoIdentity? who) =>
        RequestedFor is null || string.Equals(who?.Id, RequestedFor, StringComparison.OrdinalIgnoreCase);

    public async Task<List<PipelineRun>> Builds(string project)
    {
        var query = new Dictionary<string, string>
        {
            ["queryOrder"] = "queueTimeDescending",
            ["$top"] = "1000",
            ["minTime"] = Since,
        };
        if (RequestedFor is not null) query["requestedFor"] = RequestedFor;
        var list = await Get<AdoList<AdoBuild>>(DevBase, $"/{Esc(project)}/_apis/build/builds", query);

        var seen = new HashSet<string>();
        var runs = new List<PipelineRun>();
        foreach (var b in list.Value.Where(b => IsMine(b.RequestedFor)))
        {
            var trigger = b.TriggerInfo?.GetValueOrDefault("ci.sourceBranch");
            var branchRef = trigger?.ValueKind == JsonValueKind.String ? trigger.Value.GetString() : b.SourceBranch;
            var key = $"build:{project}:{b.Definition?.Id}:{branchRef}";
            if (!seen.Add(key)) continue;   // nieuwste eerst

            var completed = b.Status == "completed";
            runs.Add(new PipelineRun(
                Key: key,
                RunId: b.Id,
                Project: project,
                Name: b.Definition?.Name ?? "?",
                Kind: RunKind.Build,
                State: RunStateInfo.Parse(completed ? b.Result : b.Status),
                Number: b.BuildNumber ?? "",
                Branch: ShortBranch(branchRef),
                RequestedBy: b.RequestedFor?.DisplayName ?? "",
                Date: completed ? b.FinishTime : b.StartTime ?? b.QueueTime,
                Url: b.Links?.Web?.Href));
        }
        return runs;
    }

    public async Task<List<PipelineRun>> Deployments(string project)
    {
        var list = await Get<AdoList<AdoDeployment>>(VsrmBase, $"/{Esc(project)}/_apis/release/deployments", new()
        {
            ["queryOrder"] = "descending",
            ["$top"] = "200",
            ["minStartedTime"] = Since,
        });

        var seen = new HashSet<string>();
        var runs = new List<PipelineRun>();
        foreach (var d in list.Value.Where(d => IsMine(d.RequestedFor)))
        {
            var envKey = d.DefinitionEnvironmentId?.ToString() ?? d.ReleaseEnvironment?.Name ?? "?";
            var key = $"release:{project}:{d.ReleaseDefinition?.Id ?? 0}:{envKey}";
            if (!seen.Add(key)) continue;   // nieuwste eerst

            var state = (d.DeploymentStatus ?? "", d.OperationStatus ?? "") switch
            {
                ("inProgress", "Pending") => RunState.Waiting,
                ("inProgress", "Queued" or "QueuedForAgent" or "Scheduled" or "Deferred") => RunState.NotStarted,
                ("inProgress", _) => RunState.InProgress,
                ("succeeded", _) => RunState.Succeeded,
                ("failed", _) => RunState.Failed,
                ("partiallySucceeded", _) => RunState.PartiallySucceeded,
                ("notDeployed", _) => RunState.Canceled,
                _ => RunState.None,
            };

            runs.Add(new PipelineRun(
                Key: key,
                RunId: d.Id,
                Project: project,
                Name: $"{d.ReleaseDefinition?.Name ?? "?"} → {d.ReleaseEnvironment?.Name ?? "?"}",
                Kind: RunKind.Release,
                State: state,
                Number: d.Release?.Name ?? "",
                Branch: "",
                RequestedBy: d.RequestedFor?.DisplayName ?? "",
                Date: d.CompletedOn ?? d.StartedOn ?? d.QueuedOn,
                Url: d.Release?.Links?.Web?.Href));
        }
        return runs;
    }

    public static string ShortBranch(string? r)
    {
        if (string.IsNullOrEmpty(r)) return "";
        if (r.StartsWith("refs/pull/")) return "PR " + r["refs/pull/".Length..].Split('/')[0];
        foreach (var prefix in new[] { "refs/heads/", "refs/tags/" })
            if (r.StartsWith(prefix)) return r[prefix.Length..];
        return r;
    }
}
