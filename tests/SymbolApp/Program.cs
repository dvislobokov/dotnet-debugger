namespace SymbolApp;

public static class Program
{
    public static void Main()
    {
        // SymbolLib is only loaded when Run is compiled, i.e. after this line: an assembly that arrives later
        Console.WriteLine("start"); // bp:symbolAppStart
        Run();
    }

    private static void Run()
    {
        int tripled = SymbolLib.Calculator.Triple(14); // bp:symbolAppCall
        Console.WriteLine("tripled " + tripled);
        Console.WriteLine(SymbolLib.Calculator.Triple(1.5) + " " + SymbolLib.Calculator.Describe(7) + " " + SymbolLib.Calculator.Inner.Run(1));
    }
}
