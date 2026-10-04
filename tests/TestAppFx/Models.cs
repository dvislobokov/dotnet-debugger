using System;

namespace TestAppFx
{
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

        public int Sum
        {
            get { return X + Y; }
        }
    }

    public class Person
    {
        public static int Instances;
        public const string Species = "human";

        public Person()
        {
            Instances++;
            Name = "";
        }

        public string Name { get; set; }
        public int Age { get; set; }
        public Person Friend;

        public string Summary => string.Format("{0} ({1})", Name, Age);

        public string Greet(string greeting)
        {
            return greeting + ", " + Name;
        }

        public override string ToString()
        {
            return "Person:" + Name;
        }
    }

    public class Employee : Person
    {
        public Employee()
        {
            Company = "";
        }

        public string Company { get; set; }
    }

    /// <summary>Target of the "evaluate" mode: instance and static members, overloads, a property with a getter body.</summary>
    public class Calculator
    {
        private readonly string _name;
        private int _calls;

        public Calculator(string name, int offset)
        {
            _name = name;
            Offset = offset;
        }

        public static string Version
        {
            get { return "1.0-fx"; }
        }

        public int Offset { get; private set; }

        public string Name
        {
            get { return _name; }
        }

        public int Calls
        {
            get { return _calls; }
        }

        public int Multiply(int a, int b)
        {
            _calls++;
            return a * b;
        }

        public int Add(int a, int b)
        {
            return a + b + Offset;
        }

        public string Describe(string prefix)
        {
            return prefix + _name;
        }

        public static int Square(int value)
        {
            return value * value;
        }
    }
}
