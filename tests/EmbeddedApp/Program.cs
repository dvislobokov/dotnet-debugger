namespace EmbeddedApp;

public static class Program
{
    public static void Main()
    {
        int value = Compute(20);
        Console.WriteLine("embedded says " + value);
    }

    // embedded marker: this comment only exists in the source embedded into the PDB
    public static int Compute(int input)
    {
        int doubled = input * 2; // bp:embeddedCompute
        return doubled + 2;
    }
}
