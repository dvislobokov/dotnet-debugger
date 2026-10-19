// Scenarios for "step in targets" (smart step into): statements with several calls to choose from.
namespace TestApp;

public sealed class Pricing
{
    public int Count => 3; // bp:stepIntoCountGetter

    public decimal Total(List<int> lines, decimal discount)
    {
        decimal sum = lines.Sum(); // bp:stepIntoTotal
        return sum * (1 - discount);
    }
}

public static class StepInto
{
    public static void Run()
    {
        var pricing = new Pricing();
        var lines = new List<int> { 1, 2, 3 };
        decimal discount = 0.1m;
        int factor = 2;

        decimal total = pricing.Total(lines, discount) + Tax(pricing.Total(lines, 0m)); // bp:stepIntoMulti
        int nested = Outer(Inner(3)); // bp:stepIntoNested
        int count = pricing.Count + lines.Count; // bp:stepIntoProperty
        int lambdas = lines.Where(x => x > 1).Select(x => x * factor).Count(); // bp:stepIntoLambda
        int asyncResult = ComputeAsync(2).GetAwaiter().GetResult(); // bp:stepIntoAsync
        var created = new Pricing().Total(lines, Tax(1m)); // bp:stepIntoNew
        Console.WriteLine($"{total} {nested} {count} {lambdas} {asyncResult} {created}"); // bp:stepIntoEnd
    }

    private static decimal Tax(decimal value)
    {
        decimal tax = value / 10; // bp:stepIntoTax
        return tax;
    }

    private static int Outer(int value)
    {
        int result = value + 1; // bp:stepIntoOuter
        return result;
    }

    private static int Inner(int value)
    {
        int result = value * 2; // bp:stepIntoInner
        return result;
    }

    private static async Task<int> ComputeAsync(int value)
    {
        int doubled = value * 2; // bp:stepIntoAsyncBody
        await Task.Delay(1);
        return doubled + 1;
    }
}
