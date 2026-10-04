using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace DotnetDebugger.Adapter;

/// <summary>
/// Non-SDK-style ("legacy") projects, the format of Visual Studio before 2017 that most .NET Framework applications
/// still use: they import the targets of Visual Studio (WPF, WCF, COM references, packages.config, ...), which the
/// .NET SDK does not have, so they are built with MSBuild.exe of Visual Studio or its Build Tools.
/// </summary>
internal static class MSBuildLocator
{
    /// <summary>Whether the project is of the old format: no Sdk attribute, no Sdk element, no Sdk import.</summary>
    public static bool IsLegacyProject(string projectFile)
    {
        try
        {
            XElement? root = XDocument.Load(projectFile).Root;
            if (root == null || root.Attribute("Sdk") != null)
                return false;
            return !root.Elements().Any(e => e.Name.LocalName == "Sdk" || (e.Name.LocalName == "Import" && e.Attribute("Sdk") != null));
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            return false; // whoever builds it will tell
        }
    }

    private static readonly Lazy<string?> s_msbuild = new(Find);

    /// <summary>MSBuild.exe of the newest Visual Studio (or Build Tools) with MSBuild; MSBUILD_EXE_PATH wins. Null if there is none.</summary>
    public static string? MSBuildExe => s_msbuild.Value;

    private static string? Find()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        if (Environment.GetEnvironmentVariable("MSBUILD_EXE_PATH") is { Length: > 0 } configured
            && configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(configured))
        {
            return configured;
        }

        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
            return null;
        try
        {
            var startInfo = new ProcessStartInfo(vswhere)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in new[] { "-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe" })
                startInfo.ArgumentList.Add(argument);
            using Process process = Process.Start(startInfo)!;
            process.StandardInput.Close();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = stderr.Result;
            return output.Split('\n').Select(l => l.Trim()).FirstOrDefault(File.Exists);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
