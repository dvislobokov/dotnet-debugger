using System.Diagnostics;

namespace TestApp;

public class Holder
{
    public int Computed
    {
        get
        {
            int v = 20; // bp:getter
            return v + 1;
        }
    }
}

public readonly record struct Vec(int X)
{
    public static Vec operator +(Vec left, Vec right)
    {
        return new Vec(left.X + right.X); // bp:operator
    }
}

public static class StepFilters
{
    public static void Run()
    {
        int a = Hidden(1); // bp:stepHidden
        int b = Through(2); // bp:stepThrough
        var holder = new Holder();
        var v1 = new Vec(1);
        var v2 = new Vec(2);
        int c = holder.Computed + 1; // bp:stepProperty
        Vec sum = v1 + v2; // bp:stepOperator
        string text = MakeText(3); // bp:stepEnd
        Console.WriteLine(a + b + c + sum.X + text);
    }

    [DebuggerHidden]
    private static int Hidden(int x)
    {
        return x + 1;
    }

    [DebuggerStepThrough]
    private static int Through(int x)
    {
        return Callee(x);
    }

    private static int Callee(int x)
    {
        return x * 2; // bp:callee
    }

    private static string MakeText(int count)
    {
        return new string('x', count); // bp:makeText
    }
}
