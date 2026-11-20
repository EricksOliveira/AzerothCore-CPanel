namespace AzerothCorePanel.Models;

/// <summary>A character that is online, with names for the WotLK (3.3.5a) race, class and map ids.</summary>
public sealed record OnlinePlayer(string Name, int Level, int Race, int Class, int Map)
{
    private static readonly Dictionary<int, string> RaceNames = new()
    {
        [1] = "Humano",
        [2] = "Orc",
        [3] = "Anão",
        [4] = "Elfo Noturno",
        [5] = "Morto-vivo",
        [6] = "Tauren",
        [7] = "Gnomo",
        [8] = "Troll",
        [10] = "Elfo Sangrento",
        [11] = "Draenei",
    };

    private static readonly Dictionary<int, string> ClassNames = new()
    {
        [1] = "Guerreiro",
        [2] = "Paladino",
        [3] = "Caçador",
        [4] = "Ladino",
        [5] = "Sacerdote",
        [6] = "Cavaleiro da Morte",
        [7] = "Xamã",
        [8] = "Mago",
        [9] = "Bruxo",
        [11] = "Druida",
    };

    private static readonly HashSet<int> AllianceRaces = new() { 1, 3, 4, 7, 11 };

    // Continents; other maps are mostly dungeons, raids, battlegrounds and arenas.
    private static readonly Dictionary<int, string> MapNames = new()
    {
        [0] = "Reinos do Leste",
        [1] = "Kalimdor",
        [530] = "Terralém",
        [571] = "Nortúndria",
    };

    public string RaceName => RaceNames.TryGetValue(Race, out string? name) ? name : $"Raça {Race}";

    public string ClassName => ClassNames.TryGetValue(Class, out string? name) ? name : $"Classe {Class}";

    public string Faction => AllianceRaces.Contains(Race) ? "Aliança" : "Horda";

    public string MapName => MapNames.TryGetValue(Map, out string? name) ? name : $"Mapa {Map}";
}
