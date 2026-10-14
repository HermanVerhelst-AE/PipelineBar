using System;
using System.Collections.Generic;
using System.Linq;
using Forms = System.Windows.Forms;

namespace PipelineBar;

/// Windows-meldingen via het systeemvakicoon. Windows 10 en 11 tonen die als gewone toast
/// in het Meldingencentrum. Er is telkens één melding, dus meerdere wijzigingen worden gebundeld.
public sealed class Notifier
{
    public sealed record Event(string Title, string Subtitle, string Body, string? Url, RunState State);

    private const int MaxLines = 5;
    private readonly Forms.NotifyIcon _icon;
    private string? _url;

    /// Klik op een melding zonder eigen link.
    public event Action? ClickedWithoutUrl;

    public Notifier(Forms.NotifyIcon icon)
    {
        _icon = icon;
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_url is not null) Shell.Open(_url);
            else ClickedWithoutUrl?.Invoke();
        };
    }

    public void Post(IReadOnlyList<Event> events)
    {
        if (events.Count == 0) return;

        string title, text;
        if (events.Count == 1)
        {
            var e = events[0];
            title = e.Title;
            text = $"{e.Subtitle}\n{e.Body}";
            _url = e.Url;
        }
        else
        {
            title = $"{events.Count} pipelines gewijzigd";
            var lines = events.Take(MaxLines).Select(e => e.Title).ToList();
            if (events.Count > MaxLines) lines.Add($"En nog {events.Count - MaxLines} andere.");
            text = string.Join("\n", lines);
            _url = null;
        }

        var worst = events.Min(e => e.State.Priority());
        var icon = worst switch
        {
            0 => Forms.ToolTipIcon.Error,       // mislukt
            1 or 2 => Forms.ToolTipIcon.Warning, // deels geslaagd, wacht op goedkeuring
            _ => Forms.ToolTipIcon.Info,
        };
        // Windows kapt titel en tekst af op 63 en 255 tekens.
        _icon.ShowBalloonTip(10_000, Clip(title, 63), Clip(text, 255), icon);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
