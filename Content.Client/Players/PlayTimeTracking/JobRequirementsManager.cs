using Content.Shared.CCVar;
using Content.Shared.Players;
using Content.Shared.Players.JobWhitelist;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Client;
using Robust.Client.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.Players.PlayTimeTracking;

public sealed partial class JobRequirementsManager : ISharedPlaytimeManager
{
    [Dependency] private readonly IBaseClient _client = default!;
    [Dependency] private readonly IClientNetManager _net = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IEntityManager _entManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    private readonly Dictionary<string, TimeSpan> _roles = new();
    private readonly List<string> _roleBans = new();
    private readonly List<string> _jobWhitelists = new();

    private ISawmill _sawmill = default!;

    public event Action? Updated;

    public void Initialize()
    {
        _sawmill = Logger.GetSawmill("job_requirements");

        // Yeah the client manager handles role bans and playtime but the server ones are separate DEAL.
        _net.RegisterNetMessage<MsgRoleBans>(RxRoleBans);
        _net.RegisterNetMessage<MsgPlayTime>(RxPlayTime);
        _net.RegisterNetMessage<MsgJobWhitelist>(RxJobWhitelist);
        _net.RegisterNetMessage<MsgWhitelist>(RxWhitelist);

        _client.RunLevelChanged += ClientOnRunLevelChanged;
    }

    private void ClientOnRunLevelChanged(object? sender, RunLevelChangedEventArgs e)
    {
        if (e.NewLevel == ClientRunLevel.Initialize)
        {
            // Reset on disconnect, just in case.
            _roles.Clear();
        }
    }

    private void RxRoleBans(MsgRoleBans message)
    {
        _sawmill.Debug($"Received roleban info containing {message.Bans.Count} entries.");

        if (_roleBans.Equals(message.Bans))
            return;

        _roleBans.Clear();
        _roleBans.AddRange(message.Bans);
        Updated?.Invoke();
    }

    private void RxPlayTime(MsgPlayTime message)
    {
        _roles.Clear();

        // NOTE: do not assign _roles = message.Trackers due to implicit data sharing in integration tests.
        foreach (var (tracker, time) in message.Trackers)
        {
            _roles[tracker] = time;
        }

        /*var sawmill = Logger.GetSawmill("play_time");
        foreach (var (tracker, time) in _roles)
        {
            sawmill.Info($"{tracker}: {time}");
        }*/
        Updated?.Invoke();
    }

    private void RxJobWhitelist(MsgJobWhitelist message)
    {
        _jobWhitelists.Clear();
        _jobWhitelists.AddRange(message.Whitelist);
        Updated?.Invoke();
    }

    public bool IsAllowed(JobPrototype job, HumanoidCharacterProfile? profile, out FormattedMessage details)
    {
        if (_roleBans.Contains($"Job:{job.ID}"))
        {
            details = FormattedMessage.FromMarkupPermissive(Loc.GetString("role-ban"));
            return false;
        }

        details = FormattedMessage.Empty;
        var player = _playerManager.LocalSession;
        if (player == null)
            return true;

        return CheckRoleRequirements(job, profile, out details);
    }

    public bool CheckRoleRequirements(JobPrototype job, HumanoidCharacterProfile? profile, out FormattedMessage details)
    {
        var reqs = _entManager.System<SharedRoleSystem>().GetJobRequirement(job);
        return CheckRoleRequirements(reqs, profile, out details);
    }

    public bool CheckRoleRequirements(HashSet<JobRequirement>? requirements, HumanoidCharacterProfile? profile, out FormattedMessage details)
    {
        details = new FormattedMessage();

        if (requirements == null)
            return true;

        var roleTimersMultiplier = _cfg.GetCVar(CCVars.GameRoleTimers) ? _cfg.GetCVar(CCVars.GameRoleTimersMultiplier) : 0f;

        var success = true;
        foreach (var requirement in requirements)
        {
            success = requirement.Check(_entManager,
                _prototypes,
                profile,
                _roles,
                out var checkDetails,
                roleTimersMultiplier,
                _whitelisted) && success;

            if (!details.IsEmpty)
                details.PushNewline();
            details.AddMessage(checkDetails);
        }

        return success;
    }

    public bool CheckWhitelist(JobPrototype job, out FormattedMessage details)
    {
        details = FormattedMessage.FromMarkupPermissive(Loc.GetString("role-whitelisted"));

        if (!_cfg.GetCVar(CCVars.GameRoleWhitelist))
            return true;

        // DeltaV - blanket whitelist check in client
        if (_whitelisted)
            return true;

        if (job.Whitelisted && !_jobWhitelists.Contains(job.ID))
        {
            details = FormattedMessage.FromMarkupPermissive(Loc.GetString("role-not-whitelisted"));
            return false;
        }

        return true;
    }

    public TimeSpan FetchOverallPlaytime()
    {
        return _roles.TryGetValue("Overall", out var overallPlaytime) ? overallPlaytime : TimeSpan.Zero;
    }

    public IEnumerable<KeyValuePair<string, TimeSpan>> FetchPlaytimeByRoles()
    {
        var jobsToMap = _prototypes.EnumeratePrototypes<JobPrototype>();

        foreach (var job in jobsToMap)
        {
            if (_roles.TryGetValue(job.PlayTimeTracker, out var locJobName))
            {
                yield return new KeyValuePair<string, TimeSpan>(job.Name, locJobName);
            }
        }
    }

    public IReadOnlyDictionary<string, TimeSpan> GetPlayTimes(ICommonSession session)
    {
        if (session != _playerManager.LocalSession)
        {
            return new Dictionary<string, TimeSpan>();
        }

        return _roles;
    }
}
