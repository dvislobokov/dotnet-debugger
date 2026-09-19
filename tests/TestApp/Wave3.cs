namespace TestApp;

public static class TextExtensions
{
    public static int Doubled(this int value) => value * 2;

    public static string Shout(this string text) => text.ToUpperInvariant() + "!";

    public static string Initials(this Person person) => person.Name[..1];

    public static T Second<T>(this IEnumerable<T> items) => items.Skip(1).First();
}

public static class Wave3
{
    public static void Run()
    {
        int number = 42;
        string text = "hello";
        int[] numbers = [1, 2, 3];
        var list = new List<string> { "a", "b", "b" };
        var map = new Dictionary<string, int> { ["one"] = 1, ["two"] = 2 };
        var person = new Employee { Name = "Eve", Age = 40 };
        IEnumerable<int> lazy = numbers.Where(n => n > 1);
        var empty = new List<int>();

        Console.WriteLine("wave3 " + numbers.Second() + number.Doubled() + text.Shout() + person.Initials()); // bp:wave3
        GC.KeepAlive((list, map, lazy, empty));
    }
}
