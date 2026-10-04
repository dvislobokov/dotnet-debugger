using System.Diagnostics;
using DotnetDebugger.Protocol;

namespace DotnetDebugger.Tests;

/// <summary>
/// 32-bit processes (Windows only). The debugging library has to be of the bitness of the debuggee and there is no
/// 32-bit build of the adapter: such targets are refused in plain words before anything is started or attached to.
/// </summary>
public class ThirtyTwoBitTests
{
    private static string Fx32Exe => DebuggeePath("TestAppFx32", TestPaths.FxTargetFramework, "TestAppFx32.exe");
    private static string FxAnyCpu32Exe => DebuggeePath("TestAppFxAnyCpu32", TestPaths.FxTargetFramework, "TestAppFxAnyCpu32.exe");
    private static string App32Exe => DebuggeePath("TestApp32", Path.Combine(TestPaths.TargetFramework, "win-x86"), "TestApp32.exe");

    private static string DebuggeePath(string project, string outputDirectory, string file) =>
        Path.Combine(TestPaths.Root, "tests", project, "bin", TestPaths.Configuration, outputDirectory, file);

    private static void AssertRefused(DapMessage response, string target)
    {
        Assert.False(response.Success);
        Assert.Contains($"{target} runs as a 32-bit process, which dotnet-debugger cannot debug.", response.Message);
        Assert.Contains("x64", response.Message);
        Assert.DoesNotContain("HRESULT", response.Message);
    }

    [WindowsTheory]
    [InlineData("TestAppFx32.exe")]      // .NET Framework, x86
    [InlineData("TestAppFxAnyCpu32.exe")] // .NET Framework, AnyCPU preferring 32-bit (the default of old project templates)
    [InlineData("TestApp32.exe")]        // .NET, win-x86 apphost
    public void LaunchOfA32BitProgramIsRefused(string file)
    {
        string program = file switch
        {
            "TestAppFx32.exe" => Fx32Exe,
            "TestAppFxAnyCpu32.exe" => FxAnyCpu32Exe,
            _ => App32Exe,
        };
        using var client = new DapClient();
        client.Initialize();
        AssertRefused(client.RequestRaw("launch", new { program, args = new[] { "none" } }), $"'{file}'");

        Assert.False(client.HasPendingEvent("process"), "Nothing may have been started.");
    }

    [WindowsFact]
    public void AttachToA32BitProcessIsRefusedAndLeavesItRunning()
    {
        var startInfo = new ProcessStartInfo(Fx32Exe) { UseShellExecute = false, RedirectStandardOutput = true };
        startInfo.ArgumentList.Add("wait");
        using Process debuggee = Process.Start(startInfo)!;
        try
        {
            Assert.Equal("mode: wait", debuggee.StandardOutput.ReadLine());
            using (var client = new DapClient())
            {
                client.Initialize();
                AssertRefused(client.RequestRaw("attach", new { processId = debuggee.Id }), $"Process {debuggee.Id}");
            }
            Assert.False(debuggee.WaitForExit(500), "A refused attach must leave the process running.");
        }
        finally
        {
            debuggee.Kill();
        }
    }
}
