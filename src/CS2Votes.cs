using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace CS2Votes;

[MinimumApiVersion(300)]
public sealed partial class CS2Votes : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "CS2Votes";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "OrangStudio";
    public override string ModuleDescription => "Workshop map voting RTV";
    public PluginConfig Config { get; set; } = new();
    private Lang _lang = null!;
    private PersistentState _state = new();
    private string _statePath = "";
    /// the map that runs now, or null when it is not in the pool
    private MapEntry? _currentMap;
    // next map chosen by an admin or by !votemap. It skips the end-of-match vote
    private MapEntry? _nextMap;
    // true when gamemodes_server.txt changed after the server started
    private bool _mapGroupStale;
    private DateTime _mapStartedAt = DateTime.UtcNow;
    private bool _changing;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _changeTimer;

    //lifecycle
    public void OnConfigParsed(PluginConfig config)
    {
        var seen = new HashSet<ulong>();
        config.Maps = config.Maps
            .Where(m => m.Id != 0 && seen.Add(m.Id))
            .Select(m =>
            {
                m.Name = string.IsNullOrWhiteSpace(m.Name) ? m.Id.ToString() : m.Name.Trim();
                m.MapName = m.MapName.Trim();
                return m;
            })
            .ToList();

        config.EndMatchVote.VoteSeconds = Math.Clamp(config.EndMatchVote.VoteSeconds, 5, 120);
        config.EndMatchVote.ChangeDelaySeconds = Math.Clamp(config.EndMatchVote.ChangeDelaySeconds, 1, 60);
        config.EndMatchVote.MaxOptions = Math.Clamp(config.EndMatchVote.MaxOptions, 2, 10);
        config.EndMatchVote.ExcludeRecentMaps = Math.Max(0, config.EndMatchVote.ExcludeRecentMaps);
        config.Rtv.Percent = Math.Clamp(config.Rtv.Percent, 0.01f, 1f);
        config.Rtv.MinPlayers = Math.Max(1, config.Rtv.MinPlayers);
        config.VoteMap.Percent = Math.Clamp(config.VoteMap.Percent, 0.01f, 1f);
        config.VoteMap.VoteSeconds = Math.Clamp(config.VoteMap.VoteSeconds, 5, 60);
        config.VoteMap.MenuSeconds = Math.Clamp(config.VoteMap.MenuSeconds, 5, 120);
        config.Hud.LiftLines = Math.Clamp(config.Hud.LiftLines, 0, 12);
        config.MapGroupMaps = config.MapGroupMaps.Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
        if (config.MapGroupMaps.Count > 0)
        {
            foreach (var map in config.Maps.Where(m => !config.MapGroupMaps.Contains(m.MapName, StringComparer.OrdinalIgnoreCase)))
                Logger.LogWarning("{Map} ({MapName}) is not in MapGroupMaps. The native panel cannot show it", map.Name, map.MapName);
        }

        if (config.Maps.Count == 0)
            Logger.LogError("No maps in config. Add IDs to \"Maps\".");
        if (config.Version < 2)
            Logger.LogWarning("Config version {Version} is old", config.Version);

        Config = config;
    }

    public override void Load(bool hotReload)
    {
        _lang = new Lang(Path.Combine(ModuleDirectory, "lang"), Config.DefaultLanguage);
        _statePath = Path.Combine(ModuleDirectory, "state.json");
        _state = PersistentState.Load(_statePath);
        WriteMapGroupFile();
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnTick>(OnTickHudMenus);
        RegisterEventHandler<EventCsIntermission>(OnIntermission, HookMode.Pre);
        RegisterEventHandler<EventEndmatchMapvoteSelectingMap>(OnSelectingMap);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        AddCommandListener("say", OnSay);
        AddCommandListener("say_team", OnSay);
        if (hotReload)
        {
            _currentMap = ResolveCurrentMap();
            ApplyServerSettings();
        }

        Logger.LogInformation("Loaded: {Maps} maps, {Langs} languages, mapgroup {Group}, UI {Ui}.",
            Config.Maps.Count, _lang.Count, Config.MapGroup, UseNativeUi ? "native panel" : "menu");
    }

    public override void Unload(bool hotReload)
    {
        CloseAllHudMenus();
        CancelPanoramaVote();
    }

    private void OnMapStart(string mapName)
    {
        _changing = false;
        _changeTimer = null;
        _nextMap = null;
        _mapStartedAt = DateTime.UtcNow;
        ResetRtv();
        ResetEndMatchVote();
        _voteMapCooldownUntil = DateTime.MinValue;
        _gameRules = null;

        RestoreMaxRounds();

        _currentMap = ResolveCurrentMap();
        if (_currentMap != null)
        {
            _state.History.Remove(_currentMap.Id);
            _state.History.Insert(0, _currentMap.Id);
            if (_state.History.Count > 20)
                _state.History.RemoveRange(20, _state.History.Count - 20);
        }
        _state.PendingId = 0;
        SaveState();
        Logger.LogInformation("Map started: {Engine} -> {Map}", mapName,
            _currentMap == null ? "not in pool" : $"{_currentMap.Name} ({_currentMap.Id})");
        // client reads the mapgroup name when it joins new map
        Server.ExecuteCommand($"mapgroup {Config.MapGroup}");

        // configs run after OnMapStart
        AddTimer(1.0f, ApplyServerSettings, TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(8.0f, ApplyServerSettings, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void OnMapEnd()
    {
        _hudMenus.Clear();
        CancelPanoramaVote();
        ResetEndMatchVote();
        _gameRules = null;
    }

    //  server setup

    private bool UseNativeUi => Config.EndMatchVote.UiMode.ToLowerInvariant() switch
    {
        "native" => true,
        "menu" => false,
        _ => !_mapGroupStale,
    };

    private void ApplyServerSettings()
    {
        if (Config.Maps.Count == 0)
            return;

        var vote = Config.EndMatchVote;
        // the panel reads its maps from the active mapgroup
        Server.ExecuteCommand($"mapgroup {Config.MapGroup}");
        var engineVote = vote.Enabled && UseNativeUi && _nextMap == null;
        SetConVar("mp_endmatch_votenextmap", engineVote ? "1" : "0");
        SetConVar("mp_endmatch_votenextmap_keepcurrent", "0");
        SetConVar("mp_endmatch_votenextleveltime", vote.VoteSeconds.ToString());
        SetConVar("mp_match_restart_delay", (vote.VoteSeconds + vote.ChangeDelaySeconds + 25).ToString());
        SetConVar("mp_match_end_changelevel", "0");
        SetConVar("mp_match_end_restart", "0");
    }

    private static void SetConVar(string name, string value)
    {
        var cvar = ConVar.Find(name);
        if (cvar != null && cvar.StringValue == value)
            return;
        Server.ExecuteCommand($"{name} {value}");
    }

    // writes game/csgo/gamemodes_server.txt so the mapgroup has maps from config
    // The engine reads this file only at startup.
    private void WriteMapGroupFile()
    {
        if (!Config.ManageGamemodesFile || Config.MapGroupMaps.Count > 0 || Config.Maps.Count == 0)
            return;

        try
        {
            // plugins/CS2Votes -> plugins -> counterstrikesharp -> addons -> csgo
            var csgo = Directory.GetParent(ModuleDirectory)?.Parent?.Parent?.Parent?.FullName;
            if (csgo == null || !File.Exists(Path.Combine(csgo, "gameinfo.gi")))
            {
                Logger.LogWarning("Cannot find the csgo folder. gamemodes_server.txt is not managed.");
                return;
            }

            var path = Path.Combine(csgo, "gamemodes_server.txt");
            var text = BuildMapGroupFile();
            var old = File.Exists(path) ? File.ReadAllText(path) : null;
            if (old != null && MapGroupEntries(old).SequenceEqual(MapGroupEntries(text)) && old.Contains($"\"{Config.MapGroup}\""))
                return;

            if (old != null && !File.Exists(path + ".CS2Votes.bak"))
                File.Copy(path, path + ".CS2Votes.bak");
            File.WriteAllText(path, text);
            _mapGroupStale = true;
            Logger.LogWarning("gamemodes_server.txt changed. Restart the server to use the new mapgroup. Until then the vote uses the menu.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Cannot write gamemodes_server.txt");
        }
    }

    private static List<string> MapGroupEntries(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, "\"(workshop/\\d+/[^\"]+)\"")
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToList();

    private string BuildMapGroupFile()
    {
        var group = Config.MapGroup;
        var sb = new StringBuilder();
        sb.Append("// Generated by CS2Votes from configs/plugins/CS2Votes/CS2Votes.json. Edit the config, not this file.\n");
        sb.Append("\"GameModes_Server.txt\"\n{\n");
        sb.Append($"\t\"gameTypes\" {{ \"custom\" {{ \"gameModes\" {{ \"custom\" {{ \"mapgroupsMP\" {{ \"{group}\" \"\" }} }} }} }} }}\n");
        sb.Append("\t\"mapgroups\"\n\t{\n");
        sb.Append($"\t\t\"{group}\"\n\t\t{{\n\t\t\t\"name\"\t\t\"{group}\"\n\t\t\t\"maps\"\n\t\t\t{{\n");
        foreach (var map in Config.Maps)
        {
            var baseName = string.IsNullOrEmpty(map.MapName) ? map.Name : map.MapName;
            sb.Append($"\t\t\t\t\"workshop/{map.Id}/{baseName}\"\t\t\"\"\n");
        }
        sb.Append("\t\t\t}\n\t\t}\n\t}\n}\n");
        return sb.ToString();
    }

    //  maps
    /// index of the map in active mapgroup, as engine and client count it; -1 when it is not there anymore
    private int MapGroupIndex(MapEntry map) => Config.MapGroupMaps.Count > 0
        ? Config.MapGroupMaps.FindIndex(m => m.Equals(map.MapName, StringComparison.OrdinalIgnoreCase))
        : Config.Maps.IndexOf(map);

    private MapEntry? ResolveCurrentMap()
    {
        var engineName = Server.MapName;
        var pending = Config.Maps.FirstOrDefault(m => m.Id == _state.PendingId);
        var byName = Config.Maps.Where(m => m.MapName.Equals(engineName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1)
            return byName[0];
        if (byName.Count > 1)
            return pending != null && byName.Contains(pending) ? pending : byName[0];
        return pending;
    }

    // finds a map by workshop ID, display name, engine name or a unique part of a name.
    private List<MapEntry> FindMaps(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return [];

        if (ulong.TryParse(query, out var id))
        {
            var byId = Config.Maps.Where(m => m.Id == id).ToList();
            if (byId.Count > 0)
                return byId;
        }
        var exact = Config.Maps.Where(m =>
            m.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            m.MapName.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
            return exact;

        return Config.Maps.Where(m =>
            m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            m.MapName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void ScheduleMapChange(MapEntry map, float delay, string reason)
    {
        if (_changing)
            return;
        _changing = true;
        _nextMap = map;
        _state.PendingId = map.Id;
        SaveState();
        Logger.LogInformation("Map change to {Name} ({Id}) in {Delay}s: {Reason}", map.Name, map.Id, delay, reason);
        _changeTimer = AddTimer(Math.Max(0.1f, delay), () => ChangeMapNow(map), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ChangeMapNow(MapEntry map)
    {
        Server.ExecuteCommand($"host_workshop_map {map.Id}");

        // if map does not load
        _changeTimer = AddTimer(45f, () =>
        {
            Logger.LogError("host_workshop_map {Id} did not change the map in 45s.", map.Id);
            _changing = false;
            _state.PendingId = 0;
            SaveState();
            Broadcast("change.failed", map.Name);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void SaveState()
    {
        try
        {
            _state.Save(_statePath);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Cannot save state.json");
    }   }

    // game rules

    private CCSGameRulesProxy? _gameRulesProxy;
    private CCSGameRules? _gameRules;
    private CCSGameRules? GameRules
    {
        get
        {
            if (_gameRules != null && _gameRulesProxy is { IsValid: true })
                return _gameRules;
            _gameRulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
            _gameRules = _gameRulesProxy?.GameRules;
            return _gameRules;
    }   }

    private bool IsWarmup => GameRules?.WarmupPeriod ?? false;
    private int MaxRounds => ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 0;
    private int RoundsPlayed => GameRules?.TotalRoundsPlayed ?? 0;
    private int SecondsSinceMapStart => (int)(DateTime.UtcNow - _mapStartedAt).TotalSeconds;

    //  players and messages

    private static List<CCSPlayerController> Humans() => Utilities.GetPlayers()
        .Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false, Connected: PlayerConnectedState.Connected })
        .ToList();

    private static bool IsHuman(CCSPlayerController? p) => p is { IsValid: true, IsBot: false, IsHLTV: false };

    private string LanguageOf(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid)
            return Config.DefaultLanguage;
        try
        {
            return player.GetLanguage().TwoLetterISOLanguageName;
        }
        catch
        {
            return Config.DefaultLanguage;
        }
    }

    private string T(CCSPlayerController? player, string key, params object[] args) =>
        _lang.Get(LanguageOf(player), key, args);

    private string Prefix => Lang.Colorize(Config.ChatPrefix);
    private void Reply(CCSPlayerController? player, string key, params object[] args)
    {
        if (player == null || !player.IsValid)
        {
            Server.PrintToConsole(Lang.Strip(_lang.Get(Config.DefaultLanguage, key, args)));
            return;
        }
        player.PrintToChat($" {Prefix} {T(player, key, args)}");
    }

    private void Broadcast(string key, params object[] args)
    {
        foreach (var player in Humans())
            player.PrintToChat($" {Prefix} {T(player, key, args)}");
        Logger.LogInformation("[chat] {Text}", Lang.Strip(_lang.Get(Config.DefaultLanguage, key, args)));
    }

    private void BroadcastCenter(string key, params object[] args)
    {
        foreach (var player in Humans())
            player.PrintToCenter(Lang.Strip(T(player, key, args)));
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (IsWarmup || _changing)
            return HookResult.Continue;
        var max = MaxRounds;
        if (max > 0 && RoundsPlayed == max - 1)
        {
            if (_rtvPassed)
                Broadcast("rtv.last_round");
            else if (_nextMap != null)
                Broadcast("round.last_nextmap", _nextMap.Name);
            else if (Config.EndMatchVote.AnnounceLastRound && Config.EndMatchVote.Enabled)
                Broadcast("round.last");
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsHuman(player))
            return HookResult.Continue;

        AddTimer(10f, () =>
        {
            if (player is { IsValid: true } && !_changing)
                Reply(player, "welcome", _currentMap?.Name ?? Server.MapName);
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || player.IsBot)
            return HookResult.Continue;
        var steamId = player.SteamID;
        _menuVotes.Remove(steamId);
        _hudMenus.Remove(player.Slot);
        if (_rtvVoters.Remove(steamId))
        {
            // one human less can be enough for rest
            Server.NextFrame(CheckRtvThreshold);
        }
        return HookResult.Continue;
    }

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (!Config.PlainChatTriggers || !IsHuman(player))
            return HookResult.Continue;

        var text = info.ArgString.Trim().Trim('"').Trim().ToLowerInvariant();
        Server.NextFrame(() =>
        {
            if (player is not { IsValid: true })
                return;
            switch (text)
            {
                case "rtv":
                case "rockthevote":
                    DoRtv(player);
                    break;
                case "nextmap":
                    ShowNextMap(player);
                    break;
                case "currentmap":
                    ShowCurrentMap(player);
                    break;
                case "timeleft":
                    ShowTimeLeft(player);
                    break;
            }
        });
        return HookResult.Continue;
}   }