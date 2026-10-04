using System;
using System.Collections.Generic;

namespace TestAppFx
{
    /// <summary>
    /// The "domains" mode: the same code runs in the default AppDomain and in a second one (the ASP.NET-on-IIS and
    /// test-runner setup), then the second domain is unloaded and the code runs once more in the default domain.
    /// Statics are per domain, so <see cref="DomainWorker.Counter"/> tells the domains apart. "domainsShared" loads
    /// the assemblies of the second domain domain-neutral (LoaderOptimization.MultiDomain): shared code, per-domain statics.
    /// </summary>
    static class Domains
    {
        public static void Run(bool shared)
        {
            DomainWorker.Counter = 1;
            DomainWorker local = new DomainWorker();
            int first = local.Work(1);

            AppDomainSetup setup = new AppDomainSetup { ApplicationBase = AppDomain.CurrentDomain.BaseDirectory };
            if (shared)
                setup.LoaderOptimization = LoaderOptimization.MultiDomain;
            AppDomain domain = AppDomain.CreateDomain("SecondDomain", null, setup);
            DomainWorker remote = (DomainWorker)domain.CreateInstanceAndUnwrap(typeof(DomainWorker).Assembly.FullName, typeof(DomainWorker).FullName);
            int second = remote.Work(2); // bp:fx_domainCall
            AppDomain.Unload(domain);

            List<string> results = new List<string> { first.ToString(), second.ToString() };
            Console.WriteLine("unloaded: " + string.Join(",", results)); // bp:fx_domainUnloaded

            int third = local.Work(3); // bp:fx_domainAgain
            Console.WriteLine("third: " + third);
        }
    }

    /// <summary>Created in the second domain and called through a transparent proxy.</summary>
    public class DomainWorker : MarshalByRefObject
    {
        public static int Counter;

        public int Work(int input)
        {
            Counter += 100;
            int doubled = input * 2;
            string domainName = AppDomain.CurrentDomain.FriendlyName;
            Console.WriteLine("work in " + domainName + ": " + doubled); // bp:fx_domainWork
            return doubled + Counter;
        }
    }

    /// <summary>Remembers the counter of the domain it was created in.</summary>
    public class DomainProbe
    {
        public int Seen;

        public DomainProbe()
        {
            Seen = DomainWorker.Counter;
        }
    }
}
