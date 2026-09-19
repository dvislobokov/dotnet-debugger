using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;

namespace TestApp;

[DebuggerDisplay("Order {Id}: {Customer,nq} x{Lines.Count}")]
public class Order
{
    public int Id;
    public string Customer = "";
    public List<string> Lines = [];

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Secret = "hidden";

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string SecretProperty { get; set; } = "hidden too";

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public int[] Codes = [7, 8];
}

[DebuggerDisplay("{Name}")]
public class Quoted
{
    public string Name = "Ann";
}

[DebuggerDisplay("{Label()} / {Missing}")]
public class BrokenDisplay
{
    public int Id = 5;

    public string Label() => "L" + Id;
}

public class SpecialOrder : Order;

public class Temperature(double degrees)
{
    public double Degrees { get; } = degrees;

    public override string ToString() => Degrees.ToString(CultureInfo.InvariantCulture) + " C";
}

public class Plain
{
    public int Value = 1;
}

public record Rec(int A, string B);

public class Throwing
{
    public override string ToString() => throw new InvalidOperationException("no text for you");
}

public class Slow
{
    public int Fast => 1;

    public int SlowOne
    {
        get
        {
            Thread.Sleep(1500);
            return 2;
        }
    }

    public int SlowTwo
    {
        get
        {
            Thread.Sleep(1500);
            return 3;
        }
    }

    public int After => 4;

    public static int Hang()
    {
        Thread.Sleep(60000);
        return 1;
    }

    public static int Spin()
    {
        long counter = 0;
        while (counter >= 0)
            counter = (counter + 1) % 1000;
        return 2;
    }
}

public static class Display
{
    public static void Run()
    {
        var order = new Order { Id = 7, Customer = "Ann", Lines = ["a", "b"] };
        Order special = new SpecialOrder { Id = 8, Customer = "Bob" };
        var quoted = new Quoted();
        var broken = new BrokenDisplay();
        var temperature = new Temperature(24.5);
        var plain = new Plain();
        var rec = new Rec(1, "x");
        var throwing = new Throwing();
        var slow = new Slow();
        var failure = new ArgumentException("bad argument");

        var utc = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var withMillis = new DateTime(2024, 5, 6, 7, 8, 9, 123);
        var dateOnlyTime = new DateTime(2024, 5, 6);
        var span = TimeSpan.FromMinutes(90.5);
        var guid = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        var offset = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromHours(2));
        var pair = new KeyValuePair<string, int>("one", 1);
        var tuple = (1, "x");
        var refTuple = Tuple.Create(2, "y");

        var set = new HashSet<int> { 1, 2, 3 };
        set.Remove(2);
        var queue = new Queue<int>([1, 2, 3]);
        queue.Dequeue();
        var stack = new Stack<string>();
        stack.Push("bottom");
        stack.Push("top");
        var sorted = new SortedList<string, int> { ["b"] = 2, ["a"] = 1 };
        var immutable = ImmutableArray.Create(4, 5);
        var readOnly = new ReadOnlyCollection<int>(new List<int> { 6, 7 });
        var segment = new ArraySegment<int>([1, 2, 3, 4], 1, 2);
        var emptySet = new HashSet<string>();

        Console.WriteLine("display"); // bp:display
        GC.KeepAlive((order, special, quoted, broken, temperature, plain, rec, throwing, slow, failure));
        GC.KeepAlive((utc, withMillis, dateOnlyTime, span, guid, offset, pair, tuple, refTuple));
        GC.KeepAlive((set, queue, stack, sorted, immutable, readOnly, segment, emptySet));
    }
}
