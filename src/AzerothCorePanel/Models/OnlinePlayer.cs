using AzerothCorePanel.Localization;

namespace AzerothCorePanel.Models;

/// <summary>
/// A character that is online, with translated names for the WotLK (3.3.5a) race, class and map ids. The names are
/// read when shown, so the players list follows a language change on its next refresh.
/// </summary>
public sealed record OnlinePlayer(string Name, int Level, int Race, int Class, int Map)
{
    private static readonly HashSet<int> KnownRaces = new() { 1, 2, 3, 4, 5, 6, 7, 8, 10, 11 };
    private static readonly HashSet<int> KnownClasses = new() { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11 };
    private static readonly HashSet<int> AllianceRaces = new() { 1, 3, 4, 7, 11 };

    // Continents; other maps are mostly dungeons, raids, battlegrounds and arenas.
    private static readonly HashSet<int> KnownMaps = new() { 0, 1, 530, 571 };

    public string RaceName => KnownRaces.Contains(Race) ? L.T($"Race.{Race}") : L.F("Race.Other", Race);

    public string ClassName => KnownClasses.Contains(Class) ? L.T($"Class.{Class}") : L.F("Class.Other", Class);

    public bool IsAlliance => AllianceRaces.Contains(Race);

    public string Faction => L.T(IsAlliance ? "Faction.Alliance" : "Faction.Horde");

    public string MapName => KnownMaps.Contains(Map) ? L.T($"Map.{Map}") : L.F("Map.Other", Map);
}
