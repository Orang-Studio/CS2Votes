using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
namespace CS2Votes;

// end-of-match vote with native UI engine shows its map vote panel after scoreboard.
public sealed partial class CS2Votes
{
    private bool _intermissionHandled;
    private bool _voteActive;
    private bool _nativeVote;
    private bool _selectingSeen;
    private bool _finishScheduled;
    private DateTime _voteDeadline;
    private List<MapEntry> _voteOptions = [];
    private readonly Dictionary<ulong, int> _menuVotes = [];
    private readonly Dictionary<ulong, int> _seenNativeVotes = [];
    private CounterStrikeSharp.API.Modules.Timers.Timer? _votePollTimer;

    private void ResetEndMatchVote()
    {
        _intermissionHandled = false;
        _voteActive = false;
        _selectingSeen = false;
        _finishScheduled = false;
        _voteOptions = [];
        _menuVotes.Clear();
        _seenNativeVotes.Clear();
        _votePollTimer?.Kill();
        _votePollTimer = null;
    }

    private HookResult OnIntermission(EventCsIntermission @event, GameEventInfo info)
    {
        if (_intermissionHandled || Config.Maps.Count == 0)
            return HookResult.Continue;
        _intermissionHandled = true;
        CancelPanoramaVote();
        CloseAllHudMenus();
        Logger.LogInformation("Match end. Engine vote winner before the vote: {Winner}", GameRules?.EndMatchMapVoteWinner);

        if (_changing)
            return HookResult.Continue;

        if (_nextMap != null)
        {
            Broadcast("endmatch.nextmap_set", _nextMap.Name);
            ScheduleMapChange(_nextMap, Config.EndMatchVote.ChangeDelaySeconds + 4, "next map was set");
            return HookResult.Continue;
        }
        var options = BuildVoteOptions();
        if (!Config.EndMatchVote.Enabled || options.Count == 1)
        {
            var map = options[Random.Shared.Next(options.Count)];
            Broadcast("endmatch.nextmap_set", map.Name);
            ScheduleMapChange(map, Config.EndMatchVote.ChangeDelaySeconds + 4, "rotation (vote disabled)");
            return HookResult.Continue;
        }
        StartEndMatchVote(options);
        return HookResult.Continue;
    }

    // pool without recently played maps. Never fewer than two options if the pool allows it.
    private List<MapEntry> BuildVoteOptions()
    {
        var excluded = _state.History.Take(Config.EndMatchVote.ExcludeRecentMaps).ToHashSet();
        if (_currentMap != null && Config.EndMatchVote.ExcludeRecentMaps > 0)
            excluded.Add(_currentMap.Id);

        var pool = Config.Maps.Where(m => !excluded.Contains(m.Id)).ToList();
        if (pool.Count < 2)
            pool = Config.Maps.Where(m => m.Id != _currentMap?.Id).ToList();
        if (pool.Count == 0)
            pool = [.. Config.Maps];

        // random subset when the pool is larger than the panel
        return pool
            .OrderBy(_ => Random.Shared.Next())
            .Take(Config.EndMatchVote.MaxOptions)
            .OrderBy(m => Config.Maps.IndexOf(m))
            .ToList();
    }

    private void StartEndMatchVote(List<MapEntry> options)
    {
        _voteOptions = options;
        _voteActive = true;
        _nativeVote = UseNativeUi && FillNativeOptions(options);

        var seconds = Config.EndMatchVote.VoteSeconds;
        if (_nativeVote)
        {
            // engine shows panel after the scoreboard; its own selecting map event, normally ends the vote.
            _voteDeadline = DateTime.UtcNow.AddSeconds(seconds + 12);
            Broadcast("endmatch.started_native", seconds);
        }
        else
        {
            _voteDeadline = DateTime.UtcNow.AddSeconds(seconds);
            foreach (var player in Humans())
                ShowEndMatchMenu(player);
            Broadcast("endmatch.started_menu", seconds);
        }

        Logger.LogInformation("End-of-match vote ({Ui}): {Maps}", _nativeVote ? "native" : "menu",
            string.Join(", ", options.Select(m => $"{m.Name}#{MapGroupIndex(m)}")));

        _votePollTimer = AddTimer(1f, PollEndMatchVote, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private bool FillNativeOptions(List<MapEntry> options)
    {
        var rules = GameRules;
        if (rules == null)
        {
            Logger.LogWarning("No game rules entity; using the menu vote.");
            return false;
        }

        var indices = options.Select(MapGroupIndex).ToList();
        var missing = options.Where((_, i) => indices[i] < 0).Select(m => m.Name).ToList();
        if (missing.Count > 0)
        {
            Logger.LogWarning("Not in mapgroup {Group}: {Maps}. Using the menu vote.", Config.MapGroup, string.Join(", ", missing));
            return false;
        }

        WriteNativeOptions(rules, indices);
        // engine can fill arrays itself at intermission.
        AddTimer(1f, () => ReapplyNativeOptions(indices), TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(3f, () => ReapplyNativeOptions(indices), TimerFlags.STOP_ON_MAPCHANGE);
        return true;
    }

    private void ReapplyNativeOptions(List<int> indices)
    {
        if (_voteActive && _nativeVote && GameRules is { } rules)
            WriteNativeOptions(rules, indices);
    }

    private void WriteNativeOptions(CCSGameRules rules, List<int> indices)
    {
        var types = rules.EndMatchMapGroupVoteTypes;
        var opts = rules.EndMatchMapGroupVoteOptions;
        for (var slot = 0; slot < opts.Length; slot++)
        {
            var index = slot < indices.Count ? indices[slot] : -1;
            opts[slot] = index;
            if (slot < types.Length)
                types[slot] = index >= 0 ? 0 : -1;
        }
        try
        {
            if (_gameRulesProxy is { IsValid: true })
                Utilities.SetStateChanged(_gameRulesProxy, "CCSGameRulesProxy", "m_pGameRules");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "SetStateChanged on game rules failed");
        }
    }
    private void ShowEndMatchMenu(CCSPlayerController player)
    {
        var menu = new HudMenu(T(player, "menu.endmatch_title"), _voteDeadline);
        for (var i = 0; i < _voteOptions.Count; i++)
        {
            var slot = i;
            var map = _voteOptions[i];
            menu.Items.Add(new HudMenuItem(map.Name, true, p =>
            {
                if (!_voteActive || _nativeVote)
                    return;
                var changed = !_menuVotes.TryGetValue(p.SteamID, out var old) || old != slot;
                _menuVotes[p.SteamID] = slot;
                // menu stays open until the vote ends
                menu.Marked = slot;
                menu.Dirty = true;
                if (changed)
                    Broadcast("endmatch.player_voted", p.PlayerName, map.Name);
                if (AllHumansVoted())
                    ScheduleFinish(1.5f);
            }));
        }
        OpenHudMenu(player, menu);
    }

    // slot (0-based) the player voted for, or -1
    private int VoteSlotOf(CCSPlayerController player)
    {
        int slot;
        if (_nativeVote)
            slot = player.EndMatchNextMapVote;
        else if (!_menuVotes.TryGetValue(player.SteamID, out slot))
            slot = -1;
        return slot >= 0 && slot < _voteOptions.Count ? slot : -1;
    }

    private bool AllHumansVoted()
    {
        var humans = Humans();
        return humans.Count > 0 && humans.All(p => VoteSlotOf(p) >= 0);
    }

    private void PollEndMatchVote()
    {
        if (!_voteActive)
        {
            _votePollTimer?.Kill();
            _votePollTimer = null;
            return;
        }

        if (_nativeVote)
        {
            // tell chat who voted for what, like good old RTV plugins did!
            foreach (var player in Humans())
            {
                var slot = VoteSlotOf(player);
                if (slot < 0 || (_seenNativeVotes.TryGetValue(player.SteamID, out var old) && old == slot))
                    continue;
                _seenNativeVotes[player.SteamID] = slot;
                Broadcast("endmatch.player_voted", player.PlayerName, _voteOptions[slot].Name);
            }
        }

        if (AllHumansVoted())
            ScheduleFinish(2f);
        else if (DateTime.UtcNow >= _voteDeadline)
            ScheduleFinish(0.1f);
    }

    private HookResult OnSelectingMap(EventEndmatchMapvoteSelectingMap @event, GameEventInfo info)
    {
        _selectingSeen = true;
        Logger.LogInformation("Engine selecting map: count={Count} slots={Slots} winner={Winner}",
            @event.Count,
            string.Join(",", new[] { @event.Slot1, @event.Slot2, @event.Slot3, @event.Slot4, @event.Slot5,
                @event.Slot6, @event.Slot7, @event.Slot8, @event.Slot9, @event.Slot10 }),
            GameRules?.EndMatchMapVoteWinner);
        if (_voteActive)
            ScheduleFinish(0.2f);
        return HookResult.Continue;
    }

    private void ScheduleFinish(float delay)
    {
        if (_finishScheduled || !_voteActive)
            return;
        _finishScheduled = true;
        AddTimer(delay, () => FinishEndMatchVote(0), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void FinishEndMatchVote(int attempt)
    {
        if (!_voteActive)
            return;

        var counts = new int[_voteOptions.Count];
        foreach (var player in Humans())
        {
            var slot = VoteSlotOf(player);
            if (slot >= 0)
                counts[slot]++;
        }
        var total = counts.Sum();
        var top = counts.Max();
        var tied = Enumerable.Range(0, counts.Length).Where(i => counts[i] == top).ToList();
        int winner;
        if (tied.Count == 1)
        {
            winner = tied[0];
        }
        else
        {
            // engine plays its roulette and shows winner
            var engineWinner = GameRules?.EndMatchMapVoteWinner ?? -1;
            if (_nativeVote && _selectingSeen && engineWinner < 0 && attempt < 12)
            {
                AddTimer(0.5f, () => FinishEndMatchVote(attempt + 1), TimerFlags.STOP_ON_MAPCHANGE);
                return;
            }
            winner = _nativeVote && _selectingSeen && tied.Contains(engineWinner)
                ? engineWinner
                : tied[Random.Shared.Next(tied.Count)];
        }

        _voteActive = false;
        _votePollTimer?.Kill();
        _votePollTimer = null;
        CloseAllHudMenus();
        var map = _voteOptions[winner];
        Logger.LogInformation("Vote result: {Result}; engine winner {Engine}; chosen {Map}",
            string.Join(", ", _voteOptions.Select((m, i) => $"{m.Name}={counts[i]}")),
            GameRules?.EndMatchMapVoteWinner, map.Name);
        if (total == 0)
            Broadcast("endmatch.no_votes", map.Name);
        else if (tied.Count > 1)
            Broadcast("endmatch.tie", map.Name, top);
        else
            Broadcast("endmatch.result", map.Name, top, total);
        BroadcastCenter("center.nextmap", map.Name);
        ScheduleMapChange(map, Config.EndMatchVote.ChangeDelaySeconds, "end-of-match vote");
    }
}