using System.Globalization;
using System.Text.Json;

namespace CncRemasterWorkshopFixer;

public sealed class SteamWorkshopMetadataClient
{
    // Creator SteamID64 is not available in the local PGM or Workshop manifest.
    // Steam's public details endpoint is therefore the naming authority; no
    // login, API key, map payload, or telemetry is sent.
    private const string Endpoint = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(12);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    public SteamWorkshopMetadataClient(HttpClient httpClient, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "The request timeout must be positive.");
        }
    }

    public async Task<ulong> GetCreatorSteamIdAsync(ulong workshopItemId, CancellationToken cancellationToken = default)
    {
        var metadata = (await GetMetadataAsync([workshopItemId], cancellationToken)).Single();
        if (metadata.Status != SteamWorkshopMetadataStatus.Available || metadata.CreatorSteamId is null)
        {
            throw new InvalidDataException(metadata.Error ?? "Steam did not return a creator for this Workshop item.");
        }

        return metadata.CreatorSteamId.Value;
    }

    public async Task<IReadOnlyList<SteamWorkshopMetadata>> GetMetadataAsync(
        IEnumerable<ulong> workshopItemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workshopItemIds);
        var ids = workshopItemIds.Distinct().ToList();
        var results = new List<SteamWorkshopMetadata>(ids.Count);

        // Steam caps this endpoint at 100 items. A failed batch becomes retryable error
        // rows while later batches continue, so one network failure never hides packages
        // that parsed successfully or turns a metadata failure into a package failure.
        foreach (var batch in ids.Chunk(MaxBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.AddRange(await GetBatchAsync(batch, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                results.AddRange(batch.Select(itemId =>
                    SteamWorkshopMetadata.Failed(itemId, "Steam metadata request timed out. Refresh to retry.")));
            }
            catch (Exception exception)
            {
                results.AddRange(batch.Select(itemId => SteamWorkshopMetadata.Failed(itemId, exception.Message)));
            }
        }

        return results;
    }

    public static IReadOnlyList<SteamWorkshopMetadata> ParseResponse(
        string json,
        IEnumerable<ulong> requestedItemIds)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(requestedItemIds);

        using var document = JsonDocument.Parse(json);
        var details = document.RootElement.GetProperty("response").GetProperty("publishedfiledetails");
        var byId = new Dictionary<ulong, SteamWorkshopMetadata>();
        foreach (var detail in details.EnumerateArray())
        {
            if (!TryReadItemId(detail, out var itemId))
            {
                continue;
            }

            var result = detail.TryGetProperty("result", out var resultElement)
                ? resultElement.GetInt32()
                : (int?)null;
            var title = detail.TryGetProperty("title", out var titleElement)
                ? titleElement.GetString()
                : null;
            ulong? creator = null;
            if (detail.TryGetProperty("creator", out var creatorElement) &&
                ulong.TryParse(creatorElement.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCreator))
            {
                creator = parsedCreator;
            }

            byId[itemId] = result == 1 && creator is not null
                ? new SteamWorkshopMetadata(itemId, creator, title, SteamWorkshopMetadataStatus.Available, result, null)
                : new SteamWorkshopMetadata(
                    itemId, creator, title, SteamWorkshopMetadataStatus.Unavailable, result,
                    result is null ? "Steam returned incomplete Workshop metadata." : $"Steam returned result {result} for this Workshop item.");
        }

        // Preserve one result per requested ID even if Steam omits or reorders entries.
        return requestedItemIds.Distinct().Select(itemId =>
            byId.GetValueOrDefault(itemId) ??
            SteamWorkshopMetadata.Failed(itemId, "Steam omitted this Workshop item from its response.")).ToList();
    }

    private async Task<IReadOnlyList<SteamWorkshopMetadata>> GetBatchAsync(
        IReadOnlyList<ulong> workshopItemIds,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["itemcount"] = workshopItemIds.Count.ToString(CultureInfo.InvariantCulture)
        };
        for (var i = 0; i < workshopItemIds.Count; i++)
        {
            form[$"publishedfileids[{i}]"] = workshopItemIds[i].ToString(CultureInfo.InvariantCulture);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync(Endpoint, content, timeout.Token);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        return ParseResponse(json, workshopItemIds);
    }

    private static bool TryReadItemId(JsonElement detail, out ulong itemId)
    {
        itemId = 0;
        if (!detail.TryGetProperty("publishedfileid", out var idElement))
        {
            return false;
        }

        return idElement.ValueKind == JsonValueKind.String
            ? ulong.TryParse(idElement.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out itemId)
            : idElement.TryGetUInt64(out itemId);
    }
}

public enum SteamWorkshopMetadataStatus
{
    Available,
    Unavailable,
    Error
}

public sealed record SteamWorkshopMetadata(
    ulong WorkshopItemId,
    ulong? CreatorSteamId,
    string? Title,
    SteamWorkshopMetadataStatus Status,
    int? Result,
    string? Error)
{
    public static SteamWorkshopMetadata Failed(ulong workshopItemId, string error) =>
        new(workshopItemId, null, null, SteamWorkshopMetadataStatus.Error, null, error);
}

