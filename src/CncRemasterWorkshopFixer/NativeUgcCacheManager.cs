namespace CncRemasterWorkshopFixer;

public sealed class NativeUgcCacheManager
{
    public NativeCacheInstallResult Install(
        string steamPgmPath,
        ulong workshopItemId,
        ulong creatorSteamId,
        string ugcCacheRoot)
    {
        var source = ValidateSteamPackage(steamPgmPath, workshopItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ugcCacheRoot);

        var cacheRoot = Path.GetFullPath(ugcCacheRoot);
        Directory.CreateDirectory(cacheRoot);
        var basename = WorkshopNaming.CreateBasename(creatorSteamId, workshopItemId);
        var target = GetCachePgmPath(cacheRoot, creatorSteamId, workshopItemId);

        if (File.Exists(target) && FilesAreIdentical(source, target))
        {
            return new NativeCacheInstallResult(basename, target, false);
        }

        var temporary = Path.Combine(cacheRoot, $".{basename}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(
                       temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 81920, FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            if (!FilesAreIdentical(source, temporary))
            {
                throw new IOException("The temporary C&C UGC cache copy did not match the Steam package.");
            }

            // The temporary file is in the destination directory, so the final rename is
            // atomic on the normal Windows filesystem and cannot expose a partial PGM.
            File.Move(temporary, target, overwrite: true);
            if (!FilesAreIdentical(source, target))
            {
                throw new IOException("The enabled C&C UGC cache package did not match the Steam package.");
            }

            return new NativeCacheInstallResult(basename, target, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public NativeCacheRemovalResult RemoveFromGame(
        string steamPgmPath,
        string ugcCacheRoot,
        string localMapsRoot,
        MapGame? game,
        ulong creatorSteamId,
        ulong workshopItemId)
    {
        _ = ValidateSteamPackage(steamPgmPath, workshopItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ugcCacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(localMapsRoot);

        return RemoveKnownFromGame(ugcCacheRoot, localMapsRoot, game, creatorSteamId, workshopItemId);
    }

    public NativeCacheRemovalResult RemoveKnownFromGame(
        string ugcCacheRoot,
        string localMapsRoot,
        MapGame? game,
        ulong creatorSteamId,
        ulong workshopItemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ugcCacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(localMapsRoot);
        var cacheRoot = Path.GetFullPath(ugcCacheRoot);
        var cachePgm = GetCachePgmPath(cacheRoot, creatorSteamId, workshopItemId);
        var candidates = new List<string> { cachePgm };
        // Unknown layouts can still have a native PGM. Without a known game,
        // there's no safe local payload set to delete, so leave local files alone.
        if (game is not null)
        {
            candidates.AddRange(LocalMapFileRules.GetExpectedFiles(
                localMapsRoot, game.Value, creatorSteamId, workshopItemId));
        }

        var removed = new List<string>();
        var missing = new List<string>();
        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                missing.Add(path);
                continue;
            }

            File.Delete(path);
            removed.Add(path);
        }

        return new NativeCacheRemovalResult(removed, missing, game is not null);
    }

    public static string GetDefaultCacheRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            throw new InvalidOperationException("Windows roaming AppData folder could not be resolved.");
        }

        return Path.Combine(appData, "CnCRemastered", "UGC");
    }

    public static string GetCachePgmPath(
        string ugcCacheRoot,
        ulong creatorSteamId,
        ulong workshopItemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ugcCacheRoot);
        var root = Path.GetFullPath(ugcCacheRoot);
        var basename = WorkshopNaming.CreateBasename(creatorSteamId, workshopItemId);
        return ValidateCacheTarget(root, Path.Combine(root, basename + ".PGM"), basename);
    }

    public static string GetDefaultLocalMapsRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            throw new InvalidOperationException("Windows Documents folder could not be resolved.");
        }

        return Path.Combine(documents, "CnCRemastered", "Local_Custom_Maps");
    }

    public static bool FilesAreIdentical(string firstPath, string secondPath)
    {
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        using var first = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var second = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> firstBuffer = stackalloc byte[8192];
        Span<byte> secondBuffer = stackalloc byte[8192];
        while (true)
        {
            var firstRead = first.Read(firstBuffer);
            var secondRead = second.Read(secondBuffer);
            if (firstRead != secondRead || !firstBuffer[..firstRead].SequenceEqual(secondBuffer[..secondRead]))
            {
                return false;
            }

            if (firstRead == 0)
            {
                return true;
            }
        }
    }

    internal static SourceComparison CompareAndHashSource(string sourcePath, string comparisonPath)
    {
        var sourceInfo = new FileInfo(sourcePath);
        var comparisonInfo = new FileInfo(comparisonPath);
        if (sourceInfo.Length != comparisonInfo.Length)
        {
            return new(false, null);
        }

        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var comparison = new FileStream(comparisonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // Inventory needs both equality and a registry hash. Compute them in one
        // pass; a mismatch leaves hashing the whole source to callers that need it.
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var sourceBuffer = new byte[81920];
        var comparisonBuffer = new byte[81920];
        while (true)
        {
            var sourceRead = source.Read(sourceBuffer);
            var comparisonRead = comparison.Read(comparisonBuffer);
            if (sourceRead != comparisonRead ||
                !sourceBuffer.AsSpan(0, sourceRead).SequenceEqual(comparisonBuffer.AsSpan(0, comparisonRead)))
            {
                return new(false, null);
            }

            if (sourceRead == 0)
            {
                return new(true, Convert.ToHexString(hash.GetHashAndReset()));
            }
            hash.AppendData(sourceBuffer, 0, sourceRead);
        }
    }

    internal static string ValidateSteamPackage(string steamPgmPath, ulong workshopItemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamPgmPath);
        var source = Path.GetFullPath(steamPgmPath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The Steam Workshop MAPDATA.PGM was not found.", source);
        }

        if (!Path.GetFileName(source).Equals("MAPDATA.PGM", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(Path.GetDirectoryName(source))!.Equals(
                workshopItemId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The package must be MAPDATA.PGM directly beneath its numeric Workshop item directory.");
        }

        if (new FileInfo(source).Length < 24)
        {
            throw new InvalidDataException("The Steam Workshop MAPDATA.PGM is too small to contain a valid package header.");
        }

        // A package with the known raw Format #3 signature must pass structural
        // validation. Unknown signatures remain copyable for forward compatibility,
        // and known-but-unsupported member storage remains an inspection limitation.
        Span<byte> signature = stackalloc byte[8];
        using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.ReadExactly(signature);
        }
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(signature) == 0xFFFFFFFFu &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(signature[4..]) == 0x3F7D70A4u)
        {
            try { _ = PgmArchive.Open(source); }
            catch (NotSupportedException) { }
        }

        return source;
    }

    internal static string ValidateCacheTarget(string cacheRoot, string targetPath, string basename)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        var target = Path.GetFullPath(targetPath);
        var expected = Path.GetFullPath(Path.Combine(root, basename + ".PGM"));
        if (!target.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(target)!.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing an operation outside the exact C&C UGC cache target.");
        }

        return target;
    }
}

public sealed record NativeCacheInstallResult(string Basename, string PgmPath, bool ContentChanged);

public sealed record NativeCacheRemovalResult(
    IReadOnlyList<string> RemovedFiles,
    IReadOnlyList<string> MissingFiles,
    bool LocalPayloadCleanupAttempted);

internal sealed record SourceComparison(bool AreIdentical, string? SourceSha256);
