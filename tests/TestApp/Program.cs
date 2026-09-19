// Debuggee used by the integration tests. Tests locate lines by their "// bp:<name>" markers,
// so code can be moved around freely as long as the markers stay on the right statements.
using System.Diagnostics;
using System.Globalization;

namespace TestApp;

public static class Program
{
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "basic";
        Console.WriteLine("mode: " + mode); // bp:entry
        switch (mode)
        {
            case "basic":
                Basic();
                break;
            case "exception":
                Exceptions();
                break;
            case "async":
                AsyncWork(5).GetAwaiter().GetResult();
                break;
            case "wait":
                Wait();
                break;
            case "advanced":
                Advanced.Run(2);
                break;
            case "loop":
                Loop();
                break;
            case "threads":
                Threads();
                break;
            case "break":
                Break();
                break;
            case "env":
                Console.WriteLine($"arg: {args[1]}; env: {Environment.GetEnvironmentVariable("DBG_TEST")}; cwd: {Environment.CurrentDirectory}");
                Console.WriteLine($"second: {Environment.GetEnvironmentVariable("DBG_SECOND")}; urls: {Environment.GetEnvironmentVariable("ASPNETCORE_URLS")}");
                break;
            case "stdin":
                Echo();
                break;
            case "display":
                Display.Run();
                break;
            case "stepFilters":
                StepFilters.Run();
                break;
            case "userUnhandled":
                UserUnhandled();
                break;
            case "wave2":
                Wave2.Run();
                break;
            case "wave3":
                Wave3.Run();
                break;
            case "asyncParallel":
                Wave2.AsyncParallel();
                break;
            case "asyncNested":
                Wave2.AsyncNested();
                break;
            case "workers":
                Wave2.Workers();
                break;
            case "stress":
                Wave2.Stress();
                break;
        }
        Console.Error.WriteLine("done");
        return 3;
    }

    private static void Basic()
    {
        int number = 42;
        string text = "hello \"world\"";
        double ratio = 1.5;
        bool flag = true;
        char letter = 'x';
        decimal money = 12.34m;
        int? maybe = 7;
        int? nothing = null;
        Color color = Color.Green;
        Access access = Access.Read | Access.Write;
        var point = new Point { X = 1, Y = 2 };
        var person = new Person { Name = "Ann", Age = 30, Friend = new Person { Name = "Bob", Age = 31 } };
        Person employee = new Employee { Name = "Eve", Age = 40, Company = "Initech" };
        int[] numbers = [10, 20, 30];
        int[,] grid = { { 1, 2 }, { 3, 4 } };
        var list = new List<string> { "a", "b" };
        var map = new Dictionary<string, int> { ["one"] = 1 };
        object boxed = 99;
        Person? nobody = null;

        int sum = Add(number, numbers[0]); // bp:locals
        Console.WriteLine(sum.ToString(CultureInfo.InvariantCulture)); // bp:afterAdd

        int captured = 5;
        Func<int, int> multiply = x => x * captured; // bp:lambdaDecl
        Console.WriteLine(multiply(3));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"state: {number} {text} {ratio} {flag} {person.Age} {point.X} {numbers[1]}")); // bp:state
        GC.KeepAlive((letter, money, maybe, nothing, color, access, employee, grid, list, map, boxed, nobody));
    }

    private static int Add(int a, int b)
    {
        int result = a + b; // bp:add
        return result;
    }

    private static void Exceptions()
    {
        try
        {
            throw new InvalidOperationException("caught one"); // bp:throwCaught
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("handled");
        }
        throw new ArgumentException("fatal one", new FormatException("inner cause")); // bp:throwFatal
    }

    private static async Task<int> AsyncWork(int input)
    {
        int doubled = input * 2; // bp:asyncStart
        await Task.Delay(10);
        int final = doubled + 1; // bp:async
        return final;
    }

    private static void Wait()
    {
        int counter = 0;
        while (counter >= 0)
        {
            Thread.Sleep(20);
            counter++; // bp:loop
        }
    }

    private static void Loop()
    {
        int total = 0;
        for (int i = 0; i < 10; i++)
        {
            total += i; // bp:loopBody
        }
        Console.WriteLine("total: " + total); // bp:loopEnd
    }

    private static void Threads()
    {
        var worker = new Thread(() =>
        {
            int workerValue = 11;
            Console.WriteLine("worker running " + workerValue); // bp:worker
        })
        {
            Name = "worker-1",
        };
        worker.Start();
        worker.Join();
    }

    private static void UserUnhandled()
    {
        try
        {
            throw new InvalidOperationException("handled by user");
        }
        catch (InvalidOperationException)
        {
        }

        // thrown in user code, caught by the framework (the task infrastructure): "user-unhandled"
        Task task = Task.Run(() =>
        {
            throw new FormatException("escapes to framework"); // bp:userUnhandledThrow
        });
        try
        {
            task.Wait();
        }
        catch (AggregateException)
        {
            Console.WriteLine("observed");
        }
    }

    private static void Echo()
    {
        Console.WriteLine("ready");
        string? line = Console.ReadLine();
        Console.WriteLine("echo: " + line); // bp:stdinEcho
    }

    private static void Break()
    {
        Debug.WriteLine("dbg message");
        Debugger.Break();
        Console.WriteLine("after break"); // bp:afterBreak
    }
}
