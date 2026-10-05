using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;
namespace CS2Votes;

public sealed class MapEntry
{
    /// <summary>Steam Workshop file ID. The map is always loaded with host_workshop_map</summary>
    [JsonPropertyName("Id")] public ulong Id { get; set; }
    /// <summary>Display name shown to players.</summary>
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    /// <summary>Name the engine reports after the map loads, e.g. de_dust2</summary>
    [JsonPropertyName("MapName")] public string MapName { get; set; } = "";
}

public sealed class EndMatchVoteConfig
{
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
    /// <summary>"Native" = CS2 end of match vote panel, "Menu" = WASD menu, "Auto" = Native unless the mapgroup file needs restart</summary>
    [JsonPropertyName("UiMode")] public string UiMode { get; set; } = "Auto";
    [JsonPropertyName("VoteSeconds")] public int VoteSeconds { get; set; } = 20;
    /// <summary>seconds between the result and the map change</summary>
    [JsonPropertyName("ChangeDelaySeconds")] public int ChangeDelaySeconds { get; set; } = 6;
    /// <summary>How many of the last played maps (the current map included) are left out of the vote</summary>
    [JsonPropertyName("ExcludeRecentMaps")] public int ExcludeRecentMaps { get; set; } = 1;
    /// <summary>maximum options on the panel (the engine supports 10)</summary>
    [JsonPropertyName("MaxOptions")] public int MaxOptions { get; set; } = 10;
    [JsonPropertyName("AnnounceLastRound")] public bool AnnounceLastRound { get; set; } = true;
}

public sealed class RtvConfig
{
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
    /// <summary>Part of the human players that must type !rtv (0.0 - 1.0)</summary>
    [JsonPropertyName("Percent")] public float Percent { get; set; } = 0.6f;
    [JsonPropertyName("MinPlayers")] public int MinPlayers { get; set; } = 1;
    /// <summary>seconds after map start before !rtv is allowed.</summary>
    [JsonPropertyName("AllowAfterSeconds")] public int AllowAfterSeconds { get; set; } = 60;
    /// <summary>"EndOfRound" = the match ends after the current round, "Now" = the current round ends now</summary>
    [JsonPropertyName("Mode")] public string Mode { get; set; } = "Now";
}

public sealed class VoteMapConfig
{
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
    /// <summary>part of the human players that must vote F1 (yes), YES votes must also be more than no votes</summary>
    [JsonPropertyName("Percent")] public float Percent { get; set; } = 0.5f;
    [JsonPropertyName("VoteSeconds")] public int VoteSeconds { get; set; } = 20;
    [JsonPropertyName("CooldownSeconds")] public int CooldownSeconds { get; set; } = 120;
    [JsonPropertyName("ChangeDelaySeconds")] public int ChangeDelaySeconds { get; set; } = 5;
    [JsonPropertyName("AllowAfterSeconds")] public int AllowAfterSeconds { get; set; } = 60;
    /// <summary>How long the !votemap map list stays open?</summary>
    [JsonPropertyName("MenuSeconds")] public int MenuSeconds { get; set; } = 30;
}

public sealed class HudConfig
{
    /// <summary>empty lines under the WASD menu. They push menu up, above game mode HUD. 0 = normal position.</summary>
    [JsonPropertyName("LiftLines")] public int LiftLines { get; set; } = 5;
    /// <summary>Show seconds left in the menu title.</summary>
    [JsonPropertyName("ShowTimer")] public bool ShowTimer { get; set; } = true;
}

public sealed class PluginConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")] public override int Version { get; set; } = 2;
    /// <summary>
    /// Active mapgroup.
    /// </summary>
    [JsonPropertyName("MapGroup")] public string MapGroup { get; set; } = "mg_deathmatch";
    /// <summary>
    /// maps of "MapGroup" in volvo's order (gamemodes.txt).
    /// </summary>
    [JsonPropertyName("MapGroupMaps")]
    public List<string> MapGroupMaps { get; set; } =
    [
        "de_dust2", "de_inferno", "de_mirage", "de_cbble", "de_overpass", "de_dust", "de_aztec", "de_nuke",
        "de_vertigo", "cs_militia", "cs_assault", "cs_office", "cs_italy", "de_stmarc", "de_sugarcane", "de_bank",
        "de_safehouse", "de_shortdust", "ar_shoots", "ar_baggage", "ar_monastery",
    ];
    /// <summary>write game/csgo/gamemodes_server.txt from "Maps" (only for our own mapgroup, "MapGroupMaps" empty). A change needs server restart</summary>
    [JsonPropertyName("ManageGamemodesFile")] public bool ManageGamemodesFile { get; set; } = false;
    [JsonPropertyName("Maps")]
    public List<MapEntry> Maps { get; set; } =
    [
        new() { Id = 3758986810, Name = "Dust2", MapName = "de_dust2" },
        new() { Id = 3711322683, Name = "Nuke", MapName = "de_nuke" },
        new() { Id = 3644811896, Name = "Office", MapName = "cs_office" },
        new() { Id = 3615968422, Name = "Mirage", MapName = "de_mirage" },
        new() { Id = 3608612434, Name = "Inferno", MapName = "de_inferno" },
    ];
    [JsonPropertyName("ChatPrefix")] public string ChatPrefix { get; set; } = "{orange}[Maps]{default}";
    /// <summary>Language for players without a language setting and for the server console.</summary>
    [JsonPropertyName("DefaultLanguage")] public string DefaultLanguage { get; set; } = "en";
    /// <summary>also react to plain "rtv", "nextmap", "currentmap" and "timeleft" in chat (without "!").</summary>
    [JsonPropertyName("PlainChatTriggers")] public bool PlainChatTriggers { get; set; } = true;
    [JsonPropertyName("EndMatchVote")] public EndMatchVoteConfig EndMatchVote { get; set; } = new();
    [JsonPropertyName("Rtv")] public RtvConfig Rtv { get; set; } = new();
    [JsonPropertyName("VoteMap")] public VoteMapConfig VoteMap { get; set; } = new();
    [JsonPropertyName("Hud")] public HudConfig Hud { get; set; } = new();
}