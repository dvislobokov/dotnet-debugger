namespace SymbolLib;

public static class Calculator
{
    public static int Triple(int value)
    {
        int result = value * 3; // bp:symbolLibTriple
        return result;
    }

    // an overload: a function breakpoint by name stops in both
    public static double Triple(double value)
    {
        double result = value * 3; // bp:symbolLibTripleDouble
        return result;
    }

    public static string Describe<T>(T value)
    {
        string text = "value " + value; // bp:symbolLibDescribe
        return text;
    }

    public static class Inner
    {
        public static int Run(int value)
        {
            int result = value + 1; // bp:symbolLibInnerRun
            return result;
        }
    }
}
