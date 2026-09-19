using System.Linq.Expressions;
using System.Runtime.InteropServices;

namespace TestApp;

public static class Advanced
{
    public static void Run(int seed)
    {
        int factor = 3;
        Func<int, int> inline = x => x * factor; // bp:lambdaInline
        Func<int, int> block = x =>
        {
            int inner = x + factor; // bp:lambdaBody
            return inner;
        };
        int viaBlock = block(4); // bp:callLambda
        int viaInline = inline(5);

        List<int> evens = new[] { 1, 2, 3, 4 }
            .Where(n => n % 2 == 0) // bp:linqWhere
            .Select(n => n * factor)
            .ToList();

        int LocalFunction(int value)
        {
            int local = value + factor + seed; // bp:localFunction
            return local;
        }
        int viaLocal = LocalFunction(1);

        foreach (int item in Iterate(2))
            Console.WriteLine(item);

        string fromGeneric = new Box<string>("payload").Describe(7);
        int expressionBodied = Square(6);

        uint native = OperatingSystem.IsWindows() ? GetCurrentProcessId() : (uint)getpid(); // bp:pinvoke
        Expression<Func<int, int>> tree = x => x + 1; // bp:afterPinvoke
        Func<int, int> compiled = tree.Compile();
        int viaTree = compiled(5); // bp:expressionTree
        Console.WriteLine(viaTree); // bp:afterExpressionTree
        GC.KeepAlive((viaBlock, viaInline, evens, viaLocal, fromGeneric, expressionBodied, native));
    }

    private static int Square(int value) => value * value; // bp:expressionBodied

    private static IEnumerable<int> Iterate(int count)
    {
        for (int i = 0; i < count; i++)
        {
            int squared = i * i; // bp:iterator
            yield return squared;
        }
    }

    [DllImport("kernel32")]
    private static extern uint GetCurrentProcessId();

    [DllImport("libc")]
    private static extern int getpid();
}
