using System.Reflection.Metadata;

namespace DotnetDebugger.Engine.Symbols;

public sealed class SymbolOptions
{
    /// <summary>Directories (flat, or in symbol store layout) and http(s) symbol servers, in search order.</summary>
    public IReadOnlyList<string> SearchPaths { get; init; } = [];

    /// <summary>Where downloaded symbols are kept. Default: a per-user directory.</summary>
    public string? CachePath { get; init; }

    public bool SearchMicrosoftSymbolServer { get; init; }
    public bool SearchNuGetOrgSymbolServer { get; init; }
}

/// <summary>
/// Finds the portable PDB of a module that does not have it next to it. Symbol stores and servers use the
/// "simple symbol query protocol" key:  &lt;pdb name&gt;/&lt;pdb guid&gt;FFFFFFFF/&lt;pdb name&gt;.
/// </summary>
internal sealed class SymbolLocator
{
    private const string MicrosoftSymbolServer = "https://msdl.microsoft.com/download/symbols";
    private const string NuGetSymbolServer = "https://symbols.nuget.org/download/symbols";

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly SymbolOptions _options;
    private readonly string _cache;
    private readonly Action<string>? _log;
    private readonly HashSet<string> _notOnServers = new(StringComparer.OrdinalIgnoreCase);

    public SymbolLocator(SymbolOptions options, Action<string>? log)
    {
        _options = options;
        _log = log;
        _cache = options.CachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "dotnet-debugger", "symbols");
    }

    /// <param name="allowServers">Whether slow sources (http) may be asked; directories and the cache always are.</param>
    /// <returns>The PDB and whether it is "foreign" (from a cache or server rather than a directory the user named).</returns>
    /// <param name="includePublicServers">Also ask the Microsoft and NuGet.org servers even if the options do not name them.</param>
    public (MetadataReaderProvider Provider, bool Foreign)? Find(ModuleMetadata module, bool allowServers, bool includePublicServers = false)
    {
        if (module.GetPdbIdentity() is not { } identity)
            return null;
        string key = Path.Combine(identity.FileName, identity.Id.ToString("N") + "FFFFFFFF", identity.FileName);

        foreach (string directory in _options.SearchPaths.Where(p => !IsUrl(p)))
        {
            foreach (string candidate in new[] { Path.Combine(directory, key), Path.Combine(directory, identity.FileName) })
            {
                if (TryOpen(candidate) is { } provider)
                    return (provider, false);
            }
        }

        string cached = Path.Combine(_cache, key);
        if (TryOpen(cached) is { } fromCache)
            return (fromCache, true);

        if (!allowServers || (!includePublicServers && _notOnServers.Contains(key)))
            return null;

        IEnumerable<string> servers = _options.SearchPaths.Where(IsUrl);
        if (_options.SearchMicrosoftSymbolServer || includePublicServers)
            servers = servers.Append(MicrosoftSymbolServer);
        if (_options.SearchNuGetOrgSymbolServer || includePublicServers)
            servers = servers.Append(NuGetSymbolServer);

        foreach (string server in servers)
        {
            if (Download(server.TrimEnd('/') + "/" + key.Replace('\\', '/').ToLowerInvariant(), cached) && TryOpen(cached) is { } downloaded)
                return (downloaded, true);
        }
        _notOnServers.Add(key); // do not ask again during this session
        return null;
    }

    private static bool IsUrl(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private MetadataReaderProvider? TryOpen(string path)
    {
        try
        {
            return File.Exists(path) ? MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(path)) : null;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Cannot read symbols from '{path}': {e.Message}");
            return null;
        }
    }

    private bool Download(string url, string target)
    {
        try
        {
            using HttpResponseMessage response = s_http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (FileStream file = File.Create(temporary))
                response.Content.CopyToAsync(file).GetAwaiter().GetResult();
            File.Move(temporary, target, overwrite: true);
            _log?.Invoke($"Downloaded symbols: {url}");
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Symbol download failed ({url}): {e.Message}");
            return false;
        }
    }
}
