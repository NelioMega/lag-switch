using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LagSwitch.Core;
using Microsoft.Win32;

namespace LagSwitch;

public partial class MainWindow : Window
{
    private readonly Settings _settings = App.Current.Settings;
    private readonly FirewallEngine _firewall = App.Current.Firewall;
    private readonly CutEngine _cut = App.Current.Cut;
    private readonly HotkeyService _hotkeys = new();
    private readonly ObservableCollection<string> _log = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _sinceStart = new();

    private bool _loading = true;
    private bool _capturingHotkey;

    public MainWindow()
    {
        InitializeComponent();

        LogList.ItemsSource = _log;
        _ticker.Tick += (_, _) => RefreshStateDetail();

        _cut.BlockedChanged += blocked => Post(() => RefreshState(blocked));
        _cut.RunningChanged += running => Post(() => RefreshRunning(running));
        _cut.Logged += line => Post(() => Append(line));

        _hotkeys.Pressed += () => Post(OnHotkeyPressed);
        _hotkeys.Released += () => Post(() => _cut.Release());

        CutButton.PreviewMouseLeftButtonDown += OnCutButtonDown;
        CutButton.PreviewMouseLeftButtonUp += OnCutButtonUp;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void Post(Action action) => Dispatcher.BeginInvoke(action);

    // ------------------------------------------------------------ cycle de vie

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hotkeys.Attach(this);

        LoadSettingsIntoUi();
        _loading = false;

        ApplyHotkey();
        RefreshModePanels();
        RefreshTargetUi();
        RefreshState(false);
        RefreshRunning(false);

        Append("LagSwitch pret.");
        await RearmAsync();
        await RefreshFirewallStateAsync();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _ticker.Stop();
        _hotkeys.Dispose();
        _cut.PanicRestore();
    }

    private void LoadSettingsIntoUi()
    {
        TargetAllRadio.IsChecked = _settings.Target == TargetKind.AllTraffic;
        TargetAppRadio.IsChecked = _settings.Target == TargetKind.Application;

        ModeToggleRadio.IsChecked = _settings.Mode == CutMode.Toggle;
        ModeBurstRadio.IsChecked = _settings.Mode == CutMode.Burst;
        ModeHoldRadio.IsChecked = _settings.Mode == CutMode.Hold;
        ModeFlickerRadio.IsChecked = _settings.Mode == CutMode.Flicker;

        BurstBox.Text = _settings.BurstMilliseconds.ToString();
        FlickerCutBox.Text = _settings.FlickerCutMilliseconds.ToString();
        FlickerGapBox.Text = _settings.FlickerGapMilliseconds.ToString();
        MaxSecondsBox.Text = _settings.MaxCutSeconds.ToString();
        RestoreFirewallCheck.IsChecked = _settings.RestoreFirewallOnExit;
    }

    // ---------------------------------------------------------------- etat

    private void RefreshState(bool blocked)
    {
        StateText.Text = blocked ? "COUPE" : "EN LIGNE";
        var brush = (System.Windows.Media.Brush)FindResource(blocked ? "Cut" : "Online");
        StateText.Foreground = brush;
        StateDot.Fill = brush;
        RefreshStateDetail();
    }

    private void RefreshRunning(bool running)
    {
        if (running)
        {
            _sinceStart.Restart();
            _ticker.Start();
        }
        else
        {
            _ticker.Stop();
            _sinceStart.Stop();
        }

        CutButton.Content = CurrentMode switch
        {
            CutMode.Burst => "IMPULSION",
            CutMode.Hold => "MAINTENIR",
            CutMode.Flicker => running ? "ARRETER" : "LANCER",
            _ => running ? "RETABLIR" : "COUPER",
        };

        RefreshStateDetail();
    }

    private void RefreshStateDetail()
    {
        if (!_cut.IsRunning)
        {
            StateDetail.Text = _firewall.IsArmed
                ? $"Pret. Raccourci : {HotkeyService.Describe(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey)}"
                : "Regles non posees : verifie la cible.";
            return;
        }

        var elapsed = _sinceStart.Elapsed.TotalSeconds;
        var remaining = Math.Max(0, _settings.MaxCutSeconds - elapsed);
        StateDetail.Text = $"{elapsed:0.0} s ecoulees — retour automatique dans {remaining:0.0} s";
    }

    private void Append(string line)
    {
        _log.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {line}");
        while (_log.Count > 200) _log.RemoveAt(_log.Count - 1);
    }

    // -------------------------------------------------------------- cible

    private async void OnTargetChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Target = TargetAppRadio.IsChecked == true ? TargetKind.Application : TargetKind.AllTraffic;
        RefreshTargetUi();
        await RearmAsync();
    }

    private void RefreshTargetUi()
    {
        var perApp = _settings.Target == TargetKind.Application;
        PickAppButton.IsEnabled = perApp;
        BrowseButton.IsEnabled = perApp;

        TargetPathText.Text = perApp
            ? _settings.ApplicationPath ?? "aucune application choisie"
            : "tout le trafic entrant et sortant";
    }

    private async void OnPickApp(object sender, RoutedEventArgs e)
    {
        var picker = new AppPickerWindow { Owner = this };
        if (picker.ShowDialog() != true || picker.Chosen is null) return;

        await SetApplicationAsync(picker.Chosen.ExecutablePath);
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir l'executable a couper",
            Filter = "Applications (*.exe)|*.exe|Tous les fichiers (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;
        await SetApplicationAsync(dialog.FileName);
    }

    private async Task SetApplicationAsync(string path)
    {
        _settings.ApplicationPath = path;
        _settings.RecentApplications.Remove(path);
        _settings.RecentApplications.Insert(0, path);
        while (_settings.RecentApplications.Count > 8)
            _settings.RecentApplications.RemoveAt(_settings.RecentApplications.Count - 1);

        RefreshTargetUi();
        Append($"Cible : {Path.GetFileName(path)}");
        await RearmAsync();
    }

    /// <summary>Repose les regles pour la cible courante.</summary>
    private async Task RearmAsync()
    {
        if (_cut.IsRunning) _cut.Stop("changement de cible");

        if (_settings.Target == TargetKind.Application && string.IsNullOrWhiteSpace(_settings.ApplicationPath))
        {
            Append("Choisis une application avant de couper.");
            RefreshStateDetail();
            return;
        }

        try
        {
            await _firewall.ArmAsync(_settings.Target, _settings.ApplicationPath);
            Append(_settings.Target == TargetKind.Application
                ? $"Regles posees sur {Path.GetFileName(_settings.ApplicationPath!)}."
                : "Regles posees sur tout le trafic.");
        }
        catch (Exception ex)
        {
            Append($"Echec de la pose des regles : {ex.Message}");
            MessageBox.Show(
                $"Impossible de poser les regles de pare-feu.\n\n{ex.Message}\n\n" +
                "Verifie que LagSwitch est lance en administrateur et que le service Pare-feu Windows tourne.",
                "LagSwitch",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        RefreshStateDetail();
    }

    // --------------------------------------------------------------- mode

    private CutMode CurrentMode => _settings.Mode;

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.Mode = true switch
        {
            _ when ModeBurstRadio.IsChecked == true => CutMode.Burst,
            _ when ModeHoldRadio.IsChecked == true => CutMode.Hold,
            _ when ModeFlickerRadio.IsChecked == true => CutMode.Flicker,
            _ => CutMode.Toggle,
        };

        if (_cut.IsRunning) _cut.Stop("changement de mode");
        RefreshModePanels();
        ApplyHotkey();
        RefreshRunning(false);
    }

    private void RefreshModePanels()
    {
        BurstPanel.Visibility = _settings.Mode == CutMode.Burst ? Visibility.Visible : Visibility.Collapsed;
        FlickerPanel.Visibility = _settings.Mode == CutMode.Flicker ? Visibility.Visible : Visibility.Collapsed;
        RefreshFlickerSummary();
    }

    private void RefreshFlickerSummary()
    {
        var cycle = _settings.FlickerCutMilliseconds + _settings.FlickerGapMilliseconds;
        if (cycle <= 0) return;

        var lossRatio = 100.0 * _settings.FlickerCutMilliseconds / cycle;
        FlickerSummary.Text =
            $"Cycle de {cycle} ms, connexion absente {lossRatio:0} % du temps. " +
            "Chaque bascule coute environ 30 ms au pare-feu : en dessous de 100 ms, les durees reelles derivent.";
    }

    private void OnFlickerPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;

        var parts = tag.Split(',');
        if (parts.Length != 2) return;

        _settings.FlickerCutMilliseconds = int.Parse(parts[0]);
        _settings.FlickerGapMilliseconds = int.Parse(parts[1]);
        _settings.Clamp();

        FlickerCutBox.Text = _settings.FlickerCutMilliseconds.ToString();
        FlickerGapBox.Text = _settings.FlickerGapMilliseconds.ToString();
        RefreshFlickerSummary();
    }

    private void OnNumbersChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.BurstMilliseconds = Read(BurstBox, _settings.BurstMilliseconds);
        _settings.FlickerCutMilliseconds = Read(FlickerCutBox, _settings.FlickerCutMilliseconds);
        _settings.FlickerGapMilliseconds = Read(FlickerGapBox, _settings.FlickerGapMilliseconds);
        _settings.MaxCutSeconds = Read(MaxSecondsBox, _settings.MaxCutSeconds);
        _settings.Clamp();

        BurstBox.Text = _settings.BurstMilliseconds.ToString();
        FlickerCutBox.Text = _settings.FlickerCutMilliseconds.ToString();
        FlickerGapBox.Text = _settings.FlickerGapMilliseconds.ToString();
        MaxSecondsBox.Text = _settings.MaxCutSeconds.ToString();

        RefreshFlickerSummary();

        static int Read(TextBox box, int fallback) =>
            int.TryParse(box.Text.Trim(), out var value) ? value : fallback;
    }

    // ---------------------------------------------------------- declenchement

    private void OnHotkeyPressed() => _cut.Trigger(_settings);

    private void OnCutButton(object sender, RoutedEventArgs e)
    {
        if (_settings.Mode == CutMode.Hold) return; // gere par appui / relachement souris
        _cut.Trigger(_settings);
    }

    private void OnCutButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Mode != CutMode.Hold) return;
        _cut.Trigger(_settings);
    }

    private void OnCutButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Mode != CutMode.Hold) return;
        _cut.Release();
    }

    private void OnPanic(object sender, RoutedEventArgs e)
    {
        _cut.PanicRestore();
        Append("Panique : connexion retablie.");
    }

    // ------------------------------------------------------------- raccourci

    private void OnCaptureHotkey(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyButton.Content = "appuie sur une touche...";
        HotkeyStatus.Text = "Echap pour annuler.";
        _hotkeys.Unregister();
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HotkeyService.IsModifierKey(key)) return;

        _capturingHotkey = false;

        if (key == Key.Escape)
        {
            ApplyHotkey();
            return;
        }

        _settings.HotkeyModifiers = HotkeyService.CurrentModifiers();
        _settings.HotkeyVirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        ApplyHotkey();
    }

    private void ApplyHotkey()
    {
        _hotkeys.WatchRelease = _settings.Mode == CutMode.Hold;

        var label = HotkeyService.Describe(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
        HotkeyButton.Content = label;

        var ok = _hotkeys.Register(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
        HotkeyStatus.Text = ok
            ? "Actif, y compris en jeu."
            : "Deja pris par une autre application : choisis-en un autre.";
        HotkeyStatus.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Muted" : "Warn");

        if (!ok) Append($"Raccourci {label} refuse par Windows (deja enregistre ailleurs).");
        RefreshStateDetail();
    }

    // -------------------------------------------------------------- pare-feu

    private async void OnRecheckFirewall(object sender, RoutedEventArgs e) => await RefreshFirewallStateAsync();

    private void OnRestoreFirewallChanged(object sender, RoutedEventArgs e) =>
        _settings.RestoreFirewallOnExit = RestoreFirewallCheck.IsChecked == true;

    private async void OnEnableFirewall(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Activer le pare-feu Windows sur les profils ou il est eteint ?\n\n" +
            "Sans lui, LagSwitch ne peut rien bloquer. En contrepartie, Windows se remettra a " +
            "refuser les connexions entrantes non sollicitees, ce qui peut couper un serveur local, " +
            "une machine virtuelle ou un partage reseau.",
            "LagSwitch",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var ok = await _firewall.EnableFirewallAsync();
        Append(ok ? "Pare-feu Windows active." : "Activation partielle du pare-feu : certains profils ont resiste.");
        await RefreshFirewallStateAsync();
    }

    private async Task RefreshFirewallStateAsync()
    {
        var health = await _firewall.ReadHealthAsync();

        FirewallStateText.Text =
            $"Pare-feu Windows — Domaine : {Describe(health.Domain)}, " +
            $"Prive : {Describe(health.Private)}, Public : {Describe(health.Public)}." +
            (health.CurrentProfiles == 0 ? "" : $"  Profil en vigueur : {DescribeProfiles(health.CurrentProfiles)}.");

        var protectedNow = health.ActiveProfileProtected;
        var needsFix = protectedNow is not true;

        FirewallStateText.Foreground = (System.Windows.Media.Brush)FindResource(needsFix ? "Warn" : "Text");
        FirewallFixPanel.Visibility = needsFix ? Visibility.Visible : Visibility.Collapsed;
        RestoreFirewallCheck.IsChecked = _settings.RestoreFirewallOnExit;

        static string Describe(bool? state) => state switch
        {
            true => "actif",
            false => "eteint",
            _ => "inconnu",
        };

        static string DescribeProfiles(int mask)
        {
            var names = new List<string>();
            if ((mask & 1) != 0) names.Add("Domaine");
            if ((mask & 2) != 0) names.Add("Prive");
            if ((mask & 4) != 0) names.Add("Public");
            return names.Count == 0 ? "aucun" : string.Join(" + ", names);
        }
    }
}
