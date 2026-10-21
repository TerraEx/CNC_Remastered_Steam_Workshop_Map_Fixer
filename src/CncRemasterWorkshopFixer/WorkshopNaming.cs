using System.Globalization;

namespace CncRemasterWorkshopFixer;

public static class WorkshopNaming
{
    // C&C's UGC convention uses fixed-width uppercase hexadecimal for both identities.
    // The first value must be Steam's creator ID; it cannot be substituted with the
    // locally available subscriber/account identity.
    public static string FormatId(ulong value) => value.ToString("X16", CultureInfo.InvariantCulture);

    public static string CreateBasename(ulong publisherSteamId, ulong workshopItemId) =>
        $"UGC_{FormatId(publisherSteamId)}_{FormatId(workshopItemId)}_MAPDATA";

    public static ulong ParsePublisherId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.Parse(trimmed[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        // Bare 16-character values follow C&C's hex naming convention, even when
        // they contain only digits. Prefix with 0x to make other hex widths explicit.
        if (trimmed.Length == 16)
        {
            return ulong.Parse(trimmed, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        return ulong.Parse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture);
    }
}
