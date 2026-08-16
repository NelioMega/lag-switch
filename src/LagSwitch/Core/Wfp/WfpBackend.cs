namespace LagSwitch.Core.Wfp;

/// <summary>
/// Habille <see cref="WfpEngine"/> pour que <see cref="CutEngine"/> puisse le piloter comme
/// l'autre moteur. Les appels WFP tenant en une fraction de milliseconde et l'API etant deja
/// protegee par un verrou, aucun fil dedie n'est necessaire ici — contrairement au COM du
/// pare-feu, qui lui en exige un.
/// </summary>
public sealed class WfpBackend : IBlockBackend
{
    private readonly WfpEngine _engine = new();

    public string Name => "WFP";

    public bool IsArmed { get; private set; }

    public bool NeedsWindowsFirewall => false;

    public Task ArmAsync(TargetKind target, string? applicationPath)
    {
        IsArmed = false;

        _engine.Open();

        var path = target == TargetKind.Application ? applicationPath : null;
        if (target == TargetKind.Application && string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Aucune application ciblee.");

        _engine.SetTarget(path);
        IsArmed = true;
        return Task.CompletedTask;
    }

    public void SetBlocked(bool blocked, CutDirection direction)
    {
        if (!IsArmed) return;

        try
        {
            _engine.SetBlocked(
                blocked,
                outbound: direction is CutDirection.Both or CutDirection.Outbound,
                inbound: direction is CutDirection.Both or CutDirection.Inbound);
        }
        catch
        {
            // La session dynamique retirera les filtres quoi qu'il arrive : on ne laisse
            // jamais une exception ici empecher la suite du motif.
        }
    }

    public Task CleanupAsync()
    {
        IsArmed = false;
        _engine.Close();
        return Task.CompletedTask;
    }

    public void CleanupBlocking(TimeSpan timeout)
    {
        IsArmed = false;
        _engine.Close();
    }

    public void Dispose() => _engine.Dispose();
}
