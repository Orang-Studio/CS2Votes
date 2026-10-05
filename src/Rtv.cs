using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace CS2Votes;

// Rock the vote. When it passes, the match ends
public sealed partial class CS2Votes
{
    private readonly HashSet<ulong> _rtvVoters = [];
    private bool _rtvPassed;
    private int? _savedMaxRounds;
    private void ResetRtv()
    {
        _rtvVoters.Clear();
        _rtvPassed = false;
    }
    private int RtvNeeded(int humans) => Math.Max(1, (int)Math.Ceiling(humans * Config.Rtv.Percent));
    private bool MatchIsEnding => _changing || _intermissionHandled;
    private void DoRtv(CCSPlayerController player)
    {
        if (!Config.Rtv.Enabled)
        {
            Reply(player, "rtv.disabled");
            return;
        }
        if (MatchIsEnding)
        {
            Reply(player, "rtv.too_late");
            return;
        }
        if (_rtvPassed)
        {
            Reply(player, "rtv.already_passed");
            return;
        }
        if (IsWarmup)
        {
            Reply(player, "rtv.warmup");
            return;
        }

        var humans = Humans().Count;
        if (humans < Config.Rtv.MinPlayers)
        {
            Reply(player, "rtv.min_players", Config.Rtv.MinPlayers);
            return;
        }

        var wait = Config.Rtv.AllowAfterSeconds - SecondsSinceMapStart;
        if (wait > 0)
        {
            Reply(player, "rtv.wait", wait);
            return;
        }

        var needed = RtvNeeded(humans);
        if (!_rtvVoters.Add(player.SteamID))
        {
            Reply(player, "rtv.already", _rtvVoters.Count, needed);
            return;
        }

        Broadcast("rtv.voted", player.PlayerName, _rtvVoters.Count, needed);
        CheckRtvThreshold();
    }

    private void DoUnRtv(CCSPlayerController player)
    {
        if (_rtvPassed || MatchIsEnding)
        {
            Reply(player, "rtv.too_late");
            return;
        }
        if (!_rtvVoters.Remove(player.SteamID))
        {
            Reply(player, "rtv.not_voted");
            return;
        }
        Broadcast("rtv.removed", player.PlayerName, _rtvVoters.Count, RtvNeeded(Humans().Count));
    }

    private void CheckRtvThreshold()
    {
        if (_rtvPassed || MatchIsEnding || _rtvVoters.Count == 0)
            return;

        // only count voters that are still here, no lefties
        var here = Humans().Select(p => p.SteamID).ToHashSet();
        _rtvVoters.IntersectWith(here);

        if (_rtvVoters.Count >= RtvNeeded(here.Count))
            RtvPass(forcedBy: null);
    }

    private void RtvPass(string? forcedBy)
    {
        _rtvPassed = true;
        var now = forcedBy != null || Config.Rtv.Mode.Equals("Now", StringComparison.OrdinalIgnoreCase);

        if (forcedBy != null)
            Broadcast("rtv.forced", forcedBy);
        else
            Broadcast(now ? "rtv.passed_now" : "rtv.passed_round");

        EndMatch(now);
    }

    private void EndMatch(bool now)
    {
        var rules = GameRules;
        if (rules == null)
        {
            Logger.LogError("No game rules entity; cannot end the match.");
            return;
        }

        // The match ends when the number of played rounds reaches mp_maxrounds.
        _savedMaxRounds ??= MaxRounds;
        var target = RoundsPlayed + 1;
        Server.ExecuteCommand($"mp_maxrounds {target}");
        Logger.LogInformation("Match end requested ({Mode}): mp_maxrounds {Old} -> {New}",
            now ? "now" : "after this round", _savedMaxRounds, target);

        if (now)
            EndMatchNow(rules);
    }

    /// <summary>
    /// ends the current round at once, so that the match ends.
    /// we let the round clock end, the engine then ends the round
    /// the normal way (mp_default_team_winner_no_objective) and the mode script accepts it.
    /// </summary>
    private void EndMatchNow(CCSGameRules rules)
    {
        var elapsed = Server.CurrentTime - rules.RoundStartTime;
        var roundTime = Math.Max(1, (int)Math.Ceiling(elapsed) + 1);
        Logger.LogInformation("Round clock set to {New} s (was {Old} s, {Elapsed:0} s played).",
            roundTime, rules.RoundTime, elapsed);
        rules.RoundTime = roundTime;
        if (_gameRulesProxy is { IsValid: true })
            Utilities.SetStateChanged(_gameRulesProxy, "CCSGameRulesProxy", "m_pGameRules");

        // if the round is still running after the clock ended
        var roundStart = rules.RoundStartTime;
        AddTimer(4.0f, () =>
        {
            var r = GameRules;
            if (r == null || _intermissionHandled || r.RoundStartTime != roundStart)
                return;
            Logger.LogWarning("The round did not end from the clock; calling TerminateRound.");
            r.TerminateRound(3f, RoundEndReason.TerroristsWin);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void RestoreMaxRounds()
    {
        if (_savedMaxRounds is not { } value)
            return;
        _savedMaxRounds = null;
        var current = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>();
        if (current != value)
            Server.ExecuteCommand($"mp_maxrounds {value}");
}   }