using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CODMunityTop250Collector.Diagnostics;

namespace CODMunityTop250Collector.Core;

public interface IApiTokenProvider { string? GetToken(); }
public sealed class EnvironmentApiTokenProvider : IApiTokenProvider
{
    public string? GetToken() => Environment.GetEnvironmentVariable("CODMUNITY_RANKED_SYNC_TOKEN");
}
public sealed class CompositeApiTokenProvider(params IApiTokenProvider[] providers) : IApiTokenProvider
{
    public string? GetToken()
    {
        foreach (var provider in providers)
        {
            var token = provider.GetToken();
            if (!string.IsNullOrWhiteSpace(token)) return token;
        }
        return null;
    }
}

public sealed record RankedPlayer(int Position, int Sr, string Name);
public enum ApiSyncState { NotConfigured, Ok, Failed, Skipped }
public sealed record ApiSyncResult(ApiSyncState State, int? StatusCode = null, string? Reason = null);

public sealed class RankedPlayersApiClient : IDisposable
{
    internal static readonly Uri Endpoint = new("https://api.codmunity.gg/ranked-players/sync", UriKind.Absolute);
    private readonly HttpClient client;
    private readonly IApiTokenProvider tokenProvider;
    private readonly AppLogger log;
    private readonly bool ownsClient;

    public RankedPlayersApiClient(IApiTokenProvider tokenProvider, AppLogger log, HttpMessageHandler? handler = null)
    {
        this.tokenProvider = tokenProvider; this.log = log;
        client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        client.Timeout = TimeSpan.FromSeconds(15); ownsClient = true;
    }

    // Native position is COD's display rank; ties and gaps are valid here.
    public static bool TryValidateNativeLeaderboard(IEnumerable<RankedPlayer>? players, out IReadOnlyList<RankedPlayer> validated, out string reason)
    {
        var all = players?.ToArray() ?? Array.Empty<RankedPlayer>();
        if (all.Length is < 1 or > 250) { validated = Array.Empty<RankedPlayer>(); reason = "players-count-invalid"; return false; }
        var minPosition = all.Min(p => p.Position); var maxPosition = all.Max(p => p.Position);
        if (all.Any(p => p.Position is < 1 or > 250)) { validated = Array.Empty<RankedPlayer>(); reason = $"positions-invalid rows={all.Length} minPosition={minPosition} maxPosition={maxPosition}"; return false; }
        // COD leaderboard positions are display ranks: SR ties may repeat a position and create gaps.
        for (var index = 1; index < all.Length; index++)
        {
            if (all[index].Position < all[index - 1].Position) { validated = Array.Empty<RankedPlayer>(); reason = $"positions-invalid rows={all.Length} minPosition={minPosition} maxPosition={maxPosition}"; return false; }
            if (all[index].Sr > all[index - 1].Sr) { validated = Array.Empty<RankedPlayer>(); reason = "sr-order-invalid"; return false; }
        }
        if (all.Any(p => p.Sr <= 0)) { validated = Array.Empty<RankedPlayer>(); reason = "sr-invalid"; return false; }
        if (all.Any(p => string.IsNullOrWhiteSpace(p.Name))) { validated = Array.Empty<RankedPlayer>(); reason = "name-invalid"; return false; }
        validated = all; reason = ""; return true;
    }

    // The API receives only the currently populated ordinal slots; native display ranks may contain ties and gaps.
    public static IReadOnlyList<RankedPlayer> MapToApiOrdinalPositions(IReadOnlyList<RankedPlayer> nativeRows) =>
        nativeRows.Select((player, index) => new RankedPlayer(index + 1, player.Sr, player.Name)).ToArray();

    public static bool TryValidateApiRequestPlayers(IEnumerable<RankedPlayer>? players, out IReadOnlyList<RankedPlayer> validated, out string reason)
    {
        var all = players?.ToArray() ?? Array.Empty<RankedPlayer>();
        if (all.Length is < 1 or > 250) { validated = Array.Empty<RankedPlayer>(); reason = "players-count-invalid"; return false; }
        for (var index = 0; index < all.Length; index++)
        {
            if (all[index].Position != index + 1) { validated = Array.Empty<RankedPlayer>(); reason = "api-positions-invalid"; return false; }
            if (all[index].Sr <= 0) { validated = Array.Empty<RankedPlayer>(); reason = "sr-invalid"; return false; }
            if (string.IsNullOrWhiteSpace(all[index].Name)) { validated = Array.Empty<RankedPlayer>(); reason = "name-invalid"; return false; }
        }
        validated = all; reason = ""; return true;
    }

    public static bool TryValidatePlayers(IEnumerable<RankedPlayer>? players, out IReadOnlyList<RankedPlayer> validated, out string reason) =>
        TryValidateNativeLeaderboard(players, out validated, out reason);

    public async Task<ApiSyncResult> SyncAsync(string mode, IEnumerable<RankedPlayer>? players, CancellationToken token)
    {
        var sourceRows = players?.ToArray() ?? Array.Empty<RankedPlayer>();
        if (sourceRows.Length == 0) { log.Write("API_SYNC_SKIPPED", $"mode={mode} reason=empty-leaderboard"); return new(ApiSyncState.Skipped, Reason: "empty-leaderboard"); }
        if (!TryValidateNativeLeaderboard(sourceRows, out var nativeRows, out var reason)) { log.Write("API_SYNC_SKIPPED", $"mode={mode} reason={reason}"); return new(ApiSyncState.Skipped, Reason: reason); }
        var apiRows = MapToApiOrdinalPositions(nativeRows);
        if (!TryValidateApiRequestPlayers(apiRows, out var valid, out reason)) { log.Write("API_SYNC_SKIPPED", $"mode={mode} reason={reason}"); return new(ApiSyncState.Skipped, Reason: reason); }
        var bearer = tokenProvider.GetToken();
        if (string.IsNullOrWhiteSpace(bearer)) { log.Write("API_TOKEN_NOT_CONFIGURED"); return new(ApiSyncState.NotConfigured); }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                log.Write("API_SYNC_START", $"mode={mode} players={valid.Count}");
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                request.Content = new StringContent(JsonSerializer.Serialize(new { mode, players = valid.Select(p => new { position = p.Position, sr = p.Sr, name = p.Name }) }), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var duration = Stopwatch.GetElapsedTime(started).Milliseconds;
                if ((int)response.StatusCode is >= 200 and <= 299) { log.Write("API_SYNC_OK", $"mode={mode} status={(int)response.StatusCode} durationMs={duration}"); return new(ApiSyncState.Ok, (int)response.StatusCode); }
                if (attempt == 0 && IsTransient(response.StatusCode)) { log.Write("API_SYNC_RETRY", $"mode={mode} reason=status-{(int)response.StatusCode}"); continue; }
                log.Write("API_SYNC_FAILED", $"mode={mode} status={(int)response.StatusCode} durationMs={duration}"); return new(ApiSyncState.Failed, (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
            {
                var diagnostic = NetworkFailureDiagnostics.Format(ex, token); if (attempt == 0) { log.Write("API_SYNC_RETRY", $"mode={mode} attempt={attempt + 1} {diagnostic}"); continue; }
                log.Write("API_SYNC_FAILED", $"mode={mode} durationMs={Stopwatch.GetElapsedTime(started).Milliseconds} {diagnostic}"); return new(ApiSyncState.Failed, Reason: ex.GetType().Name);
            }
        }
        return new(ApiSyncState.Failed);
    }
    private static bool IsTransient(HttpStatusCode code) => code is HttpStatusCode.RequestTimeout or (HttpStatusCode)429 || (int)code >= 500;
    public void Dispose() { if (ownsClient) client.Dispose(); }
}
