using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CODMunityTop250Collector.ReleaseTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException(Usage);
            var command = args[0].ToLowerInvariant();
            var options = CommandOptions.Parse(args[1..]);
            return command switch
            {
                "keygen" => Keygen(options),
                "create-manifest" => CreateManifest(options),
                "verify" => Verify(options),
                _ => throw new ArgumentException(Usage)
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static int Keygen(CommandOptions options)
    {
        var output = options.Require("--out");
        var first = ReadPassphrase("Passphrase: ");
        var second = ReadPassphrase("Confirm passphrase: ");
        try
        {
            if (first.Length == 0) throw new ArgumentException("Passphrase cannot be empty.");
            if (!PassphrasesMatch(first, second)) throw new ArgumentException("Passphrases do not match.");
            ReleaseSigning.WriteKeyPair(output, first, options.Has("--force"));
            return 0;
        }
        finally
        {
            Array.Clear(first);
            Array.Clear(second);
        }
    }

    private static int CreateManifest(CommandOptions options)
    {
        var msiPath = options.Require("--msi");
        var info = new FileInfo(msiPath);
        if (!info.Exists) throw new FileNotFoundException("MSI file was not found.", msiPath);
        var hash = ReleaseSigning.ComputeSha256(msiPath);
        var manifest = new ReleaseManifest
        {
            Channel = "private-alpha",
            Version = options.Require("--version"),
            MsiVersion = options.Require("--msi-version"),
            MinimumUpdaterVersion = options.Require("--minimum-updater-version"),
            Url = options.Require("--url"),
            Sha256 = Convert.ToHexString(hash),
            Size = info.Length,
            PublishedAtUtc = DateTimeOffset.UtcNow,
            ReleaseNotes = options.Require("--release-notes")
        };
        CryptographicOperations.ZeroMemory(hash);
        var passphrase = ReadPassphrase("Private key passphrase: ");
        try
        {
            using var privateKey = ReleaseSigning.LoadEncryptedPrivateKey(options.Require("--private-key"), passphrase);
            ReleaseSigning.WriteManifestAndSignature(options.Require("--out"), manifest, privateKey, options.Has("--force"));
            Console.WriteLine($"Manifest: {Path.Combine(options.Require("--out"), "manifest.json")}");
            Console.WriteLine($"Signature: {Path.Combine(options.Require("--out"), "manifest.sig")}");
            return 0;
        }
        finally { Array.Clear(passphrase); }
    }

    private static int Verify(CommandOptions options)
    {
        ReleaseSigning.VerifyReleaseFiles(options.Require("--manifest"), options.Require("--signature"), options.Require("--public-key"), options.Require("--msi"));
        Console.WriteLine("VERIFY_OK");
        return 0;
    }

    private static char[] ReadPassphrase(string prompt)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Passphrase input must be interactive.");
        Console.Write(prompt);
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return chars.ToArray();
    }

    private static bool PassphrasesMatch(ReadOnlySpan<char> first, ReadOnlySpan<char> second)
    {
        var leftChars = first.ToArray();
        var rightChars = second.ToArray();
        var left = Encoding.UTF8.GetBytes(leftChars);
        var right = Encoding.UTF8.GetBytes(rightChars);
        try { return CryptographicOperations.FixedTimeEquals(left, right); }
        finally
        {
            Array.Clear(leftChars);
            Array.Clear(rightChars);
            CryptographicOperations.ZeroMemory(left);
            CryptographicOperations.ZeroMemory(right);
        }
    }

    private const string Usage = "Usage: ReleaseTool keygen --out <directory> [--force]\n       ReleaseTool create-manifest --msi <path> --version <version> --msi-version <version> --minimum-updater-version <version> --url <https-url> --release-notes <text> --private-key <path> --out <directory> [--force]\n       ReleaseTool verify --manifest <path> --signature <path> --public-key <path> --msi <path>";
}

internal sealed class CommandOptions
{
    private readonly Dictionary<string, string?> values;
    private CommandOptions(Dictionary<string, string?> values) => this.values = values;

    public static CommandOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (!option.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument: {option}");
            if (!values.TryAdd(option, null)) throw new ArgumentException($"Duplicate option: {option}");
            if (option == "--force") continue;
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Value required for {option}.");
            values[option] = args[i];
        }
        return new CommandOptions(values);
    }

    public bool Has(string name) => values.ContainsKey(name);
    public string Require(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Required option is missing: {name}");
}
