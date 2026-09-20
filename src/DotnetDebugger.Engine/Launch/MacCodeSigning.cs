using System.Diagnostics;

namespace DotnetDebugger.Engine.Launch;

/// <summary>
/// macOS lets a debugger into a process only if its executable is signed with the "get-task-allow" entitlement. The
/// dotnet host is; an application's own executable (apphost, self-contained, single-file) usually is not. Attaching to
/// such a process does not fail: it blocks inside the system, and on a machine without a user to answer it takes every
/// later debug session down with it. So this is checked before the process is started.
/// </summary>
public static class MacCodeSigning
{
    private const string Entitlement = "com.apple.security.get-task-allow";

    /// <summary>
    /// What to start instead of <paramref name="program"/>: the application's dll (run by the dotnet host) when the
    /// executable cannot be debugged and is only a launcher for that dll. Throws when there is no such way out.
    /// </summary>
    public static string ResolveDebuggableProgram(string program, Action<string>? note)
    {
        if (!OperatingSystem.IsMacOS() || program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(program))
            return program;
        if (AllowsDebugging(program) != false)
            return program;

        string dll = Path.ChangeExtension(program, ".dll");
        if (File.Exists(dll) && File.Exists(Path.ChangeExtension(program, ".runtimeconfig.json")))
        {
            note?.Invoke($"'{Path.GetFileName(program)}' is not signed with the {Entitlement} entitlement, so macOS does not allow debugging it. " +
                $"Starting '{Path.GetFileName(dll)}' with the dotnet host instead.{Environment.NewLine}");
            return dll;
        }

        throw new DebuggerException(
            $"macOS does not allow debugging '{program}': the executable is not signed with the {Entitlement} entitlement. " +
            "Sign it for debugging: create a file debug.entitlements containing " +
            $"<plist version=\"1.0\"><dict><key>{Entitlement}</key><true/></dict></plist> " +
            $"and run: codesign --force --sign - --entitlements debug.entitlements \"{program}\"");
    }

    /// <returns>null when it cannot be told (no codesign tool, unreadable output): the launch is then attempted as is.</returns>
    public static bool? AllowsDebugging(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo("/usr/bin/codesign")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string argument in new[] { "-d", "--entitlements", "-", "--xml", executable })
                startInfo.ArgumentList.Add(argument);
            using Process codesign = Process.Start(startInfo)!;
            Task<string> error = codesign.StandardError.ReadToEndAsync();
            string output = codesign.StandardOutput.ReadToEnd();
            if (!codesign.WaitForExit(10000))
            {
                codesign.Kill();
                return null;
            }
            // not signed at all: no entitlements either
            if (codesign.ExitCode != 0)
                return error.Result.Contains("not signed", StringComparison.OrdinalIgnoreCase) ? false : null;

            int key = output.IndexOf(Entitlement, StringComparison.Ordinal);
            if (key < 0)
                return false;
            // <key>com.apple.security.get-task-allow</key><true/>
            int keyEnd = output.IndexOf("</key>", key, StringComparison.Ordinal);
            if (keyEnd < 0)
                return null;
            int value = output.IndexOf('<', keyEnd + 6);
            return value >= 0 && output.AsSpan(value).StartsWith("<true");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
