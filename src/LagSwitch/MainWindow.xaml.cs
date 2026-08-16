using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
    private readonly LinkProbe _probe = new();

    /// <summary>Reverifie periodiquement que la cible n'a pas change de chemin sous nos pieds.</summary>
    private readonly DispatcherTimer _targetWatch = new() { Interval = TimeSpan.FromSeconds(3) };

    private OverlayWindow? _overlay;

    /// <summary>Chemin sur lequel les regles sont reellement posees.</summary>
    private string? _armedPath;

    /// <summary>
    /// Faux quand le pare-feu est eteint sur le profil en cours. Dans ce cas les regles se
    /// basculent tres bien et ne bloquent rien : l'application doit refuser de couper plutot
    /// que d'afficher un etat qu'elle n'a pas.
    /// </summary>
    private bool _canBlock;

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

        _probe.Measured += sample => Post(() => RefreshProbe(sample));
        _targetWatch.Tick += (_, _) => _ = WatchTargetAsync();

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

        VersionText.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "1.0.0");

        LoadSettingsIntoUi();
        _loading = false;

        ApplyHotkey();
        RefreshModePanels();
        RefreshTargetUi();
        RefreshState(false);
        RefreshRunning(false);

        ApplyComfortSettings();

        Append("LagSwitch pret.");
        await RearmAsync();
        await RefreshFirewallStateAsync();
        _targetWatch.Start();
    }

    /// <summary>Sonde, pastille et son : tout ce qui se regle dans la carte « retour en jeu ».</summary>
    private void ApplyComfortSettings()
    {
        _probe.Host = _settings.ProbeHost;
        if (_settings.ProbeEnabled) _probe.Start();
        else
        {
            _probe.Stop();
            ProbeText.Text = "sonde // desactivee";
            ProbeText.Foreground = (SolidColorBrush)FindResource("Dim");
        }

        if (_settings.ShowOverlay)
        {
            _overlay ??= new OverlayWindow();
            _overlay.Show();
            _overlay.SetState(
                !_canBlock ? "Warn" : _cut.IsBlocked ? "Cut" : "Online",
                !_canBlock ? "INACTIF" : DescribeDirection(_cut.IsBlocked));
        }
        else if (_overlay is not null)
        {
            _overlay.Close();
            _overlay = null;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _ticker.Stop();
        _targetWatch.Stop();
        _probe.Dispose();
        _hotkeys.Dispose();

        if (_overlay is not null)
        {
            _overlay.Close();
            _overlay = null;
        }

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

        DirBothRadio.IsChecked = _settings.Direction == CutDirection.Both;
        DirOutRadio.IsChecked = _settings.Direction == CutDirection.Outbound;
        DirInRadio.IsChecked = _settings.Direction == CutDirection.Inbound;
        _cut.Direction = _settings.Direction;

        BurstBox.Text = _settings.BurstMilliseconds.ToString();
        FlickerCutBox.Text = _settings.FlickerCutMilliseconds.ToString();
        FlickerGapBox.Text = _settings.FlickerGapMilliseconds.ToString();
        MaxSecondsBox.Text = _settings.MaxCutSeconds.ToString();
        RestoreFirewallCheck.IsChecked = _settings.RestoreFirewallOnExit;

        OverlayCheck.IsChecked = _settings.ShowOverlay;
        SoundCheck.IsChecked = _settings.PlaySounds;
        ProbeCheck.IsChecked = _settings.ProbeEnabled;
    }

    private void OnDirectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.Direction = true switch
        {
            _ when DirOutRadio.IsChecked == true => CutDirection.Outbound,
            _ when DirInRadio.IsChecked == true => CutDirection.Inbound,
            _ => CutDirection.Both,
        };

        _cut.Direction = _settings.Direction;
        if (_cut.IsRunning) _cut.Stop("changement de sens");

        Append($"Sens : {_settings.Direction switch
        {
            CutDirection.Outbound => "montant seul",
            CutDirection.Inbound => "descendant seul",
            _ => "les deux",
        }}");
    }

    private void OnComfortChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.ShowOverlay = OverlayCheck.IsChecked == true;
        _settings.PlaySounds = SoundCheck.IsChecked == true;
        _settings.ProbeEnabled = ProbeCheck.IsChecked == true;
        ApplyComfortSettings();
    }

    // ---------------------------------------------------------------- etat

    private void RefreshState(bool blocked)
    {
        StateText.Text = blocked ? "[ COUPE ]" : "[ EN LIGNE ]";

        var brush = (SolidColorBrush)FindResource(blocked ? "Cut" : "Online");
        StateText.Foreground = brush;
        Caret.Foreground = brush;
        StateDot.Fill = brush;

        // La lueur rend la coupure lisible du coin de l'oeil, sans regarder le texte.
        StateText.Effect = new DropShadowEffect
        {
            Color = brush.Color,
            BlurRadius = blocked ? 18 : 10,
            ShadowDepth = 0,
            Opacity = blocked ? 0.8 : 0.45,
        };

        _overlay?.SetState(blocked ? "Cut" : "Online", DescribeDirection(blocked));

        // En mode instable, la bascule revient sans arret : le bip se joue au debut et a la
        // fin du motif (voir RefreshRunning), pas a chaque cycle.
        if (_settings.PlaySounds && _settings.Mode != CutMode.Flicker) Tone.Play(blocked);

        RefreshStateDetail();
    }

    private string DescribeDirection(bool blocked) => !blocked ? "EN LIGNE" : _settings.Direction switch
    {
        CutDirection.Outbound => "COUPE ^ MONTANT",
        CutDirection.Inbound => "COUPE v DESCENDANT",
        _ => "COUPE",
    };

    /// <summary>
    /// Sans pare-feu actif, basculer les regles ne bloque rien. Plutot que d'afficher une
    /// coupure imaginaire, l'application se declare INACTIVE et refuse de couper.
    /// </summary>
    private void RefreshBlockAvailability()
    {
        CutButton.IsEnabled = _canBlock;

        if (_canBlock)
        {
            RefreshState(_cut.IsBlocked);
            return;
        }

        var warn = (SolidColorBrush)FindResource("Warn");
        StateText.Text = "[ INACTIF ]";
        StateText.Foreground = warn;
        StateText.Effect = null;
        Caret.Foreground = warn;
        StateDot.Fill = warn;
        StateDetail.Text = "impossible de bloquer // pare-feu Windows eteint sur le profil en cours";
        _overlay?.SetState("Warn", "INACTIF");
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

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
            CutMode.Flicker => running ? "STOP" : "LANCER",
            _ => running ? "RETABLIR" : "COUPER",
        };

        CutButton.Foreground = (SolidColorBrush)FindResource(running ? "Cut" : "Text");

        if (_settings.PlaySounds && _settings.Mode == CutMode.Flicker) Tone.Play(running);

        RefreshStateDetail();
    }

    private void RefreshStateDetail()
    {
        if (!_canBlock)
        {
            StateDetail.Text = "impossible de bloquer // pare-feu Windows eteint sur le profil en cours";
            return;
        }

        if (!_cut.IsRunning)
        {
            StateDetail.Text = _firewall.IsArmed
                ? $"regles armees // raccourci {HotkeyService.Describe(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey)}"
                : "regles non posees // verifie la cible";
            return;
        }

        var elapsed = _sinceStart.Elapsed.TotalSeconds;
        var remaining = Math.Max(0, _settings.MaxCutSeconds - elapsed);
        StateDetail.Text = $"t+{elapsed:0.0}s // retour automatique dans {remaining:0.0}s";
    }

    private void Append(string line)
    {
        _log.Insert(0, $"[{DateTime.Now:HH:mm:ss.fff}] > {line}");
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

    private async void OnWellKnownTarget(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string processName }) return;

        var path = TargetCatalog.ResolveCurrentPath(processName);
        if (path is null)
        {
            var label = TargetCatalog.WellKnown.TryGetValue(processName, out var friendly) ? friendly : processName;
            Append($"{label} n'est pas lance : impossible de resoudre son chemin.");
            MessageBox.Show(
                $"{label} ne tourne pas.\n\nLance-le une fois : LagSwitch a besoin du processus en cours " +
                "pour trouver son executable, qui change d'emplacement a chaque mise a jour du jeu.",
                "LagSwitch", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TargetAppRadio.IsChecked = true;
        await SetApplicationAsync(path, processName);
    }

    private async Task SetApplicationAsync(string path, string? processName = null)
    {
        _settings.ApplicationPath = path;
        _settings.ApplicationProcessName = processName ?? Path.GetFileNameWithoutExtension(path);
        _settings.RecentApplications.Remove(path);
        _settings.RecentApplications.Insert(0, path);
        while (_settings.RecentApplications.Count > 8)
            _settings.RecentApplications.RemoveAt(_settings.RecentApplications.Count - 1);

        RefreshTargetUi();
        Append($"Cible : {Path.GetFileName(path)}");
        await RearmAsync();
    }

    /// <summary>
    /// Resout le chemin a viser. Le nom de processus prime sur le chemin memorise : une mise a
    /// jour du jeu deplace son executable, et une regle restee sur l'ancien chemin ne bloque
    /// plus rien tout en ayant l'air parfaitement en place.
    /// </summary>
    private string? ResolveTargetPath()
    {
        if (_settings.Target != TargetKind.Application) return null;

        var name = _settings.ApplicationProcessName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var live = TargetCatalog.ResolveCurrentPath(name);
            if (live is not null) return live;
        }

        return _settings.ApplicationPath;
    }

    /// <summary>Repose les regles pour la cible courante.</summary>
    private async Task RearmAsync()
    {
        if (_cut.IsRunning) _cut.Stop("changement de cible");

        var path = ResolveTargetPath();

        if (_settings.Target == TargetKind.Application && string.IsNullOrWhiteSpace(path))
        {
            Append("Choisis une application avant de couper.");
            RefreshStateDetail();
            return;
        }

        try
        {
            await _firewall.ArmAsync(_settings.Target, path);
            _armedPath = path;

            if (_settings.Target == TargetKind.Application)
            {
                var moved = !string.Equals(path, _settings.ApplicationPath, StringComparison.OrdinalIgnoreCase);
                _settings.ApplicationPath = path;
                RefreshTargetUi();
                Append(moved
                    ? $"Regles posees sur {Path.GetFileName(path!)} (chemin reresolu : la cible avait bouge)."
                    : $"Regles posees sur {Path.GetFileName(path!)}.");
            }
            else
            {
                Append("Regles posees sur tout le trafic.");
            }
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

    /// <summary>
    /// Si la cible a change d'executable — mise a jour du jeu, relance depuis un autre dossier —
    /// on repose les regles sur le nouveau chemin sans rien demander.
    /// </summary>
    private async Task WatchTargetAsync()
    {
        if (_settings.Target != TargetKind.Application) return;
        if (_cut.IsRunning) return; // jamais rearmer au milieu d'un motif

        var current = ResolveTargetPath();
        if (current is null) return;
        if (string.Equals(current, _armedPath, StringComparison.OrdinalIgnoreCase)) return;

        Append("La cible a change d'emplacement : rearmement.");
        await RearmAsync();
    }

    // --------------------------------------------------------------- sonde

    /// <summary>
    /// Confronte l'etat annonce a l'etat mesure. C'est ici qu'on attrape le cas ou l'application
    /// se croit en train de bloquer alors que le trafic passe toujours.
    /// </summary>
    private void RefreshProbe(LinkSample sample)
    {
        if (!_settings.ProbeEnabled) return;

        var claimed = _cut.IsBlocked;
        var perApp = _settings.Target == TargetKind.Application;

        string text;
        string brush;

        if (!_probe.HasEverSucceeded)
        {
            text = $"sonde // {_settings.ProbeHost} jamais joint : son silence ne prouve rien (ICMP filtre ?)";
            brush = "Warn";
        }
        else if (perApp)
        {
            // La sonde vit dans LagSwitch, pas dans la cible : elle ne peut rien conclure ici.
            text = sample.Reachable
                ? $"sonde // {sample.RoundTripMs} ms — mesure LagSwitch, pas la cible : ne prouve pas sa coupure"
                : "sonde // aucune reponse";
            brush = "Dim";
        }
        else if (claimed && sample.Reachable)
        {
            text = $"sonde // LE TRAFIC PASSE ENCORE ({sample.RoundTripMs} ms) malgre la coupure";
            brush = "Cut";
        }
        else if (claimed)
        {
            text = "sonde // coupure confirmee, aucune reponse";
            brush = "Online";
        }
        else if (sample.Reachable)
        {
            text = $"sonde // {sample.RoundTripMs} ms";
            brush = "Dim";
        }
        else
        {
            text = "sonde // aucune reponse alors qu'aucune coupure n'est active";
            brush = "Warn";
        }

        ProbeText.Text = text;
        ProbeText.Foreground = (SolidColorBrush)FindResource(brush);
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

    private void OnHotkeyPressed()
    {
        if (!_canBlock)
        {
            Append("Raccourci ignore : pare-feu eteint, la coupure n'aurait aucun effet.");
            return;
        }

        _cut.Trigger(_settings);
    }

    private void OnCutButton(object sender, RoutedEventArgs e)
    {
        if (_settings.Mode == CutMode.Hold) return; // gere par appui / relachement souris
        if (!_canBlock) return;
        _cut.Trigger(_settings);
    }

    private void OnCutButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Mode != CutMode.Hold || !_canBlock) return;
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

        _canBlock = protectedNow is true;
        RefreshBlockAvailability();

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
