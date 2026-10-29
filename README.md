# yr-mapgen

A random map generator for Red Alert 2: Yuri's Revenge that produces symmetric, fairness-checked
multiplayer maps, plus a fairness analyser for existing maps.

## Building

LightningMap (the preview renderer) is a git submodule, so clone with `--recursive` or run
`git submodule update --init` once. It needs the .NET 10 SDK to build (the projects still target .NET 8).

```
dotnet build src/MapGen.Cli -c Release
```

The generator needs per-theater tile data dumped from the game's MIX files (committed under `data/`).
To regenerate it after a game/package update:

```
dotnet build tools/TileDump -c Release     # net48; uses ccmaps-net's CNCMaps.FileFormats
tools/TileDump/bin/Release/net48/TileDump.exe <YR directory> data
```

`data/ConnectedTileDrawer.ini` and the other `.ini` files in `data/` are World-Altering Editor's YR
configuration from the CnCNet YR client package. `data/garrisons.json` lists the garrisonable
buildings the shipped maps use per theater.

Island layouts are naval/air maps: starts are not expected to reach each other by ground, so the
fairness check skips that test there. Lunar has no cliffs or water in WAE's YR configuration, so it
gets neither (and no island layouts).

## Desktop app

`YRMapGenerator.exe` is a Windows app with all the settings, a Generate button, a preview and the
fairness report. Publish a standalone folder (exe + `data\`, no .NET needed):

```
dotnet publish src/MapGen.App -c Release -r win-x64 -o publish
```

Previews are rendered in-process by [LightningMap](https://github.com/11EJDE11/LightningMap)
(`external/LightningMap`, its `library-split` branch), which reads the game's art. Set the Yuri's Revenge
directory once (it is remembered in `%AppData%\yr-mapgen\settings.json`). "Copy to Maps\Custom" puts
the map into the client; the lobby preview is the one embedded in the map, so no `.png` is needed (a
same-named one would replace it, so one left from an earlier copy is removed).

## Usage

```
yr-mapgen generate --format 2v2 --theater snow --terrain cliffs --seed 42 --out my.map
yr-mapgen generate --config examples/2v2-custom-tech.json --out my.map
yr-mapgen generate --format 4v4 --count 10 --out generated/
yr-mapgen analyze "Maps/Yuri's Revenge/tourofegypt.map"
```

| Option | Values |
|---|---|
| `--format` | `1v1`, `2v2`, `3v3`, `4v4`, `2v2v2v2` |
| `--style` | `standard`, `blitz` (smaller map, tighter bases, no secondary fields) |
| `--size` | `small`, `medium`, `large` (area varies about ±8% around the preset) |
| `--shape` | `random`, `wide`, `square`, `tall` (sides split left/right on wide, top/bottom on tall) |
| `--layout` | `land` (lakes), `continent` (one landmass), `teamislands`, `playerislands`, `archipelago`, `mountainous` |
| `--symmetry` | 1v1: `rotate180`, `mirrorleftright`, `mirrortopbottom`; 2v2/3v3/4v4: those plus `quad`; 2v2v2v2: `quad`, `diamond`; any format: `none` (terrain not mirrored, see below) |
| `--theater` | `temperate`, `snow`, `urban`, `newurban`, `desert`, `lunar` |
| `--hills`, `--cliffs`, `--water`, `--trees` | 0..1 amounts of rolling high ground, cliff plateaus/ridges, water, tree cover. On `land` maps water is the share of the map under lakes (up to ~38%); on island layouts more water means smaller islands |
| `--resources` | `low`, `medium`, `high`, `veryhigh` |
| `--orefields`, `--gemfields` | field size multipliers (1 = calibrated default) |
| `--drills` | share of ore fields with an ore drill (default 0.85) |
| `--gems` | `none`, `perplayer`, `contested`, `both` |
| `--economy` | `ore`, `oilonly` (Oil in Corners style), `mixed` |
| `--time` | `day`, `dusk`, `night` |
| `--game` | Yuri's Revenge directory: the embedded preview becomes a LightningMap render (without it, a radar-style picture) |

JSON settings also take `Garrisons` (0..1), `Trails` and `WallsAroundTech` (booleans).

Tech buildings are configured in a JSON settings file (`Tech` list). Each rule has a `Type`
(`oil`, `repair` = Tech Outpost, `hospital`, `airport`, `machineshop`, `power`, `seclab`, or a
building ID), a `Placement` (`PerPlayer` within `MinDistance`..`MaxDistance` of the start,
`PerTeam` between teammates, `Contested` between the teams, `Centre`) and a `Count`.

Generated maps include `TeamStartMapping0` in `[Basic]`, so the CnCNet client offers the intended
team layout (e.g. `A,A,B,B` for 2v2: left team vs right team).

## How it works

1. **Symmetry.** All features are decided once and copied through a symmetry group acting on cell
   coordinates (mirror left-right `(x,y)->(y,x)`, mirror top-bottom `(x,y)->(c-y,c-x)`, 180°
   rotation, or 90° rotation for the diamond layout). These are exact isometries of the cell grid, so
   travel distances are identical between mirrored players. Buildings cannot rotate, so a mirrored
   non-square building is centred on its image's footprint; cliffs are re-planned along each mirrored
   outline. Defaults: 1v1 uses a 180° rotation or a left-right mirror; team formats use four
   mirrored quadrants (teams left vs right, or one team per quadrant for 2v2v2v2).
   With `none` the lakes, closed cliffs, trees and decoration are free; bases, ore, gems, tech, hills
   and ramped plateaus still follow one of the symmetries. Free terrain stays off the ground round each
   base and off straight lanes to the centre, teammates and nearest enemies, so routes stay equal, and
   re-rolls catch the rest.
2. **Layout.** Start positions come from per-format templates (team maps are wider than tall so
   teammates are closer to each other than to the enemy). Each start gets a protected base area.
3. **Terrain.** Water first: a symmetric land/water mask per layout; every enclosed lake or island
   gets a shore loop (WAE's connected-tile planner, ported) along a symmetric star-shaped outline,
   and island layouts flood the ocean from the map edge. Then cliff plateaus: some with a ramp up
   (an open cliff path that starts and ends in "Slope Set Pieces", whose connection points were
   learnt from how the shipped maps use them; the ramp is re-planned on whichever edge each mirrored
   copy lands on), plus closed plateaus and long ridges with trees on top. Then rolling high ground
   from symmetric value noise. Ground height is a field of cell-corner heights relaxed to one level per
   cell (after WAE's CornerHeightField), from which every cell's level and slope tile follow, so
   bases, ore and buildings always sit on flat ground.
4. **Resources.** Per player: a main ore field behind the base, a secondary field further out, and
   an optional gem field; shared contested and centre fields. Field sizes are calibrated against
   popular maps (Heck Freezes Over, Hex Bay); density follows official maps (mostly stages 7-11).
5. **Tech.** Placed by rule (per player / per team / contested / centre). Centre buildings must map
   onto themselves under every symmetry, so there is exactly one.
6. **Decoration.** Sandbag rings with a gap per team around shared tech, garrisonable civilian
   buildings (per-theater palette taken from the shipped maps), dirt trails from bases to tech and
   gem fields, biome tree sets and rocks (WAE's YR collections), LAT ground patches with automatic
   transitions, craters and burn marks. Ore fields get ore drills (TIBTRE) in about 85% of fields.
7. **Obstacles and connectivity.** Tree clusters, then every start must reach every other start
   through corridors at least three cells wide and every field/building must be reachable; blocking
   trees are cleared symmetrically.
8. **Verification.** The map is measured with the fairness analyser (ground travel distances for
   wheeled units, using the game's tile-to-land-type table and movement costs). Starts that should
   be equivalent are compared, plus team totals; failing attempts are re-rolled with a new seed.

## Fairness analyser

`yr-mapgen analyze` reports, per start position: resources reachable within 15/25/40/60 cells of
travel, travel to the first 4000 credits, gems within 30, distance to the nearest enemy/ally,
buildable area around the start, and travel to each kind of tech building, then flags metrics that
differ by more than the tolerance.

## Licence

Contains code ported from World-Altering Editor (GPL-3.0) and the MiniLZO/Format80 codecs it uses,
so this project is GPL-3.0 as well.
