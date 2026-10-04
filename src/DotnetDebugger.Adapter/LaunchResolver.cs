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
            string? msbuild = FindMSBuildFor(projectFile, output);
            string? framework = args.Framework;
            if (program == null)
                (program, framework) = GetTargetPath(msbuild, projectFile, args.Configuration, args.Framework, output);
            if (args.Build)
                Build(msbuild, projectFile, args.Configuration, framework, output);
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
        // checked here because the platforms disagree: Windows refuses to start the process, a Unix shell starts it elsewhere
        if (!Directory.Exists(workingDirectory))
            throw new DebuggerException($"The working directory '{workingDirectory}' does not exist.");

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

    // A non-SDK-style project is built with MSBuild.exe of Visual Studio (see MSBuildLocator); null: with `dotnet`.
    private static string? FindMSBuildFor(string projectFile, Action<string> output)
    {
        if (!MSBuildLocator.IsLegacyProject(projectFile))
            return null;
        if (MSBuildLocator.MSBuildExe is { } msbuild)
            return msbuild;
        output($"{Path.GetFileName(projectFile)} is not an SDK-style project and MSBuild.exe of Visual Studio was not found: trying `dotnet`, " +
            $"which builds such projects only when they need nothing from Visual Studio.{Environment.NewLine}");
        return null;
    }

    private static void Build(string? msbuild, string projectFile, string? configuration, string? framework, Action<string> output)
    {
        List<string> arguments;
        if (msbuild != null)
        {
            output($"Building {Path.GetFileName(projectFile)} with {msbuild}...{Environment.NewLine}");
            // packages.config is restored too (MSBuild 16.5+), PackageReference anyway
            arguments = [projectFile, "-nologo", "-v:q", "-clp:NoSummary", "-nodeReuse:false", "-restore", "-p:RestorePackagesConfig=true"];
            if (!string.IsNullOrEmpty(configuration))
                arguments.Add("-p:Configuration=" + configuration);
        }
        else
        {
            output($"Building {Path.GetFileName(projectFile)}...{Environment.NewLine}");
            arguments = ["build", projectFile, "-nologo", "-v", "q", "-clp:NoSummary", "-nodeReuse:false"];
            if (!string.IsNullOrEmpty(configuration))
                arguments.AddRange(["-c", configuration]);
            if (!string.IsNullOrEmpty(framework))
                arguments.AddRange(["-f", framework]);
        }
        (int exitCode, _) = RunDotnet(arguments, line => output(line + Environment.NewLine), msbuild);
        if (exitCode != 0)
            throw new DebuggerException($"The build of '{Path.GetFileName(projectFile)}' failed (exit code {exitCode}); see the debug console.");
    }

    /// <summary>
    /// The output assembly, evaluated (not built). A project with several target frameworks has none of its own: the
    /// requested one is used, else the first one this machine can run (the one Visual Studio debugs, too).
    /// </summary>
    private static (string Path, string? Framework) GetTargetPath(string? msbuild, string projectFile, string? configuration, string? framework,
        Action<string> output)
    {
        string name = Path.GetFileName(projectFile);
        Dictionary<string, string> properties = GetProperties(msbuild, projectFile, configuration, null);
        string targetFrameworks = properties.GetValueOrDefault("TargetFrameworks", "").Trim();
        string[] frameworks = targetFrameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (frameworks.Length == 0)
        {
            string single = properties.GetValueOrDefault("TargetFramework", "");
            if (!string.IsNullOrEmpty(framework) && single.Length > 0 && !string.Equals(framework, single, StringComparison.OrdinalIgnoreCase))
                throw new DebuggerException($"'{name}' does not target '{framework}': it targets {single}.");
        }
        else
        {
            if (string.IsNullOrEmpty(framework))
            {
                framework = frameworks.FirstOrDefault(CanRunHere) ?? frameworks[0];
                if (frameworks.Length > 1)
                    output($"{name} targets several frameworks ({targetFrameworks}): debugging {framework}. Set \"framework\" to choose another one.{Environment.NewLine}");
            }
            else if (!frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase))
            {
                throw new DebuggerException($"'{name}' does not target '{framework}': it targets {targetFrameworks}.");
            }
            properties = GetProperties(msbuild, projectFile, configuration, framework);
        }

        string targetPath = properties.GetValueOrDefault("TargetPath", "");
        if (targetPath.Length == 0)
            throw new DebuggerException($"Could not determine the output assembly of '{name}'.");
        return (targetPath, framework);

        // .NET Framework only runs on Windows, and a library targets nothing to run
        static bool CanRunHere(string tfm) =>
            !tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)
            && (OperatingSystem.IsWindows() || !(tfm.Length > 3 && char.IsDigit(tfm[3]) && !tfm.Contains('.')));
    }

    // Several properties come as JSON, in which anything that is not ASCII is escaped: MSBuild.exe writes to a pipe in
    // the OEM code page, so a plain path would not survive.
    private static Dictionary<string, string> GetProperties(string? msbuild, string projectFile, string? configuration, string? framework)
    {
        var arguments = new List<string> { projectFile, "-nologo", "-getProperty:TargetPath", "-getProperty:TargetFramework", "-getProperty:TargetFrameworks" };
        if (msbuild == null)
            arguments.Insert(0, "msbuild");
        if (!string.IsNullOrEmpty(configuration))
            arguments.Add("-p:Configuration=" + configuration);
        if (!string.IsNullOrEmpty(framework))
            arguments.Add("-p:TargetFramework=" + framework);
        (int exitCode, string text) = RunDotnet(arguments, null, msbuild);

        string name = Path.GetFileName(projectFile);
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        if (exitCode == 0 && start >= 0 && end > start)
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(text[start..(end + 1)]);
                return json.RootElement.GetProperty("Properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }
        }
        if (msbuild != null && text.Contains("MSB1001", StringComparison.Ordinal))
            throw new DebuggerException($"{msbuild} cannot report the output assembly of '{name}' (MSBuild 17.8 or later can): set \"program\" to it.");
        throw new DebuggerException($"Could not determine the output assembly of '{name}': {text.Trim()}");
    }

    /// <param name="msbuild">MSBuild.exe to run instead of `dotnet`.</param>
    private static (int ExitCode, string Output) RunDotnet(List<string> arguments, Action<string>? onLine, string? msbuild = null)
    {
        var startInfo = new ProcessStartInfo(msbuild ?? "dotnet")
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
        startInfo.Environment["VSLANG"] = "1033"; // the same for MSBuild.exe
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        // build servers outliving the command would keep our pipes open and WaitForExit waiting
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["UseSharedCompilation"] = "false";

        var collected = new StringBuilder();
        using Process process = Process.Start(startInfo) ?? throw new DebuggerException($"Failed to start '{startInfo.FileName}'.");
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
