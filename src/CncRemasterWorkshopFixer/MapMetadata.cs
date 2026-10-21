using System.Globalization;
using System.Text;

namespace CncRemasterWorkshopFixer;

public enum MapType
{
    SinglePlayer,
    Multiplayer
}

public sealed record MapMetadata(
    string? Author,
    MapType? Type,
    string? Theater,
    int? Width,
    int? Height)
{
    public bool IsEmpty => Author is null && Type is null && Theater is null &&
        Width is null && Height is null;
}

public static class MapMetadataReader
{
    public static MapMetadata? ReadFromArchive(PgmArchive archive, MapGame game)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var expectedName = game == MapGame.TiberianDawn ? "MAPDATA.INI" : "MAPDATA.MPR";
        var member = archive.Members.SingleOrDefault(candidate =>
            candidate.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
        if (member is null)
        {
            return null;
        }

        try
        {
            var metadata = Read(archive.Read(member));
            return metadata.IsEmpty ? null : metadata;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Tooltip metadata is optional and must never make a valid package unusable.
            return null;
        }
    }

    public static MapMetadata Read(byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        string? section = null;
        string? author = null;
        MapType? type = null;
        string? theater = null;
        int? width = null;
        int? height = null;

        // These fields were checked against both TD INI and RA MPR packages.
        // This is a tooltip reader, so unrelated sections and briefing text stay out.
        using var reader = new StringReader(Encoding.UTF8.GetString(contents));
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is ';' or '#')
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var candidate = trimmed[1..^1].Trim();
                section = candidate.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
                          candidate.Equals("Map", StringComparison.OrdinalIgnoreCase)
                    ? candidate
                    : null;
                continue;
            }

            if (section is null)
            {
                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = trimmed[..equals].Trim();
            var value = trimmed[(equals + 1)..].Trim();
            if (section.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("Author", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                {
                    author = value;
                }
                else if (key.Equals("SoloMission", StringComparison.OrdinalIgnoreCase) &&
                         bool.TryParse(value, out var soloMission))
                {
                    type = soloMission ? MapType.SinglePlayer : MapType.Multiplayer;
                }
            }
            else if (key.Equals("Theater", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                theater = value.ToUpperInvariant();
            }
            else if (key.Equals("Width", StringComparison.OrdinalIgnoreCase))
            {
                width = ParsePositiveDimension(value);
            }
            else if (key.Equals("Height", StringComparison.OrdinalIgnoreCase))
            {
                height = ParsePositiveDimension(value);
            }
        }

        // A lone dimension would make the tooltip imply a usable map size.
        if (width is null || height is null)
        {
            width = null;
            height = null;
        }

        return new(author, type, theater, width, height);
    }

    private static int? ParsePositiveDimension(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var dimension) && dimension > 0
            ? dimension
            : null;
}
