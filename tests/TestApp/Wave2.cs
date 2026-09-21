using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace TestApp;

public interface IShape
{
    double Area { get; }

    string Describe();
}

public class Square(double side) : IShape
{
    private int _writes;

    public double Side { get; set; } = side;

    public double Area => Side * Side;

    // a setter with logic: assignments through the debugger must really call it
    public int Counter
    {
        get => _writes;
        set => _writes = value * 2;
    }

    string IShape.Describe() => "square " + Side.ToString(CultureInfo.InvariantCulture);
}

public struct Pair
{
    public int A;
    public int B;
}

[DebuggerTypeProxy(typeof(BagProxy))]
public class Bag
{
    internal List<string> Items = ["x", "y"];
    private readonly int _noise = 1;

    public int Noise => _noise;
}

internal sealed class BagProxy(Bag bag)
{
    public int Count => bag.Items.Count;

    public string First => bag.Items[0];

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public string[] All => bag.Items.ToArray();
}

public static class Wave2
{
    public static int CounterA;
    public static int CounterB;
    public static volatile bool StopWorkers;

    public static void Run()
    {
        var square = new Square(3);
        IShape shape = square;
        Color color = Color.Red;
        var pair = new Pair { A = 1, B = 2 };
        var bag = new Bag();
        int[] numbers = [1, 2, 3];
        int counter = 0;
        Span<int> span = numbers.AsSpan(1);
        ReadOnlySpan<char> chars = "hello".AsSpan(1, 3);
        Span<string> words = new[] { "a", "b", "c" }.AsSpan(1);
        Span<Pair> pairs = new[] { new Pair { A = 1, B = 2 }, new Pair { A = 3, B = 4 } };
        Span<double> none = default;
        Memory<int> memory = numbers.AsMemory(1);
        ReadOnlyMemory<char> text = "hello".AsMemory(1, 3);
        var concurrent = new ConcurrentDictionary<string, int> { ["k"] = 5 };
        IEnumerable<int> lazy = numbers.Where(n => n > 1);

        Console.WriteLine("wave2 " + (span.Length + chars.Length + words.Length + pairs.Length + none.Length)); // bp:wave2
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"state2: {square.Side} {square.Counter} {color} {pair.A} {counter} {numbers[0]}")); // bp:wave2State
        Console.WriteLine("spans: " + span[0] + words[0] + pairs[1].B + chars.Length);
        GC.KeepAlive((shape, bag, memory, text, concurrent, lazy));
    }

    // ---------------------------------------------------------------- async

    public static void AsyncParallel()
    {
        Task<int> slow = Worker(1, 400);
        Task<int> fast = Worker(2, 50);
        Task.WaitAll(slow, fast);
        Console.WriteLine("parallel done " + (slow.Result + fast.Result));
    }

    private static async Task<int> Worker(int id, int delay)
    {
        int before = id; // bp:asyncWorker
        await Task.Delay(delay);
        int after = before * 10;
        return after;
    }

    public static void AsyncNested() => Outer().GetAwaiter().GetResult();

    private static async Task Outer()
    {
        int result = await Inner(); // bp:outerAwait
        Console.WriteLine("outer got " + result); // bp:outerAfter
    }

    private static async Task<int> Inner()
    {
        await Task.Delay(30);
        int value = 41; // bp:innerAfter
        return value + 1;
    }

    // ---------------------------------------------------------------- threads

    public static void Workers()
    {
        var a = new Thread(() => Count(ref CounterA)) { Name = "counter-A" };
        var b = new Thread(() => Count(ref CounterB)) { Name = "counter-B" };
        a.Start();
        b.Start();
        Console.WriteLine("workers started");
        a.Join();
        b.Join();
    }

    private static void Count(ref int counter)
    {
        while (!StopWorkers)
        {
            Interlocked.Increment(ref counter); // bp:workerCount
            Thread.Sleep(5);
        }
    }

    // ---------------------------------------------------------------- stress

    public static void Stress()
    {
        int[] big = new int[1_000_000];
        for (int i = 0; i < big.Length; i++)
            big[i] = i;

        int hits = 0;
        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            for (int i = 0; i < 50; i++)
                Interlocked.Increment(ref hits); // bp:stressHit
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        int depth = Recurse(2000);
        Console.WriteLine($"stress done {hits} {depth} {big.Length}"); // bp:stressDone
    }

    private static int Recurse(int remaining)
    {
        if (remaining == 0)
            return Bottom();
        return Recurse(remaining - 1) + 1;
    }

    private static int Bottom()
    {
        return 0; // bp:stressBottom
    }
}
