namespace CncRemasterWorkshopFixer;

public sealed record DirectLocalInstalledFile(string Extension, string Path, string Sha256);

public sealed record DirectLocalInstallResult(
    string Basename,
    MapGame Game,
    string PackageSha256,
    IReadOnlyList<DirectLocalInstalledFile> Files,
    bool ReplacedOwnedFiles);

public enum DirectLocalOperationStage
{
    Prepare,
    Backup,
    Commit
}

public sealed class DirectLocalInstaller(
    Action<DirectLocalOperationStage, string>? beforeOperation = null,
    Func<string, string>? sourceHash = null,
    Func<string, FileStream>? createTemporaryFile = null)
{
    private readonly Func<string, string> _sourceHash = sourceHash ?? PackageHash.Sha256;
    // The factory must create a new file at this path; returning a stream transfers
    // cleanup ownership.
    private readonly Func<string, FileStream> _createTemporaryFile = createTemporaryFile ??
        (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough));

    public DirectLocalInstallResult Install(
        string steamPgmPath,
        ulong workshopItemId,
        ulong creatorSteamId,
        string localMapsRoot,
        KnownEnabledMap? existingOwnership = null)
    {
        var source = NativeUgcCacheManager.ValidateSteamPackage(steamPgmPath, workshopItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localMapsRoot);
        var packageSha256BeforeRead = _sourceHash(source);
        PgmArchive archive;
        MapGame game;
        try
        {
            archive = PgmArchive.Open(source);
            game = MapGameClassifier.Classify(archive.Members);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidDataException)
        {
            throw new UnsupportedMapLayoutException(
                "Direct Local Install requires a supported Tiberian Dawn or Red Alert map package. Use Native C&C Cache for this item.");
        }

        ValidateOwnership(existingOwnership, workshopItemId, creatorSteamId, game);
        var basename = WorkshopNaming.CreateBasename(creatorSteamId, workshopItemId);
        var destination = LocalMapFileRules.GetDestination(localMapsRoot, game);
        var allowedExtensions = LocalMapFileRules.ExtensionsFor(game);
        var members = archive.Members.ToDictionary(
            member => Path.GetExtension(member.Name).ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
        var payloads = allowedExtensions.Select(extension =>
        {
            var data = archive.Read(members[extension]);
            var path = LocalMapFileRules.ValidateExactTarget(destination, basename,
                Path.Combine(destination, basename + extension), allowedExtensions);
            return new PreparedPayload(extension, path, data, PackageHash.Sha256(data));
        }).ToList();

        // Hash the Steam package before and after reading its archive members.
        // Steam may replace Workshop content while the fixer is running; if the
        // source changes during extraction, abort rather than installing payload
        // bytes read across different package states.
        var packageSha256AfterRead = _sourceHash(source);
        if (!packageSha256BeforeRead.Equals(packageSha256AfterRead, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Steam Workshop package changed while it was being read. Refresh and try again after Steam finishes updating the item.");

        var replacing = existingOwnership is not null;
        if (!replacing && payloads.Any(payload => File.Exists(payload.Target)))
            throw new IOException("Direct Local Install found an existing local file at one of this map's exact target paths, but the fixer does not own it. Nothing was overwritten.");

        Directory.CreateDirectory(destination);
        // Stage beside the targets so commit and rollback use same-volume renames.
        // Every payload is prepared before any existing owned file is moved aside.
        var operationId = Guid.NewGuid().ToString("N");
        var temporary = new Dictionary<PreparedPayload, string>();
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var committed = new List<string>();
        try
        {
            foreach (var payload in payloads)
            {
                var path = Path.Combine(destination, $".{basename}.{operationId}{payload.Extension}.tmp");
                beforeOperation?.Invoke(DirectLocalOperationStage.Prepare, path);
                using (var output = _createTemporaryFile(path))
                {
                    // Own cleanup as soon as CreateNew succeeds, including partial
                    // writes or failed verification. A failed open owns no existing file.
                    temporary[payload] = path;
                    output.Write(payload.Data);
                    output.Flush(flushToDisk: true);
                }
                if (!PackageHash.Sha256(path).Equals(payload.Sha256, StringComparison.Ordinal))
                    throw new IOException("A prepared Direct Local file did not match its package member.");
            }

            if (replacing)
            {
                foreach (var payload in payloads.Where(payload => File.Exists(payload.Target)))
                {
                    var backup = Path.Combine(destination, $".{basename}.{operationId}{payload.Extension}.bak");
                    beforeOperation?.Invoke(DirectLocalOperationStage.Backup, payload.Target);
                    File.Move(payload.Target, backup);
                    backups[payload.Target] = backup;
                }
            }

            foreach (var payload in payloads)
            {
                beforeOperation?.Invoke(DirectLocalOperationStage.Commit, payload.Target);
                File.Move(temporary[payload], payload.Target);
                temporary.Remove(payload);
                committed.Add(payload.Target);
            }

            foreach (var payload in payloads)
                if (!PackageHash.Sha256(payload.Target).Equals(payload.Sha256, StringComparison.Ordinal))
                    throw new IOException("An installed Direct Local file did not match its package member.");

            foreach (var backup in backups.Values) File.Delete(backup);
            backups.Clear();
            return new DirectLocalInstallResult(basename, game, packageSha256BeforeRead,
                payloads.Select(payload => new DirectLocalInstalledFile(payload.Extension, payload.Target, payload.Sha256)).ToList(), replacing);
        }
        catch (Exception installException)
        {
            var rollbackErrors = new List<string>();
            foreach (var path in committed)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception exception) { rollbackErrors.Add($"Could not remove '{path}': {exception.Message}"); }
            }
            foreach (var pair in backups)
            {
                try
                {
                    if (File.Exists(pair.Value) && !File.Exists(pair.Key))
                        File.Move(pair.Value, pair.Key);
                    else if (File.Exists(pair.Value))
                        rollbackErrors.Add($"Backup retained at '{pair.Value}' because its target could not be cleared.");
                }
                catch (Exception exception)
                {
                    rollbackErrors.Add($"Backup retained at '{pair.Value}': {exception.Message}");
                }
            }
            if (rollbackErrors.Count > 0)
                throw new IOException("Direct Local installation failed and rollback needs attention. " +
                                      string.Join(" ", rollbackErrors), installException);
            throw;
        }
        finally
        {
            foreach (var path in temporary.Values)
                if (File.Exists(path)) File.Delete(path);
            // Backups are deleted and cleared on success. If rollback cannot restore one,
            // retain it rather than turning a recoverable failure into data loss.
        }
    }

    public static IReadOnlyList<string> RemoveOwned(
        string localMapsRoot, KnownEnabledMap ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (!KnownEnabledMapRegistry.IsValid(ownership) ||
            ownership.InstallationMethod != MapInstallMethod.DirectLocal || ownership.Game is null)
            throw new InvalidOperationException("The map does not have valid Direct Local ownership.");

        // Ownership permits explicit removal even after local edits. Rebuild the
        // exact paths from identity rather than trusting stored paths or a wildcard.
        var targets = LocalMapFileRules.GetExpectedFiles(localMapsRoot, ownership.Game.Value,
            ownership.CreatorSteamId, ownership.WorkshopItemId);
        var removed = new List<string>();
        foreach (var target in targets.Where(File.Exists))
        {
            File.Delete(target);
            removed.Add(target);
        }
        return removed;
    }

    private static void ValidateOwnership(KnownEnabledMap? ownership, ulong itemId, ulong creatorId, MapGame game)
    {
        if (ownership is null) return;
        if (!KnownEnabledMapRegistry.IsValid(ownership) ||
            ownership.InstallationMethod != MapInstallMethod.DirectLocal ||
            ownership.WorkshopItemId != itemId || ownership.CreatorSteamId != creatorId || ownership.Game != game)
            throw new InvalidOperationException("The existing Direct Local ownership record is not valid for this map.");
    }

    private sealed record PreparedPayload(string Extension, string Target, byte[] Data, string Sha256);
}
