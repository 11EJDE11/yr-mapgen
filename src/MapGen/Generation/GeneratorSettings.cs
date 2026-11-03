using System.Text.Json.Serialization;

namespace MapGen.Generation;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MatchFormat
{
    /// <summary>1v1</summary>
    Duel,
    /// <summary>2v2</summary>
    TwoVsTwo,
    /// <summary>3v3</summary>
    ThreeVsThree,
    /// <summary>4v4</summary>
    FourVsFour,
    /// <summary>2v2v2v2</summary>
    FourTeamsOfTwo,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MapSize { Small, Medium, Large }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MapShape { Random, Wide, Square, Tall }

/// <summary>Land and water layout, after the built-in generator's map types.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MapLayout
{
    /// <summary>Open land with lakes (the built-in "Inland"); the water slider sets how much lake.</summary>
    Land,
    /// <summary>One landmass everyone shares, ocean around it.</summary>
    Continent,
    /// <summary>One island per team (the built-in "Team Continents").</summary>
    TeamIslands,
    /// <summary>One island per player plus a few contested islets.</summary>
    PlayerIslands,
    /// <summary>Player islands and many neutral islands (the built-in "Archipelago").</summary>
    Archipelago,
    /// <summary>Land with heavy cliffs and hills (the built-in "Mountainous").</summary>
    Mountainous,
}

/// <summary>What the cliffs are for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CliffStyle
{
    /// <summary>Chosen per map.</summary>
    Mixed,
    /// <summary>Plateaus and ridges placed off the routes: scenery that shapes the paths between them.</summary>
    Scenic,
    /// <summary>
    /// Ridge walls across each player's approach with a gateway on the route, and plateaus beside the
    /// route with the ramp facing the owner, so the high ground over the corridor can be held.
    /// </summary>
    Tactical,
    /// <summary>
    /// The map as a few large height regions: long cliff lines along their borders with ramps where
    /// the routes cross. Highland maps raise the bases, mesa maps the centre.
    /// </summary>
    Terraced,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceLevel { Low, Medium, High, VeryHigh }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GemPlacement
{
    None,
    /// <summary>A gem field for every player, further out than their main ore.</summary>
    PerPlayer,
    /// <summary>Gems only in contested fields between the teams.</summary>
    Contested,
    Both,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EconomyMode
{
    /// <summary>Ore fields, optional gems, tech as configured.</summary>
    Ore,
    /// <summary>No ore at all; oil derricks are the economy (Oil in Corners style).</summary>
    OilOnly,
    /// <summary>Smaller ore fields plus extra oil derricks.</summary>
    Mixed,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TechPlacement
{
    /// <summary>One per player, within MinDistance..MaxDistance of their start.</summary>
    PerPlayer,
    /// <summary>One per team, between the team's players.</summary>
    PerTeam,
    /// <summary>Between the teams, at equal distance from opposing players.</summary>
    Contested,
    /// <summary>In the middle of the map.</summary>
    Centre,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimeOfDay { Day, Dusk, Night }

public sealed class TechRule
{
    /// <summary>oil, repair (Tech Outpost), hospital, airport, machineshop, power, seclab, or a building ID.</summary>
    public string Type { get; set; } = "oil";
    public TechPlacement Placement { get; set; } = TechPlacement.PerPlayer;
    /// <summary>How many per player/team/contested slot.</summary>
    public int Count { get; set; } = 1;
    /// <summary>Distance band from the start (PerPlayer) in cells.</summary>
    public int MinDistance { get; set; } = 8;
    public int MaxDistance { get; set; } = 16;

    public static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["oil"] = "CAOILD", ["derrick"] = "CAOILD", ["repair"] = "CAOUTP", ["outpost"] = "CAOUTP",
        ["hospital"] = "CATHOSP", ["airport"] = "CAAIRP", ["machineshop"] = "CAMACH",
        ["power"] = "CAPOWR", ["seclab"] = "CASLAB",
    };

    [JsonIgnore]
    public string BuildingId => Aliases.TryGetValue(Type, out string id) ? id : Type.ToUpperInvariant();
}

public sealed class GeneratorSettings
{
    public int Seed { get; set; } = 1;
    public string Theater { get; set; } = "TEMPERATE";
    public MatchFormat Format { get; set; } = MatchFormat.Duel;
    public MapSize Size { get; set; } = MapSize.Medium;
    public MapShape Shape { get; set; } = MapShape.Random;
    public MapLayout Layout { get; set; } = MapLayout.Land;
    public ResourceLevel Resources { get; set; } = ResourceLevel.Medium;
    public GemPlacement Gems { get; set; } = GemPlacement.PerPlayer;

    /// <summary>Multiplier on ore field size (1 = calibrated against popular maps).</summary>
    public double OreFieldSize { get; set; } = 1.0;

    /// <summary>Multiplier on gem field size.</summary>
    public double GemFieldSize { get; set; } = 1.0;

    /// <summary>Share of ore fields that get an ore drill so they regrow (popular maps: 80-90%).</summary>
    public double Drills { get; set; } = 0.85;
    public EconomyMode Economy { get; set; } = EconomyMode.Ore;
    /// <summary>0..1: gentle hills and raised ground with sloped edges.</summary>
    public double Hills { get; set; } = 0.5;

    /// <summary>0..1: cliff plateaus and ridges (some with ramps up, some closed).</summary>
    public double Cliffs { get; set; } = 0.5;

    public CliffStyle CliffStyle { get; set; } = CliffStyle.Mixed;

    /// <summary>0..1: lakes on Land maps (share of the map under water); smaller islands on island layouts.</summary>
    public double Water { get; set; } = 0.35;

    /// <summary>0..1: garrisonable civilian buildings between the sides.</summary>
    public double Garrisons { get; set; } = 0.5;

    /// <summary>0..1: villages (a few civilian buildings on a short road with a fence) near the contested ground.</summary>
    public double Villages { get; set; } = 0.5;

    /// <summary>A river across the map between the sides, with low bridges where the routes cross it (land layouts only).</summary>
    public bool Rivers { get; set; }

    /// <summary>Dirt trails from bases towards tech and gem fields.</summary>
    public bool Trails { get; set; } = true;

    /// <summary>Sandbag rings (with gaps) around contested and centre tech.</summary>
    public bool WallsAroundTech { get; set; } = true;
    public TimeOfDay Time { get; set; } = TimeOfDay.Day;

    /// <summary>Optional override; by default it is chosen from the format.</summary>
    public SymmetryKind? Symmetry { get; set; }

    /// <summary>
    /// 0..1. At 0 every player's side is an exact copy of the others. Above 0 the copies of starts,
    /// fields, tech and plateaus drift off their mirror positions (up to about 9 cells at 1), fields
    /// and hills take their own shapes, and lakes, cliffs and trees are not mirrored at all; fairness
    /// is then enforced by measuring the map and re-rolling.
    /// </summary>
    public double Asymmetry { get; set; } = 0;

    /// <summary>
    /// 0..1: how strictly the fairness check is applied. 1 is the standard tolerance (about 15% between
    /// equivalent starts); lower values widen it, up to four times at 0, so asymmetric maps settle on
    /// a seed sooner at the cost of less equal starts.
    /// </summary>
    public double Fairness { get; set; } = 1;

    /// <summary>Multiplier on every fairness tolerance that follows from <see cref="Fairness"/>.</summary>
    [JsonIgnore]
    public double FairnessToleranceScale => 1 + 3 * (1 - Math.Clamp(Fairness, 0, 1));

    /// <summary>0..1, amount of tree cover used as obstacles.</summary>
    public double Trees { get; set; } = 0.5;

    /// <summary>Tech buildings; null uses the defaults for the format.</summary>
    public List<TechRule> Tech { get; set; }

    public string Name { get; set; }

    /// <summary>How many seeds to try before giving up on the fairness tolerance.</summary>
    public int MaxAttempts { get; set; } = 12;

    /// <summary>Symmetries that produce a fair layout for a format.</summary>
    public static SymmetryKind[] AllowedSymmetries(MatchFormat format) => format switch
    {
        MatchFormat.Duel => new[] { SymmetryKind.Rotate180, SymmetryKind.MirrorLeftRight, SymmetryKind.MirrorTopBottom, SymmetryKind.None },
        MatchFormat.FourTeamsOfTwo => new[] { SymmetryKind.Quad, SymmetryKind.Diamond, SymmetryKind.None },
        _ => new[] { SymmetryKind.Quad, SymmetryKind.Rotate180, SymmetryKind.MirrorLeftRight, SymmetryKind.MirrorTopBottom, SymmetryKind.None },
    };

    [JsonIgnore]
    public int PlayerCount => Format switch
    {
        MatchFormat.Duel => 2,
        MatchFormat.TwoVsTwo => 4,
        MatchFormat.ThreeVsThree => 6,
        MatchFormat.FourVsFour => 8,
        MatchFormat.FourTeamsOfTwo => 8,
        _ => 2,
    };

    [JsonIgnore]
    public string FormatLabel => Format switch
    {
        MatchFormat.Duel => "1v1",
        MatchFormat.TwoVsTwo => "2v2",
        MatchFormat.ThreeVsThree => "3v3",
        MatchFormat.FourVsFour => "4v4",
        MatchFormat.FourTeamsOfTwo => "2v2v2v2",
        _ => "",
    };

    public List<TechRule> EffectiveTech()
    {
        if (Tech != null)
            return Tech;

        var list = new List<TechRule>();
        if (Economy != EconomyMode.Ore)
            list.Add(new TechRule { Type = "oil", Placement = TechPlacement.PerPlayer, Count = Economy == EconomyMode.OilOnly ? 3 : 2, MinDistance = 6, MaxDistance = 20 });
        else
            list.Add(new TechRule { Type = "oil", Placement = TechPlacement.PerPlayer, Count = 1, MinDistance = 9, MaxDistance = 16 });

        list.Add(new TechRule { Type = "oil", Placement = TechPlacement.Contested, Count = 1 });

        if (Format != MatchFormat.Duel)
            list.Add(new TechRule { Type = "repair", Placement = TechPlacement.PerTeam, Count = 1 });
        list.Add(new TechRule { Type = Format == MatchFormat.Duel ? "machineshop" : "hospital", Placement = TechPlacement.Centre, Count = 1 });

        return list;
    }
}
