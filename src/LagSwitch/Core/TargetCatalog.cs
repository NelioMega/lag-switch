using System.Diagnostics;
using System.IO;

namespace LagSwitch.Core;

public sealed record RunningApp(string DisplayName, string ExecutablePath)
{
    public override string ToString() => DisplayName;
}

/// <summary>Liste les applications visibles a l'ecran, pour n'avoir a chercher un .exe a la main.</summary>
public static class TargetCatalog
{
    public static IReadOnlyList<RunningApp> Snapshot()
    {
        var found = new List<RunningApp>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero) continue;

                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path)) continue;

                found.Add(new RunningApp($"{process.ProcessName}  ({Path.GetFileName(path)})", path));
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
            .OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static RunningApp Describe(string executablePath) =>
        new($"{Path.GetFileNameWithoutExtension(executablePath)}  ({Path.GetFileName(executablePath)})",
            executablePath);
}
