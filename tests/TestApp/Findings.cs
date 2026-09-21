// Scenarios for the findings of the black-box DAP probe (docs: FINDINGS.md of the probe; tests: FindingsTests.cs).
using System.Globalization;

namespace TestApp;

public class SpinningToString
{
    public override string ToString()
    {
        long counter = 0;
        while (counter >= 0)
        {
            counter = (counter + 1) % 1000;
            Thread.Yield();
        }
        return "never";
    }
}

public class EmptyLoopProperty
{
    public int Before => 1;

    // no calls, no allocations: nothing the runtime could use as a safe point
    public int Hangs
    {
        get
        {
            while (true)
            {
            }
        }
    }

    public int After => 4;
}

public class Evil
{
    private static readonly object s_gate = new();

    public string Fine => "fine";

    // blocks inside the runtime: such an evaluation cannot be aborted
    public int Deadlocks
    {
        get
        {
            lock (s_gate)
                return 1;
        }
    }

    public static void HoldGateForever()
    {
        using var held = new ManualResetEventSlim();
        new Thread(() =>
        {
            lock (s_gate)
            {
                held.Set();
                Thread.Sleep(Timeout.Infinite);
            }
        })
        {
            IsBackground = true,
            Name = "gate-holder",
        }.Start();
        held.Wait();
    }
}

[Flags]
public enum Perm
{
    None = 0,
    Read = 1,
    Write = 2,
    Exec = 4,
}

public struct GridPoint(int x, int y)
{
    public int X = x;
    public int Y = y;
}

public class Greeter
{
    public string Greet(string name, int times = 1, string suffix = "!") =>
        string.Concat(Enumerable.Repeat("hi " + name, times)) + suffix;

    public static long Scale(int value, long factor = 10, Perm perm = Perm.Read) => value * factor + (int)perm;

    public static string TitleOf<T>(T item) where T : notnull
    {
        string title = item.ToString() + ":" + typeof(T).Name; // bp:genericMethod
        return title;
    }
}

public static class Findings
{
    public static void Run(string mode, string[] args)
    {
        switch (mode)
        {
            case "findings":
                Values();
                break;
            case "unicodeOutput":
                Console.WriteLine("Кириллица и emoji 🙂 €");
                Console.WriteLine("arg: " + (args.Length > 1 ? args[1] : ""));
                break;
            case "unhandledAsync":
                ThrowAfterAwait().GetAwaiter().GetResult();
                break;
            case "unhandledThread":
                var worker = new Thread(() => throw new InvalidOperationException("fatal on a worker")) { Name = "crashing-worker" };
                worker.Start();
                worker.Join();
                break;
            case "huge":
                Huge();
                break;
            case "evil":
                EvilEvaluation();
                break;
            case "implicitHang":
                ImplicitHang();
                break;
            case "implicitLoop":
                var looping = new EmptyLoopProperty();
                Console.WriteLine("implicit loop " + looping.Before); // bp:implicitLoop
                break;
            case "asyncSteps":
                AsyncSteps().GetAwaiter().GetResult();
                break;
            case "linqStep":
                LinqStep();
                break;
            case "parseFail":
                ParseFail();
                break;
            case "evaluator":
                Evaluator();
                break;
        }
    }

    private static unsafe void Evaluator()
    {
        var greeter = new Greeter();
        Perm perm = Perm.Read | Perm.Write;
        Func<int, int> twice = x => x * 2;
        int offset = 3;
        Func<int, int> shifted = x => x + offset;
        (int Id, string Name) tuple = (1, "t");
        int? maybe = 5;
        var point = new GridPoint(1, 2);
        int[] numbers = [10, 20, 30, 40, 50, 60, 70];
        int target = 42;
        int* pointer = &target;
        double real = 2.5;
        double* realPointer = &real;
        Console.WriteLine(Greeter.TitleOf(7) + Greeter.TitleOf("text"));
        Console.WriteLine($"evaluator {greeter.Greet("x")} {perm} {twice(2)} {shifted(1)} {tuple.Name} {maybe} {point.X} {numbers.Length} {*pointer} {*realPointer}"); // bp:evaluator
        Console.WriteLine($"evaluator after {maybe?.ToString(CultureInfo.InvariantCulture) ?? "null"} {point.X},{point.Y}"); // bp:evaluatorAfter
    }

    private static void Values()
    {
        string longText = new string('a', 5000);
        DayOfWeek day = DayOfWeek.Friday;
        var date = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);
        var anon = new { A = 1, B = "two" };
        Task done = Task.CompletedTask;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"findings {longText.Length} {day} {date.Kind} {anon.A} {done.Status}")); // bp:findings
    }

    private static async Task ThrowAfterAwait()
    {
        await Task.Delay(10);
        throw new InvalidOperationException("fatal after await");
    }

    private static void Huge()
    {
        List<int> million = Enumerable.Range(0, 1_000_000).ToList();
        var bytes = new byte[5_000_000];
        Dictionary<int, string> lookup = Enumerable.Range(0, 100_000).ToDictionary(i => i, i => i.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine($"huge {million.Count} {bytes.Length} {lookup.Count}"); // bp:huge
    }

    private static void EvilEvaluation()
    {
        Evil.HoldGateForever();
        var evil = new Evil();
        Console.WriteLine("evil " + evil.Fine); // bp:evil
    }

    private static void ImplicitHang()
    {
        var spinning = new SpinningToString();
        int plain = 5;
        Console.WriteLine("implicit " + plain); // bp:implicitHang
        GC.KeepAlive(spinning);
    }

    private static async Task<int> AsyncSteps()
    {
        int first = await Compute(1);
        int second = await Compute(first); // bp:asyncSecond
        Console.WriteLine("async steps " + second);
        return second;
    }

    private static async Task<int> Compute(int value)
    {
        await Task.Delay(10); // bp:computeFirst
        return value + 1;
    } // bp:computeEnd

    private static void LinqStep()
    {
        int[] data = [3, 1, 2];
        int max = data.Where(x => x > 1).Max(); // bp:linqCall
        Console.WriteLine("max " + max); // bp:linqAfter
    }

    private static void ParseFail()
    {
        try
        {
            int.Parse("not a number", CultureInfo.InvariantCulture); // bp:parseFail
        }
        catch (FormatException)
        {
            Console.WriteLine("parse failed");
        }
    }
}
