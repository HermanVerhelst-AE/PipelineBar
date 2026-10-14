using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace PipelineBar;

public partial class SettingsWindow : Window
{
    private readonly PipelineStore _store;
    private readonly bool _loading;
    private bool _testing;

    private static Settings Prefs => Settings.Current;

    public SettingsWindow(PipelineStore store)
    {
        _store = store;
        _loading = true;
        InitializeComponent();

        OrganizationBox.Text = Prefs.Organization;
        ProjectsBox.Text = Prefs.Projects;
        FilterBox.Text = Prefs.PipelineFilter;
        OnlyMineBox.IsChecked = Prefs.OnlyMine;
        ReleasesBox.IsChecked = Prefs.IncludeReleases;
        NotifyStartBox.IsChecked = Prefs.NotifyOnStart;
        NotifySuccessBox.IsChecked = Prefs.NotifyOnSuccess;
        Select(AgeBox, Prefs.MaxAgeDays);
        Select(RefreshBox, Prefs.RefreshMinutes);
        AutostartBox.IsChecked = Autostart.IsEnabled;

        _loading = false;
        UpdateTokenState();
        Loaded += (_, _) => (Prefs.Organization.Length == 0 ? (Control)OrganizationBox : TokenBox).Focus();
    }

    private static void Select(ComboBox box, int value)
    {
        box.SelectedValue = value.ToString();
        if (box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    private static int Selected(ComboBox box) => int.TryParse(box.SelectedValue as string, out var v) ? v : 0;

    private string Organization => OrganizationBox.Text.Trim();

    // Verbinding

    private void UpdateTokenState()
    {
        var hasToken = Organization.Length > 0 && CredentialStore.Read(Organization) is not null;
        TokenHint.Text = hasToken ? "Opgeslagen in Windows Referentiebeheer." : "Plak je token.";
        SaveTokenButton.IsEnabled = TokenBox.Password.Length > 0 && Organization.Length > 0;
        TestButton.IsEnabled = !_testing && Organization.Length > 0 && (hasToken || TokenBox.Password.Length > 0);
        TokenLink.IsEnabled = Organization.Length > 0;
    }

    private void Organization_Commit(object sender, RoutedEventArgs e)
    {
        if (Organization == Prefs.Organization) return;
        Prefs.Organization = Organization;
        Prefs.Save();
        TestResult.Visibility = Visibility.Collapsed;
        UpdateTokenState();
        _store.Restart();
    }

    private void Organization_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Organization_Commit(sender, e);
        else Dispatcher.BeginInvoke(UpdateTokenState);
    }

    private void Token_Changed(object sender, RoutedEventArgs e) => UpdateTokenState();

    private void Token_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveToken_Click(sender, e);
    }

    private void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        Organization_Commit(sender, e);
        var token = TokenBox.Password.Trim();
        if (token.Length == 0 || Organization.Length == 0) return;
        if (CredentialStore.Save(token, Organization))
        {
            TokenBox.Clear();
            ShowResult(true, "Token opgeslagen");
            _store.Restart();
        }
        else
        {
            ShowResult(false, "Opslaan in Referentiebeheer mislukt");
        }
        UpdateTokenState();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var candidate = TokenBox.Password.Length > 0 ? TokenBox.Password.Trim() : CredentialStore.Read(Organization) ?? "";
        _testing = true;
        UpdateTokenState();
        TestResult.Visibility = Visibility.Collapsed;
        try
        {
            var count = (await new AzureDevOpsClient(Organization, candidate, Prefs.MaxAgeDays).Projects()).Count;
            ShowResult(true, $"Verbonden met {count} project{(count == 1 ? "" : "en")}");
        }
        catch (Exception ex)
        {
            ShowResult(false, ex is AdoException ? ex.Message : ex.GetBaseException().Message);
        }
        finally
        {
            _testing = false;
            UpdateTokenState();
        }
    }

    private void ShowResult(bool ok, string text)
    {
        TestResult.Text = (ok ? "✓ " : "✕ ") + text;
        TestResult.Foreground = Theme.Brush(Theme.StateColor(ok ? RunState.Succeeded : RunState.Failed, Theme.AppsDark));
        TestResult.Visibility = Visibility.Visible;
    }

    private void TokenLink_Click(object sender, RoutedEventArgs e)
    {
        if (Organization.Length > 0)
            Shell.Open($"https://dev.azure.com/{Uri.EscapeDataString(Organization)}/_usersSettings/tokens");
    }

    // Pipelines en meldingen

    private void Text_Commit(object sender, RoutedEventArgs e)
    {
        if (ProjectsBox.Text == Prefs.Projects && FilterBox.Text == Prefs.PipelineFilter) return;
        Prefs.Projects = ProjectsBox.Text;
        Prefs.PipelineFilter = FilterBox.Text;
        Prefs.Save();
        _store.Restart();
    }

    private void Text_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Text_Commit(sender, e);
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Prefs.OnlyMine = OnlyMineBox.IsChecked == true;
        Prefs.IncludeReleases = ReleasesBox.IsChecked == true;
        Prefs.NotifyOnStart = NotifyStartBox.IsChecked == true;
        Prefs.NotifyOnSuccess = NotifySuccessBox.IsChecked == true;
        Prefs.MaxAgeDays = Selected(AgeBox);
        Prefs.RefreshMinutes = Math.Max(1, Selected(RefreshBox));
        Prefs.Save();
        _store.Restart();
    }

    private void NotificationSettings_Click(object sender, RoutedEventArgs e) => Shell.Open("ms-settings:notifications");

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        Autostart.Set(AutostartBox.IsChecked == true);
        AutostartBox.IsChecked = Autostart.IsEnabled;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Organization_Commit(this, new RoutedEventArgs());
        Text_Commit(this, new RoutedEventArgs());
    }
}

/// Start PipelineBar bij het aanmelden via HKCU\…\Run.
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PipelineBar";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string value
                && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(Name, Command);
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch { }
    }
}
