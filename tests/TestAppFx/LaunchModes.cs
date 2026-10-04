// Modes of the launch tests (FrameworkLaunchTests): a terminal, its stdin and console, and the environment.
using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace TestAppFx
{
    static class LaunchModes
    {
        public static void Run(string mode)
        {
            switch (mode)
            {
                case "stdin":
                    Echo();
                    break;
                case "terminal":
                    Terminal();
                    break;
                case "zap":
                    Console.WriteLine("ZapDisable: " + (Environment.GetEnvironmentVariable("COMPlus_ZapDisable") ?? "<unset>"));
                    break;
            }
        }

        static void Echo()
        {
            Console.WriteLine("ready");
            string line = Console.ReadLine();
            Console.WriteLine("echo: " + line); // bp:fx_stdinEcho
            Console.WriteLine("env: " + Environment.GetEnvironmentVariable("DBG_TEST"));
        }

        // The processes attached to the console this one runs in.
        static void Terminal()
        {
            uint[] processes = new uint[64];
            uint count = GetConsoleProcessList(processes, (uint)processes.Length);
            Console.WriteLine("console processes: " + string.Join(",", processes.Take((int)Math.Min(count, (uint)processes.Length))));
        }

        [DllImport("kernel32")]
        static extern uint GetConsoleProcessList(uint[] processes, uint count);
    }
}
