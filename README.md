# yr-mapgen

Random map generator for Red Alert 2: Yuri's Revenge. Makes symmetric multiplayer maps, measures them
for fairness and re-rolls until they pass. Also analyses existing maps.

## Download

[Releases](https://github.com/11EJDE11/yr-mapgen/releases): unzip, run `YRMapGenerator.exe`, set your
Yuri's Revenge folder. No .NET install needed.

## Desktop app

- All settings, Generate / All random, zoomable preview, fairness report.
- The Yuri's Revenge folder is needed for previews and "Copy to Maps\Custom". Saved in
  `%AppData%\yr-mapgen\settings.json`.
- "Copy to Maps\Custom" copies the `.map` only. The lobby preview is embedded in the map.

## Command line

```
yr-mapgen generate --format 2v2 --theater snow --seed 42 --out my.map
yr-mapgen generate --config examples/2v2-custom-tech.json --out my.map
yr-mapgen generate --format 4v4 --count 10 --out generated/
yr-mapgen analyze "Maps/Yuri's Revenge/tourofegypt.map"
```

| Option | Values |
|---|---|
| `--format` | `1v1`, `2v2`, `3v3`, `4v4`, `2v2v2v2` |
| `--style` | `standard`, `blitz` (smaller map, tighter bases, no secondary fields) |
| `--size` | `small`, `medium`, `large` (±8% area) |
| `--shape` | `random`, `wide`, `square`, `tall` |
| `--layout` | `land`, `continent`, `teamislands`, `playerislands`, `archipelago`, `mountainous` |
| `--symmetry` | 1v1: `rotate180`, `mirrorleftright`, `mirrortopbottom`. Team formats: those plus `quad`. 2v2v2v2: `quad`, `diamond`. Any: `none` |
| `--theater` | `temperate`, `snow`, `urban`, `newurban`, `desert`, `lunar` |
| `--hills`, `--cliffs`, `--water`, `--trees` | 0..1 |
| `--resources` | `low`, `medium`, `high`, `veryhigh` |
| `--orefields`, `--gemfields` | size multiplier (1 = default) |
| `--drills` | share of ore fields with an ore drill (default 0.85) |
| `--gems` | `none`, `perplayer`, `contested`, `both` |
| `--economy` | `ore`, `oilonly`, `mixed` |
| `--time` | `day`, `dusk`, `night` |
| `--seed`, `--count`, `--attempts`, `--out` | |
| `--game` | Yuri's Revenge folder. Embedded preview is a LightningMap render; without it, radar-style |

JSON settings (`--config`) also take `Garrisons` (0..1), `Trails`, `WallsAroundTech` and a `Tech` list.
Each tech rule has:
- `Type`: `oil`, `repair`, `hospital`, `airport`, `machineshop`, `power`, `seclab` or a building ID
- `Placement`: `PerPlayer` (`MinDistance`..`MaxDistance` from the start), `PerTeam`, `Contested`, `Centre`
- `Count`

## Output

- `[Basic] TeamStartMapping0` is set, so the CnCNet client offers the intended teams (e.g. `A,A,B,B`).
- Embedded preview: LightningMap render of the visible area at 4 px per column, max 75k px.
- Island layouts skip the ground-reachability check between starts.
- Lunar has no cliffs, water or island layouts.

## How it works

1. **Symmetry.** Features are decided once and copied through a symmetry group: mirror, 180° rotation,
   or 90° rotation for diamond. These are exact isometries of the cell grid. With `none`, lakes,
   closed cliffs, trees and decoration are free; bases, resources, tech, hills and ramped plateaus stay
   symmetric.
2. **Starts.** Per-format templates, each with a protected base area.
3. **Terrain.** Water mask and shores, cliff plateaus with ramps, ridges, rolling high ground. Heights
   are a corner-height field relaxed to one level per cell, so bases and buildings sit on flat ground.
4. **Resources.** Per player: main, secondary and optional gem field. Shared contested and centre
   fields. Sizes calibrated against popular maps.
5. **Tech.** Placed per player, per team, contested or centre (exactly one).
6. **Decoration.** Sandbags round shared tech, garrisons, dirt trails, trees, rocks, LAT patches,
   craters, ore drills.
7. **Connectivity.** Starts connect through corridors at least 3 cells wide; every field and building
   is reachable.
8. **Verification.** The fairness analyser compares equivalent starts and team totals. Failures are
   re-rolled with a new seed. Two attempts run in parallel; each seed still gives the same map.

## Fairness analyser

`yr-mapgen analyze` reports per start:
- resources reachable within 15/25/40/60 cells of travel
- travel to the first 4000 credits
- gems within 30 cells
- distance to the nearest enemy and ally
- buildable area round the start
- travel to each kind of tech building

It flags any metric that differs by more than the tolerance.

## Building

Needs the .NET 10 SDK (projects target .NET 8). LightningMap is a submodule.

```
git clone --recursive https://github.com/11EJDE11/yr-mapgen
dotnet build src/MapGen.Cli -c Release
dotnet publish src/MapGen.App -c Release -r win-x64 -o publish
```

`data/` holds per-theater tile data dumped from the game's MIX files. To regenerate it:

```
dotnet build tools/TileDump -c Release
tools/TileDump/bin/Release/net48/TileDump.exe <YR directory> data
```

- `data/*.ini`: WAE's YR configuration from the CnCNet YR client.
- `data/garrisons.json`: garrisonable buildings per theater, taken from the shipped maps.

## Credits and licence

- [World-Altering Editor](https://github.com/CnCNet/WorldAlteringEditor) by
  [Rampastring](https://github.com/Rampastring): ported connected-tile planner, corner-height levelling,
  MiniLZO/Format80 codecs, YR configuration. See `credits.txt`.
- [LightningMap](https://github.com/11EJDE11/LightningMap): previews.
- [ccmaps-net](https://github.com/zzattack/ccmaps-net): MIX reading in TileDump.

GPL-3.0, as WAE is.
