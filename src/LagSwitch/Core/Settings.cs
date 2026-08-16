using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LagSwitch.Core;

/// <summary>Ce que la coupure vise.</summary>
public enum TargetKind
{
    /// <summary>Tout le trafic de la machine.</summary>
    AllTraffic,

    /// <summary>Le trafic d'un seul executable.</summary>
    Application,
}

/// <summary>Quel sens du trafic la coupure vise.</summary>
public enum CutDirection
{
    /// <summary>Montant et descendant : la machine disparait du reseau.</summary>
    Both,

    /// <summary>Montant seul : le serveur cesse de t'entendre, mais tu continues de le voir.</summary>
    Outbound,

    /// <summary>Descendant seul : tu continues d'emettre, mais tu ne recois plus rien.</summary>
    Inbound,
}

/// <summary>Comment le raccourci declenche la coupure.</summary>
public enum CutMode
{
    /// <summary>Une pression coupe, la suivante retablit.</summary>
    Toggle,

    /// <summary>Une pression coupe pendant une duree fixe, puis retablit seule.</summary>
    Burst,

    /// <summary>Coupe tant que la touche reste enfoncee.</summary>
    Hold,

    /// <summary>Alterne coupure et retour en ligne : simule une connexion instable.</summary>
    Flicker,
}

public sealed class Settings
{
    public CutMode Mode { get; set; } = CutMode.Toggle;
    public TargetKind Target { get; set; } = TargetKind.AllTraffic;

    public CutDirection Direction { get; set; } = CutDirection.Both;

    /// <summary>Chemin complet de l'executable vise quand <see cref="Target"/> vaut Application.</summary>
    public string? ApplicationPath { get; set; }

    /// <summary>
    /// Nom du processus vise, sans extension. C'est LUI qui fait autorite : le chemin d'un jeu
    /// change a chaque mise a jour (Roblox vit dans un dossier version-&lt;hash&gt;), donc une regle
    /// clouee sur un chemin fige cesse de bloquer quoi que ce soit, sans rien dire.
    /// </summary>
    public string? ApplicationProcessName { get; set; }

    /// <summary>Hote interroge par la sonde pour constater l'etat reel du lien.</summary>
    public string ProbeHost { get; set; } = "1.1.1.1";

    public bool ProbeEnabled { get; set; } = true;

    /// <summary>Pastille toujours au-dessus, pour voir l'etat sans quitter le plein ecran.</summary>
    public bool ShowOverlay { get; set; } = true;

    public bool PlaySounds { get; set; } = true;

    /// <summary>Code de touche virtuelle du raccourci. F8 par defaut.</summary>
    public uint HotkeyVirtualKey { get; set; } = 0x77;

    /// <summary>Modificateurs du raccourci (combinaison de <see cref="Native.Mod"/>).</summary>
    public uint HotkeyModifiers { get; set; } = 0;

    /// <summary>Duree d'une coupure en mode impulsion.</summary>
    public int BurstMilliseconds { get; set; } = 900;

    /// <summary>En mode instable : duree coupee de chaque cycle.</summary>
    public int FlickerCutMilliseconds { get; set; } = 150;

    /// <summary>En mode instable : duree en ligne de chaque cycle.</summary>
    public int FlickerGapMilliseconds { get; set; } = 400;

    /// <summary>Garde-fou : au-dela, la connexion revient toute seule.</summary>
    public int MaxCutSeconds { get; set; } = 15;

    /// <summary>Si l'application a active le pare-feu, le remettre comme avant en quittant.</summary>
    public bool RestoreFirewallOnExit { get; set; } = true;

    public List<string> RecentApplications { get; set; } = new();

    public void Clamp()
    {
        // Une bascule de regle coute environ 30 ms : en dessous de 50 ms, la consigne
        // ne veut plus dire grand-chose.
        BurstMilliseconds = Math.Clamp(BurstMilliseconds, 50, 10_000);
        FlickerCutMilliseconds = Math.Clamp(FlickerCutMilliseconds, 50, 5_000);
        FlickerGapMilliseconds = Math.Clamp(FlickerGapMilliseconds, 50, 5_000);
        MaxCutSeconds = Math.Clamp(MaxCutSeconds, 1, 300);
        if (HotkeyVirtualKey == 0) HotkeyVirtualKey = 0x77;
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LagSwitch");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options);
                if (loaded is not null)
                {
                    loaded.Clamp();
                    return loaded;
                }
            }
        }
        catch
        {
            // Un fichier illisible ne doit pas empecher l'application de demarrer.
        }

        return new Settings();
    }

    public static void Save(Settings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Ecriture best-effort : perdre les reglages est moins grave que planter.
        }
    }
}
