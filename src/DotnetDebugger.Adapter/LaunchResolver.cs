using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotnetDebugger.Engine;
using DotnetDebugger.Protocol;

namespace DotnetDebugger.Adapter;

/// <summary>What to start, after "project", "build" and launchSettings.json have been taken into account.</summary>
internal sealed record ResolvedLaunch(string Program, string[] Args, string WorkingDirectory, Dictionary<string, string?> Environment);

/// <summary>
/// Turns launch arguments into a concrete command. Lives in the adapter rather than in an editor extension so that
/// every DAP client gets project-based launching.
/// </summary>
internal static class LaunchResolver
{
    private static readonly string[] s_projectExtensions = [".csproj", ".fsproj", ".vbproj"];

    public static ResolvedLaunch Resolve(LaunchArguments args, Action<string> output)
    {
        string? projectFile = args.Project == null ? null : FindProjectFile(args.Project);
        string? program = args.Program;

        if (projectFile != null)
        {
            if (args.Build)
                Build(projectFile, args.Configuration, output);
            program ??= GetTargetPath(projectFile, args.Configuration);
        }
        if (string.IsNullOrEmpty(program))
            throw new DebuggerException("Either 'program' or 'project' is required for launch.");
        if (!File.Exists(program))
        {
            throw new DebuggerException(projectFile != null && !args.Build
                ? $"Program '{program}' does not exist. Build the project first or set \"build\": true."
                : $"Program '{program}' does not exist.");
        }

        LaunchProfile? profile = LoadProfile(args, projectFile);

        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (profile != null)
        {
            if (!string.IsNullOrEmpty(profile.ApplicationUrl))
                environment["ASPNETCORE_URLS"] = profile.ApplicationUrl;
            foreach (var (name, value) in profile.EnvironmentVariables)
                environment[name] = value;
        }
        foreach (var (name, value) in args.Env ?? [])
            environment[name] = value;

        // Precompiled (ReadyToRun) code is optimized and the runtime prefers it over jitting, whatever the debugger
        // asks for per module. For a program that was published that way the only switch is process wide.
        if (args.SuppressJitOptimizations ?? IsReadyToRun(program))
        {
            environment.TryAdd("DOTNET_ReadyToRun", "0");
            environment.TryAdd("DOTNET_TieredCompilation", "0");
            if (args.SuppressJitOptimizations == null)
                output("The program is compiled ReadyToRun: precompiled code is disabled for this session (\"suppressJitOptimizations\": false turns that off)." + Environment.NewLine);
        }

        string[] arguments = args.Args ?? (profile?.CommandLineArgs is { Length: > 0 } commandLine ? SplitCommandLine(commandLine) : []);

        string defaultDirectory = Path.GetDirectoryName(projectFile ?? Path.GetFullPath(program))!;
        string workingDirectory = args.Cwd ?? profile?.WorkingDirectory ?? defaultDirectory;
        if (!Path.IsPathRooted(workingDirectory))
            workingDirectory = Path.GetFullPath(Path.Combine(defaultDirectory, workingDirectory));

        return new ResolvedLaunch(Path.GetFullPath(program), arguments, workingDirectory, environment);
    }

    /// <summary>Whether the managed assembly behind <paramref name="program"/> carries precompiled native code.</summary>
    private static bool IsReadyToRun(string program)
    {
        try
        {
            string assembly = program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? program : Path.ChangeExtension(program, ".dll");
            if (!File.Exists(assembly))
                return false;
            using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(assembly));
            return pe.PEHeaders.CorHeader is { } header
                && ((header.Flags & System.Reflection.PortableExecutable.CorFlags.ILLibrary) != 0 || header.ManagedNativeHeaderDirectory.Size != 0);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- project

    private static string FindProjectFile(string project)
    {
        if (File.Exists(project))
            return Path.GetFullPath(project);
        if (Directory.Exists(project))
        {
            string[] candidates = Directory.GetFiles(project).Where(f => s_projectExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToArray();
            return candidates.Length switch
            {
                1 => Path.GetFullPath(candidates[0]),
                0 => throw new DebuggerException($"No project file found in '{project}'."),
                _ => throw new DebuggerException($"'{project}' contains several project files; specify one of them."),
            };
        }
        throw new DebuggerException($"Project '{project}' does not exist.");
    }

    private static void Build(string projectFile, string? configuration, Action<string> output)
    {
        output($"Building {Path.GetFileName(projectFile)}...{Environment.NewLine}");
        var arguments = new List<string> { "build", projectFile, "-nologo", "-v", "q", "-clp:NoSummary", "-nodeReuse:false" };
        if (!string.IsNullOrEmpty(configuration))
            arguments.AddRange(["-c", configuration]);
        (int exitCode, _) = RunDotnet(arguments, line => output(line + Environment.NewLine));
        if (exitCode != 0)
            throw new DebuggerException($"The build of '{Path.GetFileName(projectFile)}' failed (exit code {exitCode}); see the debug console.");
    }

    private static string GetTargetPath(string projectFile, string? configuration)
    {
        var arguments = new List<string> { "msbuild", projectFile, "-nologo", "-getProperty:TargetPath" };
        if (!string.IsNullOrEmpty(configuration))
            arguments.Add("-p:Configuration=" + configuration);
        (int exitCode, string stdout) = RunDotnet(arguments, null);
        string targetPath = stdout.Trim();
        if (exitCode != 0 || targetPath.Length == 0 || targetPath.Contains('\n'))
            throw new DebuggerException($"Could not determine the output assembly of '{Path.GetFileName(projectFile)}': {targetPath}");
        return targetPath;
    }

    private static (int ExitCode, string Output) RunDotnet(List<string> arguments, Action<string>? onLine)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // never let a child read the DAP stream
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        // build servers outliving the command would keep our pipes open and WaitForExit waiting
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["UseSharedCompilation"] = "false";

        var collected = new StringBuilder();
        using Process process = Process.Start(startInfo) ?? throw new DebuggerException("Failed to start 'dotnet'.");
        process.StandardInput.Close();
        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data == null)
                return;
            lock (collected)
                collected.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        }
        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return (process.ExitCode, collected.ToString());
    }

    // ---------------------------------------------------------------- launchSettings.json

    private sealed class LaunchSettingsFile
    {
        public Dictionary<string, LaunchProfile>? Profiles { get; set; }
    }

    private sealed class LaunchProfile
    {
        public string? CommandName { get; set; }
        public string? CommandLineArgs { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? ApplicationUrl { get; set; }
        public Dictionary<string, string?> EnvironmentVariables { get; set; } = [];
    }

    private static LaunchProfile? LoadProfile(LaunchArguments args, string? projectFile)
    {
        // "": explicitly disabled; null: the project's default profile if there is one
        if (args.LaunchSettingsProfile == "")
            return null;

        string? path = args.LaunchSettingsFilePath
            ?? (projectFile == null ? null : Path.Combine(Path.GetDirectoryName(projectFile)!, "Properties", "launchSettings.json"));
        if (path == null || !File.Exists(path))
        {
            if (args.LaunchSettingsProfile != null || args.LaunchSettingsFilePath != null)
                throw new DebuggerException($"Launch settings file '{path ?? "Properties/launchSettings.json"}' not found (profile '{args.LaunchSettingsProfile}').");
            return null;
        }

        LaunchSettingsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<LaunchSettingsFile>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            throw new DebuggerException($"'{path}' is not valid: {e.Message}");
        }

        Dictionary<string, LaunchProfile> profiles = file?.Profiles ?? [];
        if (args.LaunchSettingsProfile != null)
        {
            return profiles.TryGetValue(args.LaunchSettingsProfile, out LaunchProfile? named)
                ? named
                : throw new DebuggerException($"Launch profile '{args.LaunchSettingsProfile}' not found in '{path}'.");
        }
        return profiles.Values.FirstOrDefault(p => string.Equals(p.CommandName, "Project", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Splits like a shell would for simple cases: whitespace separates, double quotes group.</summary>
    internal static string[] SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false, hasToken = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                hasToken = true;
                i++;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                    result.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (hasToken)
            result.Add(current.ToString());
        return result.ToArray();
    }
}
