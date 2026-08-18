using System.IO;
using System.Reflection;
using System.Text;
using CODMunityTop250Collector.Core;

namespace CODMunityTop250Collector.App;

// Internal / Closed Beta only. The resource is generated from the release environment and is not source controlled.
internal sealed class EmbeddedRankedSyncTokenProvider : IApiTokenProvider
{
    private const string ResourceName = "CODMunityTop250Collector.App.EmbeddedRankedSyncToken";

    public string? GetToken()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var token = reader.ReadToEnd().Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
