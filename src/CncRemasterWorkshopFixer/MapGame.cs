namespace CncRemasterWorkshopFixer;

public enum MapGame
{
    TiberianDawn,
    RedAlert
}

public sealed class UnsupportedMapLayoutException(string message) : NotSupportedException(message);

public static class MapGameClassifier
{
    public static MapGame Classify(IEnumerable<PgmMember> members)
    {
        var memberList = members.ToList();
        if (memberList.Any(member => !Path.GetFileNameWithoutExtension(member.Name)
                .Equals("MAPDATA", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The archive contains a member whose basename is not MAPDATA.");
        }

        var extensions = memberList
            .Select(member => Path.GetExtension(member.Name).ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Exact member sets avoid routing mixed or unfamiliar packages to the wrong game.
        if (memberList.Count == 3 && extensions.SetEquals(new[] { ".MPR", ".JSON", ".TGA" }))
        {
            return MapGame.RedAlert;
        }

        if (memberList.Count == 4 && extensions.SetEquals(new[] { ".INI", ".BIN", ".JSON", ".TGA" }))
        {
            return MapGame.TiberianDawn;
        }

        throw new UnsupportedMapLayoutException(
            "The PGM payload is structurally valid but is not a supported Tiberian Dawn or Red Alert map layout.");
    }

    public static string DestinationDirectoryName(MapGame game) => game switch
    {
        MapGame.TiberianDawn => "Tiberian_Dawn",
        MapGame.RedAlert => "Red_Alert",
        _ => throw new ArgumentOutOfRangeException(nameof(game))
    };
}
