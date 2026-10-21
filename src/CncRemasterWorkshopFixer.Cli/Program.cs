using System.Globalization;
using CncRemasterWorkshopFixer;

namespace CncRemasterWorkshopFixer.Cli;

/// <summary>Diagnostic and native-cache command-line entry point.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(CliOptions.HelpText);
                return 0;
            }

            if (options.ScanRoot is not null)
            {
                using var scanHttpClient = CreateHttpClient();
                var scanner = new WorkshopMapScanner(new SteamWorkshopMetadataClient(scanHttpClient));
                var maps = await scanner.ScanAsync([options.ScanRoot], options.CacheRoot);
                foreach (var map in maps)
                {
                    Console.WriteLine($"{map.Title}\t{map.Game?.ToString() ?? "Unknown"}\t{MapDisplayText.ForStatus(map.InstallStatus)}\t{map.WorkshopItemId}");
                    if (map.Error is not null)
                    {
                        Console.WriteLine($"  Inspection/scan note: {map.Error}");
                    }
                }

                Console.WriteLine($"Scanned {maps.Count} Workshop map(s). No files were changed.");
                return 0;
            }

            var pgmPath = Path.GetFullPath(options.PgmPath!);
            if (options.Inspect)
            {
                var archive = PgmArchive.Open(pgmPath);
                MapGame? game = null;
                try { game = MapGameClassifier.Classify(archive.Members); }
                catch (NotSupportedException) { }
                Console.WriteLine($"Game: {game?.ToString() ?? "Unknown"}");
                foreach (var member in archive.Members)
                {
                    Console.WriteLine($"{member.Name}\t{member.Size} bytes\toffset {member.Offset}");
                }

                Console.WriteLine("Inspection complete. No files were changed.");
                return 0;
            }

            var itemDirectory = Directory.GetParent(pgmPath)?.Name;
            if (!ulong.TryParse(itemDirectory, NumberStyles.None, CultureInfo.InvariantCulture, out var workshopItemId))
            {
                throw new ArgumentException("MAPDATA.PGM must be inside a directory named with its numeric Workshop item ID.");
            }

            var creatorId = options.CreatorId;
            if (creatorId is null)
            {
                using var httpClient = CreateHttpClient();
                creatorId = await new SteamWorkshopMetadataClient(httpClient).GetCreatorSteamIdAsync(workshopItemId);
            }

            // The CLI copies the native package without adopting Direct ownership.
            // The GUI inventory can recognise this cache copy on its next refresh.
            var result = new NativeUgcCacheManager().Install(
                pgmPath, workshopItemId, creatorId.Value, options.CacheRoot);
            Console.WriteLine(result.ContentChanged
                ? $"Enabled the map by copying its byte-identical package into C&C's UGC cache:\n{result.PgmPath}"
                : $"C&C's UGC cache already contains the byte-identical package:\n{result.PgmPath}");
            Console.WriteLine("C&C Remastered will process and extract the map on game launch.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(ApplicationInfo.UserAgent);
        return httpClient;
    }
}

internal sealed record CliOptions(
    string? PgmPath,
    string? ScanRoot,
    string CacheRoot,
    ulong? CreatorId,
    bool Inspect,
    bool ShowHelp)
{
    public const string HelpText = """
        C&C Remastered Steam Workshop Map Fixer CLI

        Usage:
          CncRemasterWorkshopFixer.Cli <path-to-MAPDATA.PGM> [options]
          CncRemasterWorkshopFixer.Cli --scan <Workshop-root> [--cache-root <directory>]
          CncRemasterWorkshopFixer.Cli --inspect <path-to-MAPDATA.PGM>

        Options:
          --cache-root <directory>  C&C native UGC cache destination.
                                    Defaults to %APPDATA%\CnCRemastered\UGC.
          --creator <id>            Creator SteamID64 in decimal, 0x-prefixed hex, or
                                    16-digit hex. If omitted, query Steam metadata.
          --scan <directory>        Read-only scan of one Workshop content root.
          --inspect <PGM>           Read-only member and game inspection.
          --help                    Show this help.
        """;

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            return new(null, null, NativeUgcCacheManager.GetDefaultCacheRoot(), null, false, true);
        }

        string? pgm = null;
        string? scanRoot = null;
        var cacheRoot = NativeUgcCacheManager.GetDefaultCacheRoot();
        ulong? creator = null;
        var inspect = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--cache-root":
                    cacheRoot = Path.GetFullPath(ReadValue(args, ref i, "--cache-root"));
                    break;
                case "--scan":
                    scanRoot = Path.GetFullPath(ReadValue(args, ref i, "--scan"));
                    break;
                case "--creator":
                    creator = WorkshopNaming.ParsePublisherId(ReadValue(args, ref i, "--creator"));
                    break;
                case "--inspect":
                    inspect = true;
                    pgm = ReadValue(args, ref i, "--inspect");
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        throw new ArgumentException($"Unknown option '{args[i]}'.");
                    }

                    if (pgm is not null)
                    {
                        throw new ArgumentException("Only one MAPDATA.PGM path may be supplied.");
                    }
                    pgm = args[i];
                    break;
            }
        }

        if ((pgm is null) == (scanRoot is null))
        {
            throw new ArgumentException("Choose exactly one MAPDATA.PGM enable/inspection or one Workshop-root scan.");
        }
        if (inspect && scanRoot is not null)
        {
            throw new ArgumentException("--inspect cannot be combined with --scan.");
        }
        return new(pgm, scanRoot, cacheRoot, creator, inspect, false);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length) throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }
}
