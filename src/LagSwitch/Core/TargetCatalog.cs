using System.Diagnostics;
using System.IO;

namespace LagSwitch.Core;

public sealed record RunningApp(string DisplayName, string ExecutablePath, string ProcessName)
{
    public bool IsWellKnown => TargetCatalog.WellKnown.ContainsKey(ProcessName);

    public override string ToString() => DisplayName;
}

/// <summary>
/// Trouve les applications a viser. La regle du jeu : le <b>nom de processus</b> fait autorite,
/// jamais le chemin. Roblox s'installe dans <c>...\Versions\version-&lt;hash&gt;\</c> et ce hash
/// change a chaque mise a jour ; une regle de pare-feu clouee sur l'ancien chemin continue
/// d'exister, reste active, et ne bloque plus rien du tout.
/// </summary>
public static class TargetCatalog
{
    /// <summary>Cibles proposees d'office, parce que ce sont celles qu'on vise vraiment.</summary>
    public static readonly IReadOnlyDictionary<string, string> WellKnown =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RobloxPlayerBeta"] = "Roblox",
            ["RobloxStudioBeta"] = "Roblox Studio",
        };

    public static IReadOnlyList<RunningApp> Snapshot()
    {
        var found = new List<RunningApp>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var known = WellKnown.ContainsKey(process.ProcessName);
                if (!known && process.MainWindowHandle == IntPtr.Zero) continue;

                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path)) continue;

                found.Add(Describe(path, process.ProcessName));
            }
            catch
            {
                // Processus protege ou deja termine : on passe.
            }
            finally
            {
                process.Dispose();
            }
        }

        return found
            .GroupBy(app => app.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(app => app.IsWellKnown)
            .ThenBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static RunningApp Describe(string executablePath, string? processName = null)
    {
        var name = processName ?? Path.GetFileNameWithoutExtension(executablePath);
        var label = WellKnown.TryGetValue(name, out var friendly)
            ? $"{friendly}  ({name}.exe)"
            : $"{name}  ({Path.GetFileName(executablePath)})";
        return new RunningApp(label, executablePath, name);
    }

    /// <summary>
    /// Rend le chemin ACTUEL de l'executable d'un processus en cours, ou null s'il ne tourne pas.
    /// C'est ce qui evite qu'une mise a jour du jeu ne vide silencieusement la regle.
    /// </summary>
    public static string? ResolveCurrentPath(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
            catch
            {
                // Sans les droits, on ne lit pas le module : on tente le processus suivant.
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    public static bool IsRunning(string processName) => Process.GetProcessesByName(processName).Length > 0;
}
