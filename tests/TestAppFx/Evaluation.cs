using System;
using System.Collections.Generic;
using System.Linq;

namespace TestAppFx
{
    /// <summary>The "evaluate" mode: a frame full of things to evaluate (method calls, getters, arithmetic, strings, LINQ).</summary>
    static class Evaluation
    {
        public static void Run()
        {
            Calculator calc = new Calculator("main", 10);
            int x = 6;
            int y = 7;
            double half = 0.5;
            string greeting = "Привет";
            string name = "world";
            List<int> values = new List<int> { 1, 2, 3, 4 };
            Person person = new Person { Name = "Ann", Age = 30 };

            int product = calc.Multiply(x, y); // bp:fx_evaluate
            Console.WriteLine(Describe(product, greeting, name)); // bp:fx_afterEvaluate
            GC.KeepAlive(new object[] { half, values.Sum(), person });
        }

        static string Describe(int product, string greeting, string name)
        {
            return greeting + ", " + name + ": " + product;
        }
    }
}
