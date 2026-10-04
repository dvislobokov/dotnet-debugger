// Debuggee of FrameworkLaunchTests, built from a non-SDK-style project. C# 7.3.
using System;

namespace LegacyFxApp
{
    class Program
    {
        static int Main(string[] args)
        {
            string kind = "legacy";
            Console.WriteLine(kind + " app on " + typeof(object).Assembly.GetName().Name); // bp:legacy_main
            return 7;
        }
    }
}
