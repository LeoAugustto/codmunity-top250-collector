using System;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CODMunityTop250Collector.Updater;

namespace CODMunityTop250Collector.ReleaseTool;

public sealed record ReleaseManifest
{
    [JsonPropertyOrder(0)] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyOrder(1)] public required string Channel { get; init; }
    [JsonPropertyOrder(2)] public required string Version { get; init; }
    [JsonPropertyOrder(3)] public required string MsiVersion { get; init; }
    [JsonPropertyOrder(4)] public required string MinimumUpdaterVersion { get; init; }
    [JsonPropertyOrder(5)] public required string Url { get; init; }
    [JsonPropertyOrder(6)] public required string Sha256 { get; init; }
    [JsonPropertyOrder(7)] public long Size { get; init; }
    [JsonPropertyOrder(8)] public DateTimeOffset PublishedAtUtc { get; init; }
    [JsonPropertyOrder(9)] public required string ReleaseNotes { get; init; }
}

public static class ManifestJson
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new UtcDateTimeOffsetConverter() }
    };

    public static byte[] Serialize(ReleaseManifest manifest)
    {
        ReleaseManifestValidator.Validate(manifest);
        return Utf8NoBom.GetBytes(JsonSerializer.Serialize(manifest, Options) + "\n");
    }

    public static ReleaseManifest Deserialize(ReadOnlySpan<byte> bytes)
    {
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(bytes, Options)
            ?? throw new InvalidDataException("Manifest is empty.");
        ReleaseManifestValidator.Validate(manifest);
        return manifest;
    }

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString() ?? throw new JsonException("publishedAtUtc is required.");
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
                throw new JsonException("publishedAtUtc is invalid.");
            return timestamp.ToUniversalTime();
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}

public static class ReleaseManifestValidator
{
    public static void Validate(ReleaseManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported schemaVersion.");
        if (!string.Equals(manifest.Channel, "private-alpha", StringComparison.Ordinal)) throw new InvalidDataException("Unsupported channel.");
        if (!IsVersion(manifest.Version) || !IsVersion(manifest.MinimumUpdaterVersion)) throw new InvalidDataException("Invalid application version.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.MsiVersion, "^\\d+\\.\\d+\\.\\d+$")) throw new InvalidDataException("Invalid MSI version.");
        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri) || !UpdateTrust.IsTrustedReleaseUri(uri)) throw new InvalidDataException("Release URL is not trusted.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Sha256, "^[0-9A-F]{64}$")) throw new InvalidDataException("SHA-256 must be uppercase hexadecimal.");
        if (manifest.Size <= 0) throw new InvalidDataException("MSI size must be positive.");
        if (manifest.PublishedAtUtc.Offset != TimeSpan.Zero) throw new InvalidDataException("publishedAtUtc must be UTC.");
        if (manifest.ReleaseNotes is null) throw new InvalidDataException("releaseNotes is required.");
    }

    private static bool IsVersion(string? value) => !string.IsNullOrWhiteSpace(value) &&
        System.Text.RegularExpressions.Regex.IsMatch(value, "^\\d+\\.\\d+\\.\\d+(-[0-9A-Za-z.-]+)?$");
}

public static class ReleaseSigning
{
    private const string PrivateKeyLabel = "ENCRYPTED PRIVATE KEY";
    private const string PublicKeyLabel = "PUBLIC KEY";
    private static readonly PbeParameters PrivateKeyEncryption = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200_000);

    public static byte[] ExportEncryptedPrivateKey(ECDsa key, ReadOnlySpan<char> passphrase) =>
        key.ExportEncryptedPkcs8PrivateKey(passphrase, PrivateKeyEncryption);

    public static ECDsa ImportEncryptedPrivateKey(ReadOnlySpan<byte> encryptedPkcs8, ReadOnlySpan<char> passphrase)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            key.ImportEncryptedPkcs8PrivateKey(passphrase, encryptedPkcs8, out var consumed);
            if (consumed != encryptedPkcs8.Length) throw new CryptographicException("Private key contains trailing data.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public static byte[] SignManifest(ECDsa key, ReadOnlySpan<byte> manifestBytes) =>
        key.SignData(manifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static bool VerifyManifest(ECDsa publicKey, ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signature) =>
        publicKey.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static string PublicKeyFingerprint(ReadOnlySpan<byte> subjectPublicKeyInfo) => Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo));

    public static void WriteKeyPair(string outputDirectory, ReadOnlySpan<char> passphrase, bool force)
    {
        var privatePath = Path.Combine(outputDirectory, "codmunity-top250-release-private.pem");
        var publicPath = Path.Combine(outputDirectory, "codmunity-top250-release-public.pem");
        EnsureWritableTargets(force, privatePath, publicPath);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var encryptedPrivate = ExportEncryptedPrivateKey(key, passphrase);
        var publicDer = key.ExportSubjectPublicKeyInfo();
        try
        {
            WritePem(privatePath, PrivateKeyLabel, encryptedPrivate, force);
            WritePem(publicPath, PublicKeyLabel, publicDer, force);
            Console.WriteLine($"Private key: {privatePath}");
            Console.WriteLine($"Public key: {publicPath}");
            Console.WriteLine($"Public key SHA-256: {PublicKeyFingerprint(publicDer)}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedPrivate);
            CryptographicOperations.ZeroMemory(publicDer);
        }
    }

    public static ECDsa LoadEncryptedPrivateKey(string path, ReadOnlySpan<char> passphrase)
    {
        var encryptedPrivate = ReadPem(path, PrivateKeyLabel);
        try { return ImportEncryptedPrivateKey(encryptedPrivate, passphrase); }
        finally { CryptographicOperations.ZeroMemory(encryptedPrivate); }
    }

    public static ECDsa LoadPublicKey(string path)
    {
        var publicDer = ReadPem(path, PublicKeyLabel);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            key.ImportSubjectPublicKeyInfo(publicDer, out var consumed);
            if (consumed != publicDer.Length) throw new CryptographicException("Public key contains trailing data.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(publicDer); }
    }

    public static void VerifyReleaseFiles(string manifestPath, string signaturePath, string publicKeyPath, string msiPath)
    {
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var signature = ReadBase64Signature(signaturePath);
        try
        {
            using var publicKey = LoadPublicKey(publicKeyPath);
            if (!VerifyManifest(publicKey, manifestBytes, signature)) throw new CryptographicException("Manifest signature is invalid.");
            var manifest = ManifestJson.Deserialize(manifestBytes);
            var info = new FileInfo(msiPath);
            if (!info.Exists) throw new FileNotFoundException("MSI file was not found.", msiPath);
            if (info.Length != manifest.Size) throw new InvalidDataException("MSI size does not match manifest.");
            var expected = Convert.FromHexString(manifest.Sha256);
            var actual = ComputeSha256(msiPath);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new CryptographicException("MSI SHA-256 does not match manifest.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(actual);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(manifestBytes);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    public static byte[] ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(stream);
    }

    public static void WriteManifestAndSignature(string outputDirectory, ReleaseManifest manifest, ECDsa privateKey, bool force)
    {
        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        var signaturePath = Path.Combine(outputDirectory, "manifest.sig");
        EnsureWritableTargets(force, manifestPath, signaturePath);
        var manifestBytes = ManifestJson.Serialize(manifest);
        var signature = SignManifest(privateKey, manifestBytes);
        var publicDer = privateKey.ExportSubjectPublicKeyInfo();
        try
        {
            using var publicKey = ECDsa.Create();
            publicKey.ImportSubjectPublicKeyInfo(publicDer, out _);
            if (!VerifyManifest(publicKey, manifestBytes, signature)) throw new CryptographicException("Immediate signature verification failed.");
            WriteNewFile(manifestPath, manifestBytes, force);
            WriteNewFile(signaturePath, Encoding.ASCII.GetBytes(Convert.ToBase64String(signature) + "\n"), force);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(manifestBytes);
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(publicDer);
        }
    }

    private static void EnsureWritableTargets(bool force, params string[] paths)
    {
        foreach (var path in paths)
            if (File.Exists(path) && !force) throw new IOException($"Refusing to overwrite existing file: {path}. Use --force to replace it.");
    }

    private static void WritePem(string path, string label, ReadOnlySpan<byte> der, bool force) =>
        WriteNewFile(path, Encoding.UTF8.GetBytes(new string(PemEncoding.Write(label, der))), force);

    private static void WriteNewFile(string path, byte[] bytes, bool force)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, force);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] ReadPem(string path, string expectedLabel)
    {
        var pem = File.ReadAllText(path, Encoding.UTF8);
        var fields = PemEncoding.Find(pem);
        if (!string.Equals(pem[fields.Label], expectedLabel, StringComparison.Ordinal)) throw new InvalidDataException($"Expected {expectedLabel} PEM.");
        return Convert.FromBase64String(pem[fields.Base64Data]);
    }

    private static byte[] ReadBase64Signature(string path) => Convert.FromBase64String(File.ReadAllText(path, Encoding.ASCII).Trim());
}
