using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TestAppFx
{
    /// <summary>
    /// Types whose private fields are called differently in mscorlib than in System.Private.CoreLib: the debugger reads
    /// them straight from memory, so each of them needs to be seen on .NET Framework.
    /// </summary>
    static class Collections
    {
        public static void Run()
        {
            DateTime moment = new DateTime(2024, 5, 17, 13, 45, 30, DateTimeKind.Utc);
            DateTimeOffset stamp = new DateTimeOffset(2024, 5, 17, 13, 45, 30, TimeSpan.FromHours(3));
            TimeSpan span = TimeSpan.FromMinutes(90);
            decimal price = -79228162514264.337593543950335m;
            HashSet<int> set = new HashSet<int> { 1, 2, 3 };
            HashSet<string> holes = new HashSet<string> { "a", "b", "c", "d" };
            holes.Remove("b");
            Dictionary<string, int> removed = new Dictionary<string, int> { { "one", 1 }, { "two", 2 }, { "three", 3 } };
            removed.Remove("two");
            ConcurrentDictionary<string, int> concurrent = new ConcurrentDictionary<string, int>();
            concurrent["k"] = 5;
            concurrent["m"] = 7;

            Console.WriteLine("collections ready"); // bp:fx_collections
            GC.KeepAlive(new object[] { moment, stamp, span, price, set, holes, removed, concurrent });
        }

        // Outer awaits Middle awaits Inner. Middle is a plain Task method: the non-generic builder of mscorlib wraps the generic one.
        public static void AsyncNested()
        {
            Outer().GetAwaiter().GetResult();
        }

        static int s_result;

        static async Task Outer()
        {
            await Middle(); // bp:fx_outerAwait
            Console.WriteLine("outer got " + s_result); // bp:fx_outerAfter
        }

        static async Task Middle()
        {
            int result = 0;
            result = await Inner(); // bp:fx_middleAwait
            s_result = result; // bp:fx_middleAfter
        }

        static async Task<int> Inner()
        {
            await Task.Delay(30);
            int value = 41; // bp:fx_innerAfter
            return value + 1;
        }
    }
}
