// Debuggee of FrameworkLaunchTests, built for .NET and .NET Framework. C# 7.3.
using System;

namespace MultiFxApp
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("runtime: " + typeof(object).Assembly.GetName().Name); // bp:multi_main
        }
    }
}
