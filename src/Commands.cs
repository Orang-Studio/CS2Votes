using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace CS2Votes;

public sealed partial class CS2Votes
{
    private const string AdminFlag = "@css/changemap";

    // player commands

    [ConsoleCommand("css_rtv", "Vote to change the map")]
    [ConsoleCommand("css_rockthevote", "Vote to change the map")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdRtv(CCSPlayerController? player, CommandInfo info)
    {
        if (IsHuman(player))
            DoRtv(player!);
    }
    [ConsoleCommand("css_unrtv", "Take back your RTV vote")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdUnRtv(CCSPlayerController? player, CommandInfo info)
    {
        if (IsHuman(player))
            DoUnRtv(player!);
    }
    [ConsoleCommand("css_votemap", "Start a vote to change to a map now")]
    [ConsoleCommand("css_vmap", "Start a vote to change to a map now")]
    [CommandHelper(usage: "[map]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdVoteMap(CCSPlayerController? player, CommandInfo info)
    {
        if (IsHuman(player))
            DoVoteMap(player!, info.ArgString.Trim().Trim('"'));
    }
    [ConsoleCommand("css_nextmap", "Show the next map")]
    public void CmdNextMap(CCSPlayerController? player, CommandInfo info) => ShowNextMap(player);
    [ConsoleCommand("css_currentmap", "Show the current map")]
    public void CmdCurrentMap(CCSPlayerController? player, CommandInfo info) => ShowCurrentMap(player);
    [ConsoleCommand("css_timeleft", "Show the rounds left on this map")]
    public void CmdTimeLeft(CCSPlayerController? player, CommandInfo info) => ShowTimeLeft(player);
    [ConsoleCommand("css_maps", "Show the map pool")]
    [ConsoleCommand("css_maplist", "Show the map pool")]
    public void CmdMaps(CCSPlayerController? player, CommandInfo info) => ShowMapList(player);
    private void ShowNextMap(CCSPlayerController? player)
    {
        if (_nextMap != null)
            Reply(player, "nextmap.set", _nextMap.Name);
        else if (_voteActive)
            Reply(player, "nextmap.voting");
        else
            Reply(player, "nextmap.vote");
    }

    private void ShowCurrentMap(CCSPlayerController? player)
    {
        if (_currentMap != null)
            Reply(player, "currentmap", _currentMap.Name, _currentMap.Id);
        else
            Reply(player, "currentmap.unknown", Server.MapName);
    }

    private void ShowTimeLeft(CCSPlayerController? player)
    {
        if (MatchIsEnding)
        {
            Reply(player, "timeleft.ending");
            return;
        }
        if (IsWarmup)
        {
            Reply(player, "timeleft.warmup");
            return;
        }
        var max = MaxRounds;
        if (max <= 0)
        {
            Reply(player, "timeleft.none");
            return;
        }
        var left = Math.Max(1, max - RoundsPlayed);
        Reply(player, left == 1 ? "timeleft.last" : "timeleft.rounds", left);
    }
    private void ShowMapList(CCSPlayerController? player)
    {
        var names = Config.Maps.Select(m => m.Id == _currentMap?.Id
            ? $"{ChatColors.Green}{m.Name}{ChatColors.Default}"
            : m.Name);
        Reply(player, "maps.list", string.Join(", ", names));
    }

    // admin commands
    [ConsoleCommand("css_setnextmap", "Set the next map (skips the end-of-match vote)")]
    [CommandHelper(minArgs: 1, usage: "<map|workshop id>")]
    [RequiresPermissions(AdminFlag)]
    public void CmdSetNextMap(CCSPlayerController? player, CommandInfo info)
    {
        var map = ResolveAdminMap(player, info.ArgString.Trim().Trim('"'));
        if (map == null)
            return;
        if (_changing)
        {
            Reply(player, "admin.already_changing");
            return;
        }

        _nextMap = map;
        ApplyServerSettings();
        Broadcast("admin.nextmap_set", AdminName(player), map.Name);
        Logger.LogInformation("{Admin} set the next map to {Map} ({Id})", AdminName(player), map.Name, map.Id);

        // during the end-of-match vote this decides vote
        if (_voteActive)
        {
            _voteActive = false;
            ScheduleMapChange(map, Config.EndMatchVote.ChangeDelaySeconds, "admin set next map during vote");
        }
    }

    [ConsoleCommand("css_forcemap", "Change to a map now, by name or workshop ID")]
    [CommandHelper(minArgs: 1, usage: "<map|workshop id>")]
    [RequiresPermissions(AdminFlag)]
    public void CmdForceMap(CCSPlayerController? player, CommandInfo info)
    {
        var map = ResolveAdminMap(player, info.ArgString.Trim().Trim('"'));
        if (map == null)
            return;

        StopPendingChange();
        CancelPanoramaVote();
        _voteActive = false;
        Broadcast("admin.forcemap", AdminName(player), map.Name);
        BroadcastCenter("center.nextmap", map.Name);
        ScheduleMapChange(map, 3f, $"forced by {AdminName(player)}");
    }

    [ConsoleCommand("css_forcevote", "End the match now and start the map vote")]
    [ConsoleCommand("css_forcertv", "End the match now and start the map vote")]
    [RequiresPermissions(AdminFlag)]
    public void CmdForceVote(CCSPlayerController? player, CommandInfo info)
    {
        if (MatchIsEnding)
        {
            Reply(player, "rtv.too_late");
            return;
        }
        if (IsWarmup)
        {
            Server.ExecuteCommand("mp_warmup_end");
            Reply(player, "admin.warmup_ended");
            return;
        }
        RtvPass(AdminName(player));
    }
    [ConsoleCommand("css_cancelvote", "Cancel map votes, RTV and a pending map change")]
    [RequiresPermissions(AdminFlag)]
    public void CmdCancelVote(CCSPlayerController? player, CommandInfo info)
    {
        CancelPanoramaVote();
        _rtvVoters.Clear();
        if (!_intermissionHandled)
        {
            _rtvPassed = false;
            RestoreMaxRounds();
        }
        if (!_intermissionHandled)
            StopPendingChange();
        _nextMap = _changing ? _nextMap : null;
        ApplyServerSettings();
        Broadcast("admin.cancelled", AdminName(player));
    }
    [ConsoleCommand("css_mapvote_status", "Print the map vote state (debug)")]
    [RequiresPermissions(AdminFlag)]
    public void CmdStatus(CCSPlayerController? player, CommandInfo info)
    {
        var rules = GameRules;
        var lines = new List<string>
        {
            $"CS2Votes {ModuleVersion} | engine map {Server.MapName} | current {_currentMap?.Name ?? "?"} ({_currentMap?.Id}) | ui {(UseNativeUi ? "native" : "menu")}{(_mapGroupStale ? " (mapgroup file changed, restart needed)" : "")}",
            $"next {_nextMap?.Name ?? "-"} | changing {_changing} | rtv {_rtvVoters.Count}/{RtvNeeded(Humans().Count)} passed {_rtvPassed} | humans {Humans().Count}",
            $"rounds {RoundsPlayed}/{MaxRounds} warmup {IsWarmup} | intermission {_intermissionHandled} vote {_voteActive} native {_nativeVote} selecting {_selectingSeen}",
            $"history {string.Join(",", _state.History.Take(5))} | pending {_state.PendingId}",
        };
        if (rules != null)
        {
            var opts = new List<int>();
            foreach (var o in rules.EndMatchMapGroupVoteOptions)
                opts.Add(o);
            var types = new List<int>();
            foreach (var t in rules.EndMatchMapGroupVoteTypes)
                types.Add(t);
            lines.Add($"engine options [{string.Join(",", opts)}] types [{string.Join(",", types)}] winner {rules.EndMatchMapVoteWinner} | mapgroup {Config.MapGroup}");
        }
        if (_voteOptions.Count > 0)
            lines.Add("vote options: " + string.Join(", ", _voteOptions.Select((m, i) => $"{i}:{m.Name}")));
        foreach (var p in Humans())
            lines.Add($"  {p.PlayerName}: endmatch vote {p.EndMatchNextMapVote}");

        foreach (var line in lines)
            info.ReplyToCommand(line);
    }

    // helpers
    private MapEntry? ResolveAdminMap(CCSPlayerController? player, string query)
    {
        var matches = FindMaps(query);
        if (matches.Count == 1)
            return matches[0];
        if (matches.Count > 1)
        {
            Reply(player, "map.ambiguous", string.Join(", ", matches.Select(m => m.Name)));
            return null;
        }
        // admins can load any workshop map by its ID, also one that is not in the pool
        if (ulong.TryParse(query, out var id) && id > 0)
            return new MapEntry { Id = id, Name = id.ToString() };
        Reply(player, "map.not_found", query);
        ShowMapList(player);
        return null;
    }

    private void StopPendingChange()
    {
        _changeTimer?.Kill();
        _changeTimer = null;
        _changing = false;
        _state.PendingId = 0;
    }
    private static string AdminName(CCSPlayerController? player) =>
        player is { IsValid: true } ? player.PlayerName : "Console";
}