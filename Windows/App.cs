using System;
using System.Drawing;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace PipelineBar;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Eén exemplaar per gebruiker.
        using var mutex = new Mutex(true, @"Local\be.ae.pipelinebar", out var first);
        if (!first) return;
        new App().Run();
    }
}

public sealed class App : Application
{
    private Forms.NotifyIcon _tray = null!;
    private PipelineStore _store = null!;
    private MenuWindow _menu = null!;
    private SettingsWindow? _settings;
    private Icon? _currentIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Theme.Apply(Resources);

        _tray = new Forms.NotifyIcon { Text = "PipelineBar", Visible = true };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) ToggleMenu();
        };
        _tray.ContextMenuStrip = BuildContextMenu();

        var notifier = new Notifier(_tray);
        notifier.ClickedWithoutUrl += ShowMenu;
        _store = new PipelineStore(notifier);
        _menu = new MenuWindow(_store, OpenSettings, Quit);
        _store.Changed += UpdateTray;
        UpdateTray();

        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(UpdateTray);
        SystemEvents.PowerModeChanged += (_, args) =>
        {
            if (args.Mode == PowerModes.Resume) Dispatcher.Invoke(_store.Restart);
        };

        _store.Restart();
        if (string.IsNullOrWhiteSpace(Settings.Current.Organization)) OpenSettings();
    }

    private Forms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Pipelines tonen", null, (_, _) => ShowMenu());
        menu.Items.Add("Nu vernieuwen", null, async (_, _) => await _store.Refresh());
        menu.Items.Add("Azure DevOps openen", null, (_, _) =>
        {
            var org = Settings.Current.Organization.Trim();
            if (org.Length > 0) Shell.Open($"https://dev.azure.com/{Uri.EscapeDataString(org)}");
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Instellingen…", null, (_, _) => OpenSettings());
        menu.Items.Add("PipelineBar afsluiten", null, (_, _) => Quit());
        return menu;
    }

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;   // licht/donker
        Dispatcher.Invoke(() =>
        {
            Theme.Apply(Resources);
            UpdateTray();
            _menu.Render();
        });
    }

    private void UpdateTray()
    {
        var icon = TrayIconRenderer.Render(_store.OverallState, _store.BadgeCount);
        _tray.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;

        var text = $"PipelineBar: {_store.Summary}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private void ToggleMenu()
    {
        // Een klik op het icoon sluit het venster eerst door het focusverlies; niet meteen heropenen.
        if (_menu.IsVisible || (DateTime.UtcNow - _menu.HiddenAt).TotalMilliseconds < 300) _menu.HideMenu();
        else _menu.ShowNearTray();
    }

    private void ShowMenu()
    {
        if (!_menu.IsVisible) _menu.ShowNearTray();
    }

    private void OpenSettings()
    {
        _menu.HideMenu();
        if (_settings is null)
        {
            _settings = new SettingsWindow(_store);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    private void Quit()
    {
        _tray.Visible = false;
        _tray.Dispose();
        Shutdown();
    }
}
