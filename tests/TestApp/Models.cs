namespace TestApp;

[Flags]
public enum Access
{
    None = 0,
    Read = 1,
    Write = 2,
}

public enum Color
{
    Red,
    Green,
    Blue,
}

public struct Point
{
    public int X;
    public int Y;

    public readonly int Sum => X + Y;
}

public class Person
{
    public static int Instances;
    public const string Species = "human";

    public Person() => Instances++;

    public string Name { get; set; } = "";
    public int Age { get; set; }
    public Person? Friend;

    public string Summary => $"{Name} ({Age})";
    public string Broken => throw new NotSupportedException("no value for you");

    public string Greet(string greeting) => greeting + ", " + Name;

    public override string ToString() => "Person:" + Name;
}

public class Employee : Person
{
    public string Company { get; set; } = "";
}

public class Box<T>(T value)
{
    private readonly T _value = value;

    public string Describe<TExtra>(TExtra extra)
    {
        string description = _value + ":" + extra; // bp:generic
        return description;
    }
}
