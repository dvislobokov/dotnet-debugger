using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ClrDebug;

namespace DotnetDebugger.Engine.Launch;

/// <summary>
/// Tells dbgshim where the debugging libraries of a runtime are (mscordbi, mscordaccore). Normally they sit next to
/// coreclr, but a single-file application has the runtime linked into its executable and ships without them; they
/// then have to come from an installed runtime or a runtime pack of exactly the same build.
/// </summary>
[GeneratedComClass]
internal sealed partial class DebuggingLibraryProvider(Action<string>? log) : ICLRDebuggingLibraryProvider3
{
    public HRESULT ProvideWindowsLibrary(string pwszFileName, string pwszRuntimeModule, LIBRARY_PROVIDER_INDEX_TYPE indexType,
        uint dwTimestamp, int dwSizeOfImage, out IntPtr ppResolvedModulePath)
    {
        ppResolvedModulePath = IntPtr.Zero;
        try
        {
            string? found = Find(pwszFileName, pwszRuntimeModule, candidate => indexType switch
            {
                // "Identity": timestamp and size of the wanted file itself
                LIBRARY_PROVIDER_INDEX_TYPE.Identity => Matches(candidate, dwTimestamp, dwSizeOfImage),
                // "Runtime": they describe the runtime module; the library next to a matching coreclr is the one
                LIBRARY_PROVIDER_INDEX_TYPE.Runtime => Matches(Path.Combine(Path.GetDirectoryName(candidate)!, "coreclr.dll"), dwTimestamp, dwSizeOfImage),
                _ => true,
            });
            log?.Invoke($"Debugging library {pwszFileName} ({indexType}) for {pwszRuntimeModule}: {found ?? "not found"}");
            if (found == null)
                return HRESULT.E_FAIL;
            ppResolvedModulePath = Marshal.StringToCoTaskMemUni(found);
            return HRESULT.S_OK;
        }
        catch (Exception e)
        {
            log?.Invoke("Debugging library lookup failed: " + e.Message);
            return HRESULT.E_FAIL;
        }
    }

    public HRESULT ProvideUnixLibrary(string pwszFileName, string pwszRuntimeModule, LIBRARY_PROVIDER_INDEX_TYPE indexType,
        byte[] pbBuildId, int iBuildIdSize, out IntPtr ppResolvedModulePath)
    {
        ppResolvedModulePath = IntPtr.Zero;
        try
        {
            byte[] wanted = pbBuildId.AsSpan(0, Math.Min(iBuildIdSize, pbBuildId.Length)).ToArray();
            string runtimeLibrary = OperatingSystem.IsMacOS() ? "libcoreclr.dylib" : "libcoreclr.so";
            string? found = Find(pwszFileName, pwszRuntimeModule, candidate => indexType switch
            {
                // "Identity": the build id of the wanted library itself
                LIBRARY_PROVIDER_INDEX_TYPE.Identity => Same(BuildId.Read(candidate), wanted),
                // "Runtime": the build id of the runtime; the library next to a matching libcoreclr is the one
                LIBRARY_PROVIDER_INDEX_TYPE.Runtime => Same(BuildId.Read(Path.Combine(Path.GetDirectoryName(candidate)!, runtimeLibrary)), wanted),
                _ => true,
            });
            log?.Invoke($"Debugging library {pwszFileName} ({indexType}, build id {Convert.ToHexString(wanted)}) for {pwszRuntimeModule}: {found ?? "not found"}");
            if (found == null)
                return HRESULT.E_FAIL;
            ppResolvedModulePath = Marshal.StringToCoTaskMemUni(found);
            return HRESULT.S_OK;
        }
        catch (Exception e)
        {
            log?.Invoke("Debugging library lookup failed: " + e.Message);
            return HRESULT.E_FAIL;
        }
    }

    private static string? Find(string fileName, string runtimeModule, Func<string, bool> matches)
    {
        string name = Path.GetFileName(fileName);
        foreach (string directory in CandidateDirectories(runtimeModule))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate) && matches(candidate))
                return candidate;
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirectories(string runtimeModule)
    {
        // the usual place first: next to the runtime
        if (Path.GetDirectoryName(runtimeModule) is { Length: > 0 } next)
            yield return next;

        var roots = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.GetDirectoryName(Environment.ProcessPath),
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet") : "/usr/share/dotnet",
            OperatingSystem.IsWindows() ? null : "/usr/lib/dotnet",
            OperatingSystem.IsMacOS() ? "/usr/local/share/dotnet" : null,
        };
        foreach (string? root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct())
        {
            string shared = Path.Combine(root!, "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(shared))
                continue;
            foreach (string version in Directory.GetDirectories(shared).OrderByDescending(d => d))
                yield return version;
        }

        // runtime packs restored for self-contained and single-file publishing
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string rid = RuntimeInformation.RuntimeIdentifier;
        string pack = Path.Combine(packages, "microsoft.netcore.app.runtime." + rid);
        if (Directory.Exists(pack))
        {
            foreach (string version in Directory.GetDirectories(pack).OrderByDescending(d => d))
                yield return Path.Combine(version, "runtimes", rid, "native");
        }
    }

    private static bool Same(byte[]? actual, byte[] wanted) => actual != null && wanted.Length > 0 && actual.AsSpan().SequenceEqual(wanted);

    private static bool Matches(string file, uint timestamp, int sizeOfImage)
    {
        try
        {
            if (!File.Exists(file))
                return false;
            using var pe = new PEReader(File.OpenRead(file));
            return unchecked((uint)pe.PEHeaders.CoffHeader.TimeDateStamp) == timestamp && pe.PEHeaders.PEHeader?.SizeOfImage == sizeOfImage;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
