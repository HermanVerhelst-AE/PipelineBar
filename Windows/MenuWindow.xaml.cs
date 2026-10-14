using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PipelineBar;

public sealed record RunRow(PipelineRun Run, string Glyph, Brush GlyphBrush, bool Pulse, string Name,
                            Visibility ReleaseVisibility, string Detail, string Ago, string? Url, string Tooltip,
                            double Opacity, bool CanToggle, string ToggleGlyph, string ToggleTooltip);

public sealed record GroupRow(string Project, string FailedText, RunRow[] Runs);

/// Het uitklapvenster boven het systeemvak.
public partial class MenuWindow : Window
{
    private const double ShadowMargin = 12;   // marge rond de kaart in de XAML
    private const double Gap = 10;            // afstand tot de taakbalk

    private readonly PipelineStore _store;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DoubleAnimation _spin = new(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };

    private System.Drawing.Point _anchor;   // muisaanwijzer bij het openen, in schermpixels

    public DateTime HiddenAt { get; private set; }

    public MenuWindow(PipelineStore store, Action openSettings, Action quit)
    {
        _store = store;
        _openSettings = openSettings;
        _quit = quit;
        InitializeComponent();

        _store.Changed += () => { if (IsVisible) Render(); };
        _clock.Tick += (_, _) => Render();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PositionNearCursor, DispatcherPriority.Loaded);
    }

    // Tonen en verbergen

    public void ShowNearTray()
    {
        _anchor = Forms.Cursor.Position;
        Render();
        Opacity = 0;   // eerst plaatsen, dan pas zichtbaar maken
        Show();
        UpdateLayout();
        PositionNearCursor();
        Opacity = 1;
        Activate();
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
        _clock.Start();
    }

    public void HideMenu()
    {
        if (!IsVisible) return;
        Hide();
        HiddenAt = DateTime.UtcNow;
        _clock.Stop();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        HideMenu();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 verbergt enkel; afsluiten gaat via de knop.
        e.Cancel = true;
        HideMenu();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape: HideMenu(); break;
            case Key.F5:
            case Key.R when ctrl: _ = _store.Refresh(); break;
            case Key.OemComma when ctrl: _openSettings(); break;
            case Key.Q when ctrl: _quit(); break;
            default: base.OnPreviewKeyDown(e); return;
        }
        e.Handled = true;
    }

    /// Plaatst het venster naast de taakbalk, bij de muisaanwijzer (daar staat het icoon).
    private void PositionNearCursor()
    {
        if (!IsVisible) return;
        var cursor = _anchor;
        var screen = Forms.Screen.FromPoint(cursor);
        var area = screen.WorkingArea;
        var bounds = screen.Bounds;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        int inset = (int)Math.Round((ShadowMargin - Gap) * scale);   // zichtbare kaart staat Gap van de rand

        int minX = area.Left - inset, maxX = area.Right - w + inset;
        int minY = area.Top - inset, maxY = area.Bottom - h + inset;
        int x, y;
        if (area.Bottom < bounds.Bottom) { x = cursor.X - w / 2; y = maxY; }        // taakbalk onderaan
        else if (area.Top > bounds.Top) { x = cursor.X - w / 2; y = minY; }         // bovenaan
        else if (area.Left > bounds.Left) { x = minX; y = cursor.Y - h / 2; }       // links
        else if (area.Right < bounds.Right) { x = maxX; y = cursor.Y - h / 2; }     // rechts
        else { x = maxX; y = maxY; }                                                 // automatisch verbergen

        x = Math.Clamp(x, minX, Math.Max(minX, maxX));
        y = Math.Clamp(y, minY, Math.Max(minY, maxY));
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, 0, 0,
                     SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    // Inhoud

    public void Render()
    {
        var dark = Theme.AppsDark;
        var now = DateTimeOffset.Now;
        var overall = _store.OverallState ?? RunState.None;
        var org = Settings.Current.Organization.Trim();

        HeaderGlyph.Text = overall.Glyph();
        HeaderGlyph.Foreground = Theme.Brush(Theme.StateColor(overall, dark));
        SummaryText.Text = _store.Summary;
        UpdatedText.Text = UpdatedLabel(org, now);
        OpenAdoButton.IsEnabled = org.Length > 0;

        DismissAllButton.Visibility = _store.Problem is null && _store.DismissibleCount > 0
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !_store.IsRefreshing;
        if (_store.IsRefreshing) RefreshRotation.BeginAnimation(RotateTransform.AngleProperty, _spin);
        else RefreshRotation.BeginAnimation(RotateTransform.AngleProperty, null);

        ProblemPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        ListScroll.Visibility = Visibility.Collapsed;

        if (_store.Problem is { } problem)
        {
            ProblemText.Text = problem;
            ProblemPanel.Visibility = Visibility.Visible;
        }
        else if (_store.Runs.Count == 0)
        {
            EmptyPanel.Visibility = Visibility.Visible;
        }
        else
        {
            GroupList.ItemsSource = _store.Groups.Select(g => new GroupRow(
                g.Project,
                g.Runs.Count(r => r.State == RunState.Failed && !_store.IsDismissed(r)) is var n and > 0
                    ? $"{n} mislukt" : "",
                g.Runs.Select(r =>
                {
                    var dismissed = _store.IsDismissed(r);
                    return new RunRow(
                        Run: r,
                        Glyph: r.State.Glyph(),
                        GlyphBrush: Theme.Brush(Theme.StateColor(dismissed ? RunState.None : r.State, dark)),
                        Pulse: r.State == RunState.InProgress && !dismissed,
                        Name: r.Name,
                        ReleaseVisibility: r.Kind == RunKind.Release ? Visibility.Visible : Visibility.Collapsed,
                        Detail: r.Detail.Length == 0 ? r.State.Label() : r.Detail,
                        Ago: r.Date is { } d ? Format.ShortRelative(d, now) : "",
                        Url: r.Url,
                        Tooltip: dismissed
                            ? $"{r.State.Label()}, genegeerd. Klik om te openen in Azure DevOps."
                            : $"{r.State.Label()}. Klik om te openen in Azure DevOps.",
                        Opacity: dismissed ? 0.55 : 1,
                        CanToggle: dismissed || r.State.NeedsAttention(),
                        ToggleGlyph: dismissed ? "\uE7B3" : "\uED1A",   // View / Hide
                        ToggleTooltip: dismissed ? "Opnieuw tonen" : "Negeren tot er iets verandert");
                }).ToArray())).ToList();
            ErrorList.ItemsSource = _store.Errors;
            ErrorList.Visibility = _store.Errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ListScroll.Visibility = Visibility.Visible;
        }

        if (IsVisible) Dispatcher.BeginInvoke(PositionNearCursor, DispatcherPriority.Loaded);   // hoogte kan wijzigen
    }

    private string UpdatedLabel(string org, DateTimeOffset now)
    {
        if (_store.LastUpdated is not { } date) return org.Length == 0 ? "Azure DevOps" : org;
        var ago = Format.ShortRelative(date, now);
        return ago == "nu" ? $"{org}, net bijgewerkt" : $"{org}, {ago} geleden bijgewerkt";
    }

    // Knoppen

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = _store.Refresh();
    private void Settings_Click(object sender, RoutedEventArgs e) => _openSettings();
    private void Quit_Click(object sender, RoutedEventArgs e) => _quit();

    private void OpenAdo_Click(object sender, RoutedEventArgs e)
    {
        var org = Settings.Current.Organization.Trim();
        if (org.Length > 0) Shell.Open($"https://dev.azure.com/{Uri.EscapeDataString(org)}");
        HideMenu();
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string url) Shell.Open(url);
        HideMenu();
    }

    private void DismissAll_Click(object sender, RoutedEventArgs e) => _store.DismissAll();

    private void ToggleDismiss_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;   // niet ook de rij openen
        if ((sender as Button)?.Tag is not PipelineRun run) return;
        if (_store.IsDismissed(run)) _store.Undismiss(run);
        else _store.Dismiss(run);
    }

    // Win32

    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
