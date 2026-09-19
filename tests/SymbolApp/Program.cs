namespace SymbolApp;

public static class Program
{
    public static void Main()
    {
        int tripled = SymbolLib.Calculator.Triple(14); // bp:symbolAppCall
        Console.WriteLine("tripled " + tripled);
    }
}
