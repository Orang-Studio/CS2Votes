using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2MenuManager.API.Class;
using CS2MenuManager.API.Menu;
using Microsoft.Extensions.Logging;

namespace CS2Votes;
public sealed partial class CS2Votes
{
    private DateTime _voteMapCooldownUntil = DateTime.MinValue;
    private bool _ownsPanoramaVote;
    private void DoVoteMap(CCSPlayerController player, string query)
    {
        if (!Config.VoteMap.Enabled)
        {
            Reply(player, "votemap.disabled");
            return;
        }
        if (MatchIsEnding)
        {
            Reply(player, "rtv.too_late");
            return;
        }
        if (VoteManager.IsVoteActive)
        {
            Reply(player, "votemap.busy");
            return;
        }

        var cooldown = (int)Math.Ceiling((_voteMapCooldownUntil - DateTime.UtcNow).TotalSeconds);
        if (cooldown > 0)
        {
            Reply(player, "votemap.cooldown", cooldown);
            return;
        }

        var wait = Config.VoteMap.AllowAfterSeconds - SecondsSinceMapStart;
        if (wait > 0)
        {
            Reply(player, "votemap.wait", wait);
            return;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            ShowVoteMapMenu(player);
            return;
        }

        var matches = FindMaps(query);
        if (matches.Count == 0)
        {
            Reply(player, "map.not_found", query);
            ShowMapList(player);
            return;
        }
        if (matches.Count > 1)
        {
            Reply(player, "map.ambiguous", string.Join(", ", matches.Select(m => m.Name)));
            return;
        }

        var map = matches[0];
        if (map.Id == _currentMap?.Id)
        {
            Reply(player, "votemap.current", map.Name);
            return;
        }

        StartVoteMap(player, map);
    }

    private void ShowVoteMapMenu(CCSPlayerController player)
    {
        var menu = new HudMenu(T(player, "menu.votemap_title"), DateTime.UtcNow.AddSeconds(Config.VoteMap.MenuSeconds));
        foreach (var map in Config.Maps)
        {
            var isCurrent = map.Id == _currentMap?.Id;
            var label = isCurrent ? $"{map.Name} {T(player, "menu.current_suffix")}" : map.Name;
            var target = map;
            menu.Items.Add(new HudMenuItem(label, !isCurrent, p =>
            {
                CloseHudMenu(p);
                DoVoteMap(p, target.Id.ToString());
            }));
        }
        OpenHudMenu(player, menu);
    }

    private void StartVoteMap(CCSPlayerController caller, MapEntry map)
    {
        _voteMapCooldownUntil = DateTime.UtcNow.AddSeconds(Config.VoteMap.CooldownSeconds);
        var details = Lang.Strip(_lang.Get(Config.DefaultLanguage, "votemap.panel", map.Name));
        var vote = new PanoramaVote("#SFUI_vote_panorama_vote_default", details,
            info => VoteMapResult(info, map),
            (_, _, _) => { },
            this)
        {
            VoteCaller = caller,
        };
        _ownsPanoramaVote = true;
        vote.DisplayVoteToAll(Config.VoteMap.VoteSeconds);
        Broadcast("votemap.started", caller.PlayerName, map.Name);
        Logger.LogInformation("{Caller} started votemap for {Map} ({Id})", caller.PlayerName, map.Name, map.Id);
    }

    private bool VoteMapResult(YesNoVoteInfo info, MapEntry map)
    {
        _ownsPanoramaVote = false;
        var needed = Math.Max(1, (int)Math.Ceiling(info.TotalClients * Config.VoteMap.Percent));
        var passed = info.YesVotes > info.NoVotes && info.YesVotes >= needed;
        Server.NextFrame(() =>
        {
            if (!passed)
            {
                Broadcast("votemap.failed", map.Name, info.YesVotes, info.NoVotes, needed);
                return;
            }
            if (MatchIsEnding)
                return;

            Broadcast("votemap.passed", map.Name, info.YesVotes, info.NoVotes);
            BroadcastCenter("center.nextmap", map.Name);
            ScheduleMapChange(map, Config.VoteMap.ChangeDelaySeconds, "votemap");
        });
        return passed;
    }

    private void CancelPanoramaVote()
    {
        if (!_ownsPanoramaVote)
            return;
        _ownsPanoramaVote = false;
        try
        {
            if (VoteManager.IsVoteActive)
                VoteManager.CancelActiveVote();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "CancelActiveVote failed");
}   }   }