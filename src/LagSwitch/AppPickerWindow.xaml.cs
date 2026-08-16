using System.Windows;
using System.Windows.Controls;
using LagSwitch.Core;

namespace LagSwitch;

public partial class AppPickerWindow : Window
{
    private IReadOnlyList<RunningApp> _all = Array.Empty<RunningApp>();

    public RunningApp? Chosen { get; private set; }

    public AppPickerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Reload();
            SearchBox.Focus();
        };
    }

    private void Reload()
    {
        _all = TargetCatalog.Snapshot();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var needle = SearchBox.Text.Trim();
        AppList.ItemsSource = needle.Length == 0
            ? _all
            : _all.Where(app =>
                app.DisplayName.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ||
                app.ExecutablePath.Contains(needle, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not RunningApp app) return;
        Chosen = app;
        DialogResult = true;
    }
}
