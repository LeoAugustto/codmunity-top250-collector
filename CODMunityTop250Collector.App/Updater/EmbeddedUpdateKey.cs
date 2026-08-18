using System.Security.Cryptography;
using System.IO;
using CODMunityTop250Collector.Updater;

namespace CODMunityTop250Collector.App;

public static class EmbeddedUpdateKey
{
    private const string ResourceName = "CODMunityTop250Collector.App.Updater.codmunity-top250-release-public.pem";
    public static byte[] LoadSubjectPublicKeyInfo()
    {
        using var stream = typeof(EmbeddedUpdateKey).Assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException("Embedded update public key is unavailable.");
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();
        var fields = PemEncoding.Find(pem);
        if (!string.Equals(pem[fields.Label], "PUBLIC KEY", StringComparison.Ordinal)) throw new CryptographicException("Embedded update public key is invalid.");
        var der = Convert.FromBase64String(pem[fields.Base64Data]);
        if (!string.Equals(UpdateTrust.PublicKeyFingerprint(der), UpdateTrust.ExpectedPublicKeyFingerprint, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(der);
            throw new CryptographicException("Embedded update public key fingerprint mismatch.");
        }
        return der;
    }
}
