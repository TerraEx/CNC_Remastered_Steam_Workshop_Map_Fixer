using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CncRemasterWorkshopFixer;

public sealed record ApplicationSettings(
    int Version,
    AppTheme Theme,
    string? ManualWorkshopRoot = null,
    MapInstallMethod DefaultInstallMethod = MapInstallMethod.NativeCache,
    AppAppearance Appearance = AppAppearance.Standard)
{
    public const int CurrentVersion = 3;

    // Keep the v3 conceptual order available to callers while retaining source
    // compatibility with the v1/v2 positional constructor order.
    public ApplicationSettings(int version, AppTheme theme, AppAppearance appearance,
        string? manualWorkshopRoot = null,
        MapInstallMethod defaultInstallMethod = MapInstallMethod.NativeCache)
        : this(version, theme, manualWorkshopRoot, defaultInstallMethod, appearance)
    {
    }

    public static ApplicationSettings Default { get; } = new(
        CurrentVersion, AppTheme.System, null, MapInstallMethod.NativeCache, AppAppearance.Standard);
}

public sealed class ApplicationSettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Path { get; } = System.IO.Path.GetFullPath(
        string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A settings path is required.", nameof(path))
            : path);

    public ApplicationSettings Load() => LoadCore(persistMigrations: true);

    // A rejected second instance still needs its theme, but mustn't migrate the
    // settings file while the owning instance is using it.
    public ApplicationSettings LoadReadOnly() => LoadCore(persistMigrations: false);

    private ApplicationSettings LoadCore(bool persistMigrations)
    {
        if (!File.Exists(Path))
        {
            return ApplicationSettings.Default;
        }

        try
        {
            // Close the settings file before a migration invokes the atomic writer;
            // an open Windows read handle would otherwise prevent replacing the file.
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var versionElement) ||
                !versionElement.TryGetInt32(out var version) ||
                version is < 1 or > ApplicationSettings.CurrentVersion ||
                !TryReadOptionalString(root, "manualWorkshopRoot", out var manualWorkshopRoot))
            {
                return ApplicationSettings.Default;
            }

            if (!TryReadLegacyTheme(root, "theme", out var theme, out var legacyAppearance))
            {
                return ApplicationSettings.Default;
            }

            if (version == 1)
            {
                var migrated = new ApplicationSettings(
                    ApplicationSettings.CurrentVersion, theme, manualWorkshopRoot,
                    MapInstallMethod.NativeCache, AppAppearance.Standard);
                if (persistMigrations)
                {
                    Save(migrated);
                }
                return migrated;
            }

            if (!TryReadEnum(root, "defaultInstallMethod", out MapInstallMethod installMethod))
            {
                return ApplicationSettings.Default;
            }

            // Version 2 stored faction appearances as themes. Split that preference
            // into Dark plus the faction so the old look survives the v3 migration.
            if (version == 2)
            {
                var migrated = new ApplicationSettings(
                    ApplicationSettings.CurrentVersion, theme, manualWorkshopRoot, installMethod, legacyAppearance);
                if (persistMigrations)
                {
                    Save(migrated);
                }
                return migrated;
            }

            if (!TryReadEnum(root, "appearance", out AppAppearance currentAppearance))
            {
                return ApplicationSettings.Default;
            }

            if (!TryReadEnum(root, "theme", out AppTheme currentTheme))
            {
                return ApplicationSettings.Default;
            }

            return new(ApplicationSettings.CurrentVersion, currentTheme, manualWorkshopRoot, installMethod, currentAppearance);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return ApplicationSettings.Default;
        }
    }

    public void Save(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? temporary = null;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(directory);
            temporary = System.IO.Path.Combine(
                directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                       temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream,
                    settings with { Version = ApplicationSettings.CurrentVersion }, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
            temporary = null;
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            // Settings are best-effort: a locked-down profile must not prevent the
            // already-selected in-memory preference from being used.
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (IsPersistenceFailure(exception)) { }
            }
        }
    }

    public void SaveTheme(AppTheme theme) => Save(Load() with { Theme = theme });

    public void SaveAppearance(AppAppearance appearance) => Save(Load() with { Appearance = appearance });

    public void SaveManualWorkshopRoot(string? manualWorkshopRoot) =>
        Save(Load() with { ManualWorkshopRoot = manualWorkshopRoot });

    public void SaveDefaultInstallMethod(MapInstallMethod method) =>
        Save(Load() with { DefaultInstallMethod = method });

    private static bool TryReadEnum<TEnum>(JsonElement root, string propertyName, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return root.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.String &&
               Enum.TryParse(element.GetString(), ignoreCase: true, out value) &&
               Enum.IsDefined(value);
    }

    private static bool TryReadLegacyTheme(JsonElement root, string propertyName,
        out AppTheme theme, out AppAppearance appearance)
    {
        theme = AppTheme.System;
        appearance = AppAppearance.Standard;
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return element.GetString()?.Trim().ToLowerInvariant() switch
        {
            "system" => true,
            "light" => SetLegacyTheme(AppTheme.Light, AppAppearance.Standard, out theme, out appearance),
            "dark" => SetLegacyTheme(AppTheme.Dark, AppAppearance.Standard, out theme, out appearance),
            "gdi" => SetLegacyTheme(AppTheme.Dark, AppAppearance.Gdi, out theme, out appearance),
            "nod" => SetLegacyTheme(AppTheme.Dark, AppAppearance.Nod, out theme, out appearance),
            "allies" => SetLegacyTheme(AppTheme.Dark, AppAppearance.Allies, out theme, out appearance),
            "soviets" => SetLegacyTheme(AppTheme.Dark, AppAppearance.Soviets, out theme, out appearance),
            _ => false
        };
    }

    private static bool SetLegacyTheme(AppTheme theme, AppAppearance appearance,
        out AppTheme parsedTheme, out AppAppearance parsedAppearance)
    {
        parsedTheme = theme;
        parsedAppearance = appearance;
        return true;
    }

    private static bool TryReadOptionalString(
        JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool IsReadFailure(Exception exception) =>
        IsPersistenceFailure(exception) || exception is JsonException or NotSupportedException;

    private static bool IsPersistenceFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
