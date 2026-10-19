// Scenario for lambdas in evaluated expressions (LINQ with predicates and selectors in Watch / Evaluate).
namespace TestApp;

public static class Lambdas
{
    public static void Run()
    {
        var numbers = new List<int> { 1, 2, 3, 4, 5 };
        int[] empty = [];
        string[] words = ["apple", "fig", "banana"];
        int threshold = 2;
        int factor = 10;
        var people = new List<Person>
        {
            new() { Name = "Ann", Age = 30 },
            new() { Name = "Bob", Age = 41 },
            new() { Name = "Cid", Age = 25 },
        };
        var map = new Dictionary<string, int> { ["one"] = 1, ["two"] = 2, ["three"] = 3 };
        Func<int, bool> isBig = x => x > 3;
        Console.WriteLine(numbers.Count + empty.Length + words.Length + threshold + factor + people.Count + map.Count); // bp:lambdas
        GC.KeepAlive(isBig);
    }
}
