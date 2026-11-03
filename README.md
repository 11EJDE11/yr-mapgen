# yr-mapgen

Random map generator for Red Alert 2: Yuri's Revenge. Makes symmetric multiplayer maps, measures them
for fairness and re-rolls until they pass. Also analyses existing maps.

## Download

[Releases](https://github.com/11EJDE11/yr-mapgen/releases): unzip, run `YRMapGenerator.exe`, set your
Yuri's Revenge folder. No .NET install needed.

## Desktop app

- All settings, Generate / Random, zoomable preview, fairness report.
- Random rolls every setting that is not locked. The padlock next to a setting keeps it: lock
  "Gem fields %" at 200 and press Random for any map with big gem fields. Format and
  economy start locked. An unlocked seed is re-rolled on every Generate.
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
| `--size` | `small`, `medium`, `large` (±8% area) |
| `--shape` | `random`, `wide`, `square`, `tall` |
| `--layout` | `land`, `continent`, `teamislands`, `playerislands`, `archipelago`, `mountainous` |
| `--symmetry` | 1v1: `rotate180`, `mirrorleftright`, `mirrortopbottom`. Team formats: those plus `quad`. 2v2v2v2: `quad`, `diamond`. Any: `none` |
| `--asymmetry` | 0..1. 0: exact copies. Higher: each side's starts, fields, tech and high ground drift off their mirror spots and take their own shapes; scenery is not mirrored. Fairness is enforced by measuring |
| `--fairness` | 0..1. How strictly equivalent starts must match: 1 = within about 15%, 0 = four times looser (asymmetric maps settle sooner) |
| `--theater` | `temperate`, `snow`, `urban`, `newurban`, `desert`, `lunar` |
| `--hills`, `--cliffs`, `--water`, `--trees`, `--villages` | 0..1 |
| `--rivers` | `yes`: a river across the map between the sides with low bridges where the routes cross it; the centre stays a ford (land layouts) |
| `--cliffstyle` | `mixed` (per map), `terraced` (a few large height regions with long cliff lines along their borders and ramps where the routes cross; highland maps raise the bases, mesa maps the centre), `scenic` (plateaus off the routes), `tactical` (ridge walls with a gateway across each approach, ramped plateaus beside the route facing the owner) |
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
   symmetric. With `asymmetry` above 0 a smooth drift field pushes every copy of a start, field, tech
   building and plateau off its mirror position (up to about 9 cells at 1), fields are regrown with
   their own shapes, hills are sampled off their mirror cells, and the scenery is free as with `none`.
   The fairness check (with a tolerance widened by up to half) and re-rolling then keep the sides equal.
2. **Starts.** Per-format templates, each with a protected base area.
3. **Terrain.** With the `terraced` cliff style the map is first divided into a grid of 14-cell blocks,
   each lowland or raised terrace (highland maps raise the bases with a valley between the sides, mesa
   maps raise the centre); the borders become long cliff lines with ramp gaps where the routes cross.
   Then water mask and shores, cliff plateaus with ramps, ridges, rolling high ground. Heights are a
   corner-height field relaxed to one level per cell, so bases and buildings sit on flat ground.
4. **Resources.** Per player: main, secondary and optional gem field. Shared contested and centre
   fields. Sizes calibrated against popular maps.
5. **Tech.** Placed per player, per team, contested or centre (exactly one).
6. **Decoration.** Sandbags round shared tech, garrisons, villages (civilian buildings on a short road
   with a fence), dirt trails from each base to its own tech and gems, towards the centre and to the
   nearest teammate, trees, rocks, LAT patches, craters, ore drills.
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
