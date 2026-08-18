using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CODMunityTop250Collector.Updater;

public sealed record UpdateManifest(
    int SchemaVersion,
    string Channel,
    string Version,
    string MsiVersion,
    string MinimumUpdaterVersion,
    string Url,
    string Sha256,
    long Size,
    DateTimeOffset PublishedAtUtc,
    string ReleaseNotes);

public sealed record AvailableUpdate(UpdateManifest Manifest, AppSemanticVersion Version);
public enum UpdateCheckKind { None, Available, MinimumUpdaterVersionNotMet, Failed }
public sealed record UpdateCheckResult(UpdateCheckKind Kind, AvailableUpdate? Update = null, string? Detail = null);
public enum AutomaticUpdateOutcome { HandedOff, Failed, AlreadyInProgress }

public static class UpdateCheckSchedule
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RecurringInterval = TimeSpan.FromMinutes(10);
}

// Serializes automatic and user-initiated checks into one update workflow.
public sealed class UpdateOperationGate
{
    private int active;
    public bool IsActive => Volatile.Read(ref active) != 0;
    public bool TryBegin() => Interlocked.CompareExchange(ref active, 1, 0) == 0;
    public void End() => Interlocked.Exchange(ref active, 0);
}

public readonly record struct AppSemanticVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<AppSemanticVersion>
{
    private static readonly Regex Pattern = new("^(?<major>0|[1-9]\\d*)\\.(?<minor>0|[1-9]\\d*)\\.(?<patch>0|[1-9]\\d*)(?:-(?<pre>[0-9A-Za-z.-]+))?$", RegexOptions.CultureInvariant);
    public static bool TryParse(string? value, out AppSemanticVersion version)
    {
        var match = Pattern.Match(value ?? string.Empty);
        if (!match.Success) { version = default; return false; }
        version = new(int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture), match.Groups["pre"].Success ? match.Groups["pre"].Value : null);
        return true;
    }
    public int CompareTo(AppSemanticVersion other)
    {
        var numeric = Major.CompareTo(other.Major); if (numeric != 0) return numeric;
        numeric = Minor.CompareTo(other.Minor); if (numeric != 0) return numeric;
        numeric = Patch.CompareTo(other.Patch); if (numeric != 0) return numeric;
        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;
        return StringComparer.Ordinal.Compare(Prerelease, other.Prerelease);
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (string.IsNullOrEmpty(Prerelease) ? string.Empty : "-" + Prerelease);
}

public static class UpdateTrust
{
    public const string ExpectedPublicKeyFingerprint = "7906D6369851CC6F7064A8C4F69D4A244E78AAA1ED4CC9D65BE705C15640284C";
    public const string ObjectStorageHost = "gr5favu0uzxf.objectstorage.sa-saopaulo-1.oci.customer-oci.com";
    public const string LogicalReleasePathPrefix = "/n/gr5favu0uzxf/b/codmunity-updates/o/top250/releases/";
    private const string EncodedReleasePathPrefix = "/n/gr5favu0uzxf/b/codmunity-updates/o/top250%2Freleases%2F";
    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxSignatureBytes = 4 * 1024;
    public const long MaxMsiBytes = 128L * 1024 * 1024;
    public static readonly Uri ManifestUri = new("https://gr5favu0uzxf.objectstorage.sa-saopaulo-1.oci.customer-oci.com/n/gr5favu0uzxf/b/codmunity-updates/o/top250%2Fmanifest.json");
    public static readonly Uri SignatureUri = new("https://gr5favu0uzxf.objectstorage.sa-saopaulo-1.oci.customer-oci.com/n/gr5favu0uzxf/b/codmunity-updates/o/top250%2Fmanifest.sig");
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    public static string PublicKeyFingerprint(ReadOnlySpan<byte> subjectPublicKeyInfo) => Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo));
    public static ECDsa ImportPublicKey(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        var key = ECDsa.Create();
        try { key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var consumed); if (consumed != subjectPublicKeyInfo.Length) throw new CryptographicException("Public key contains trailing data."); return key; }
        catch { key.Dispose(); throw; }
    }
    public static bool VerifyManifest(ECDsa publicKey, ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signature) =>
        publicKey.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    public static bool IsTrustedReleaseUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, ObjectStorageHost, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;

        var escapedPath = "/" + uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimStart('/');
        if (escapedPath.Contains("%25", StringComparison.OrdinalIgnoreCase) ||
            !escapedPath.StartsWith(EncodedReleasePathPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        string logicalPath;
        try { logicalPath = Uri.UnescapeDataString(escapedPath); }
        catch (UriFormatException) { return false; }
        if (!logicalPath.StartsWith(LogicalReleasePathPrefix, StringComparison.Ordinal)) return false;

        var objectName = logicalPath[LogicalReleasePathPrefix.Length..];
        return objectName.Length > 0 && objectName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) &&
            !objectName.Contains('/') && !objectName.Contains('\\') && objectName is not "." and not "..";
    }
    public static byte[] DecodeSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxSignatureBytes) throw new InvalidDataException("Signature is outside the allowed size.");
        var text = Encoding.ASCII.GetString(bytes).Trim();
        return Convert.FromBase64String(text);
    }
}

public static class UpdateManifestSerializer
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Converters = { new UtcConverter() } };
    public static UpdateManifest Deserialize(ReadOnlySpan<byte> exactManifestBytes)
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(exactManifestBytes, Options) ?? throw new InvalidDataException("Manifest is empty.");
        UpdateManifestValidator.Validate(manifest);
        return manifest;
    }
    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString();
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) || timestamp.Offset != TimeSpan.Zero) throw new JsonException("publishedAtUtc must be a UTC timestamp.");
            return timestamp;
        }
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }
}

public static class UpdateManifestValidator
{
    private static readonly Regex MsiVersion = new("^\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant);
    public static void Validate(UpdateManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported manifest schema.");
        if (!string.Equals(manifest.Channel, "private-alpha", StringComparison.Ordinal)) throw new InvalidDataException("Unsupported update channel.");
        if (!AppSemanticVersion.TryParse(manifest.Version, out _) || !AppSemanticVersion.TryParse(manifest.MinimumUpdaterVersion, out _)) throw new InvalidDataException("Invalid application version.");
        if (!MsiVersion.IsMatch(manifest.MsiVersion)) throw new InvalidDataException("Invalid MSI version.");
        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri) || !UpdateTrust.IsTrustedReleaseUri(uri)) throw new InvalidDataException("Release URL is not trusted.");
        if (!Sha256.IsMatch(manifest.Sha256)) throw new InvalidDataException("Invalid MSI SHA-256.");
        if (manifest.Size is <= 0 or > UpdateTrust.MaxMsiBytes) throw new InvalidDataException("MSI size is outside the allowed range.");
        if (manifest.PublishedAtUtc.Offset != TimeSpan.Zero) throw new InvalidDataException("publishedAtUtc must be UTC.");
        if (manifest.ReleaseNotes is null) throw new InvalidDataException("releaseNotes is required.");
    }
}

public sealed class AutomaticUpdateCoordinator
{
    private int active;
    public bool IsActive => Volatile.Read(ref active) != 0;

    public async Task<AutomaticUpdateOutcome> RunAsync(
        AvailableUpdate update,
        Func<AvailableUpdate, CancellationToken, Task<string>> download,
        Action requestSafeState,
        Func<CancellationToken, Task> waitForSafeState,
        Action cancelSafeState,
        Action<string, string?> log,
        Action<string, string> startUpdateHost,
        CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) return AutomaticUpdateOutcome.AlreadyInProgress;
        var safeStateRequested = false;
        var handoffStarted = false;
        try
        {
            var msi = await download(update, token);
            requestSafeState(); safeStateRequested = true;
            await waitForSafeState(token);
            log("UPDATE_INSTALL_START", $"version={update.Version}");
            startUpdateHost(msi, update.Manifest.Sha256);
            handoffStarted = true;
            log("UPDATE_INSTALL_HANDOFF", $"version={update.Version}");
            return AutomaticUpdateOutcome.HandedOff;
        }
        catch (Exception ex)
        {
            log("UPDATE_AUTO_UPDATE_FAILED", $"reason={ex.GetType().Name}");
            return AutomaticUpdateOutcome.Failed;
        }
        finally
        {
            if (!handoffStarted)
            {
                if (safeStateRequested) cancelSafeState();
                Interlocked.Exchange(ref active, 0);
            }
        }
    }
}

public sealed class PrivateUpdateClient : IDisposable
{
    private readonly HttpClient client;
    private readonly ECDsa publicKey;
    private readonly Action<string, string?> log;
    private readonly bool ownsClient;

    public PrivateUpdateClient(byte[] subjectPublicKeyInfo, Action<string, string?> log, HttpMessageHandler? handler = null) : this(subjectPublicKeyInfo, log, handler, UpdateTrust.ExpectedPublicKeyFingerprint) { }
    internal PrivateUpdateClient(byte[] subjectPublicKeyInfo, Action<string, string?> log, HttpMessageHandler? handler, string expectedFingerprint)
    {
        var fingerprint = UpdateTrust.PublicKeyFingerprint(subjectPublicKeyInfo);
        if (!string.Equals(fingerprint, expectedFingerprint, StringComparison.Ordinal)) throw new CryptographicException("Embedded update public key fingerprint mismatch.");
        publicKey = UpdateTrust.ImportPublicKey(subjectPublicKeyInfo);
        this.log = log;
        client = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler, disposeHandler: true);
        client.Timeout = UpdateTrust.RequestTimeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CODMunityTop250Collector/0.1.6");
        ownsClient = true;
    }

    public async Task<UpdateCheckResult> CheckAsync(AppSemanticVersion currentVersion, CancellationToken cancellationToken)
    {
        log("UPDATE_CHECK_START", null);
        try
        {
            var manifestBytes = await GetLimitedAsync(UpdateTrust.ManifestUri, UpdateTrust.MaxManifestBytes, cancellationToken);
            var signatureBytes = await GetLimitedAsync(UpdateTrust.SignatureUri, UpdateTrust.MaxSignatureBytes, cancellationToken);
            byte[] signature;
            try { signature = UpdateTrust.DecodeSignature(signatureBytes); }
            finally { CryptographicOperations.ZeroMemory(signatureBytes); }
            try
            {
                if (!UpdateTrust.VerifyManifest(publicKey, manifestBytes, signature)) { log("UPDATE_MANIFEST_SIGNATURE_INVALID", null); return new(UpdateCheckKind.Failed, Detail: "signature"); }
                var manifest = UpdateManifestSerializer.Deserialize(manifestBytes);
                if (!AppSemanticVersion.TryParse(manifest.Version, out var latest) || !AppSemanticVersion.TryParse(manifest.MinimumUpdaterVersion, out var minimum)) throw new InvalidDataException("Invalid manifest version.");
                if (currentVersion.CompareTo(minimum) < 0) { log("UPDATE_MINIMUM_VERSION_NOT_MET", $"current={currentVersion} minimum={minimum}"); return new(UpdateCheckKind.MinimumUpdaterVersionNotMet); }
                if (latest.CompareTo(currentVersion) <= 0) { log("UPDATE_CHECK_OK", $"current={currentVersion} latest={latest}"); return new(UpdateCheckKind.None); }
                log("UPDATE_AVAILABLE", $"version={latest}"); return new(UpdateCheckKind.Available, new AvailableUpdate(manifest, latest));
            }
            finally { CryptographicOperations.ZeroMemory(manifestBytes); CryptographicOperations.ZeroMemory(signature); }
        }
        catch (UpdateNotPublishedException ex)
        {
            log("UPDATE_CHECK_NOT_PUBLISHED", $"status={(int)ex.StatusCode}");
            return new(UpdateCheckKind.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or FormatException or CryptographicException)
        {
            log("UPDATE_CHECK_FAILED", ex.GetType().Name);
            return new(UpdateCheckKind.Failed, Detail: ex.GetType().Name);
        }
    }

    public async Task<string> DownloadMsiAsync(AvailableUpdate update, string updatesDirectory, CancellationToken cancellationToken)
    {
        UpdateManifestValidator.Validate(update.Manifest);
        Directory.CreateDirectory(updatesDirectory);
        var target = Path.Combine(updatesDirectory, "CODMunityTop250Collector-update.msi");
        var temporary = target + ".download";
        try
        {
            log("UPDATE_DOWNLOAD_START", $"version={update.Manifest.Version}");
            using var response = await client.GetAsync(new Uri(update.Manifest.Url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            EnsureTrustedResponse(response, new Uri(update.Manifest.Url));
            if (response.Content.Headers.ContentLength is { } length && (length != update.Manifest.Size || length > UpdateTrust.MaxMsiBytes)) throw new InvalidDataException("MSI Content-Length is invalid.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920]; long total = 0; var nextProgressPercent = 10;
                try
                {
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, cancellationToken);
                        if (read == 0) break;
                        total += read;
                        if (total > update.Manifest.Size || total > UpdateTrust.MaxMsiBytes) throw new InvalidDataException("MSI exceeds the allowed size.");
                        var percent = (int)(total * 100 / update.Manifest.Size);
                        if (percent >= nextProgressPercent)
                        {
                            log("UPDATE_DOWNLOAD_PROGRESS", $"percent={Math.Min(percent, 100)}");
                            nextProgressPercent += 10;
                        }
                        hasher.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    var actualHash = hasher.GetHashAndReset();
                    var expectedHash = Convert.FromHexString(update.Manifest.Sha256);
                    try
                    {
                        if (total != update.Manifest.Size) throw new InvalidDataException("MSI size does not match manifest.");
                        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash)) throw new CryptographicException("MSI SHA-256 does not match manifest.");
                    }
                    finally { CryptographicOperations.ZeroMemory(actualHash); CryptographicOperations.ZeroMemory(expectedHash); }
                }
                finally { Array.Clear(buffer); }
            }
            File.Move(temporary, target, true);
            log("UPDATE_DOWNLOAD_OK", $"version={update.Manifest.Version}");
            return target;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            log("UPDATE_HASH_INVALID", null);
            throw;
        }
    }

    private async Task<byte[]> GetLimitedAsync(Uri uri, int maximum, CancellationToken token)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        EnsureTrustedResponse(response, uri);
        if (response.Content.Headers.ContentLength is { } length && (length < 0 || length > maximum)) throw new InvalidDataException("Response exceeds its allowed size.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, token); if (read == 0) break;
                if (output.Length + read > maximum) throw new InvalidDataException("Response exceeds its allowed size.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        finally { Array.Clear(buffer); }
    }

    private static void EnsureTrustedResponse(HttpResponseMessage response, Uri requestedUri)
    {
        if ((int)response.StatusCode is >= 300 and < 400) throw new HttpRequestException("Redirects are not accepted.");
        if (response.RequestMessage?.RequestUri is { } finalUri && (!string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !string.Equals(finalUri.Host, requestedUri.Host, StringComparison.OrdinalIgnoreCase) || !finalUri.IsDefaultPort)) throw new HttpRequestException("Response host is not trusted.");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new UpdateNotPublishedException(response.StatusCode);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() { publicKey.Dispose(); if (ownsClient) client.Dispose(); }
}

internal sealed class UpdateNotPublishedException(HttpStatusCode statusCode) : IOException("No signed update manifest has been published.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
