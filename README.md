# CS2Votes

A map vote plugin for Counter-Strike 2 servers that play Steam Workshop maps.
It is a [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin. It was made for Prop Hunt server: 78.61.47.144:27015.

- **End-of-match vote** in CS2 native vote panel, with map names and pictures.
  If native panel cannot show a map,  vote uses WASD menu.
- **`!rtv`**: when enough players vote, match ends and map vote starts.
- **`!votemap [map]`**: a native F1 (yes) / F2 (no) vote to change to one map now.
  Without a map name, it opens WASD menu with all maps.
- **WASD menu** with a countdown. Its shown higher than the normal center text so the game mode HUD does not cover it.
- Workshop maps always load by ID with `host_workshop_map <id>`. 
- Languages: English and Lithuanian (`src/lang/*.json`).

## Requirements

| Item                                                          | Version                                                        |
|---------------------------------------------------------------|----------------------------------------------------------------|
| CounterStrikeSharp                                            | 1.0.374 or later (.NET 10)                                     |
| [CS2MenuManager](https://github.com/schwarper/CS2MenuManager) | 1.0.42 (shared library in `addons/counterstrikesharp/shared/`) |

## Install

1. Download `CS2Votes-<version>.zip` from Releases
2. Extract it into `game/csgo/` on the server. This gives
   `addons/counterstrikesharp/plugins/CS2Votes/`.
3. Start the server, or let CounterStrikeSharp hot-reload the plugin.
   The plugin writes its config to
   `addons/counterstrikesharp/configs/plugins/CS2Votes/CS2Votes.json`.
4. Put your workshop maps in `Maps`. See `config/CS2Votes.example.json`.


### Native panel

The game client draws each tile of the end-of-match panel from its own copy of the active mapgroup
(Valve's `gamemodes.txt`). A mapgroup that only the server knows shows "undefined".
So plugin uses Valve's `mg_deathmatch`, which has stock names of pool maps,
and shows each workshop map at the index of its `MapName` in `MapGroupMaps`.
map that loads is always the workshop map.

A map whose `MapName` is not in `MapGroupMaps` cannot show in the native panel. Then the vote uses the menu.
After a CS2 update that changes `mg_deathmatch`, update `MapGroupMaps`.

## Build

```sh
dotnet build src/CS2Votes.csproj -c Release
# or
scripts/package.sh
```

Without a local .NET 10 SDK:

```sh
docker run --rm -v "$PWD":/w -w /w mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/CS2Votes.csproj -c Release
```