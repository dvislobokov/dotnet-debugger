namespace DotnetDebugger.Engine.Symbols;

/// <summary>
/// A type as written by <see cref="ModuleMetadata"/>'s signature decoder: "System.Int32", "Ns.List`1&lt;System.String&gt;",
/// "!0" (type variable of the class), "!!0" (of the method), "T[]". Parsed into a tree so that a parameter type can be
/// matched against the closed type of an argument, binding the method's type variables on the way.
/// </summary>
internal sealed record TypeSignature(string Name, IReadOnlyList<TypeSignature> Arguments, int ArrayRank = 0)
{
    public bool IsMethodVariable => ArrayRank == 0 && Name.StartsWith("!!", StringComparison.Ordinal);
    public bool IsTypeVariable => ArrayRank == 0 && !IsMethodVariable && Name.StartsWith('!');

    public static TypeSignature Parse(string text)
    {
        int position = 0;
        TypeSignature result = ParseAt(text, ref position);
        return position == text.Length ? result : throw new FormatException($"Unexpected '{text[position..]}' in type '{text}'.");
    }

    private static TypeSignature ParseAt(string text, ref int position)
    {
        int start = position;
        while (position < text.Length && text[position] is not ('<' or '>' or ',' or '['))
            position++;
        string name = text[start..position].Trim();
        if (name.StartsWith("ref ", StringComparison.Ordinal))
            name = name[4..];

        var arguments = new List<TypeSignature>();
        if (position < text.Length && text[position] == '<')
        {
            do
            {
                position++; // '<' or ','
                arguments.Add(ParseAt(text, ref position));
            }
            while (position < text.Length && text[position] == ',');
            position++; // '>'
        }

        var result = new TypeSignature(name, arguments);
        while (position + 1 < text.Length && text[position] == '[')
        {
            int close = text.IndexOf(']', position);
            result = new TypeSignature("", [result], ArrayRank: close - position);
            position = close + 1;
        }
        return result;
    }

    /// <summary>Replaces the type variables of a class ("!0") with the type arguments of a closed instantiation.</summary>
    public TypeSignature Substitute(IReadOnlyList<TypeSignature> classArguments)
    {
        if (IsTypeVariable && int.TryParse(Name.AsSpan(1), out int index) && index < classArguments.Count)
            return classArguments[index];
        return Arguments.Count == 0 ? this : this with { Arguments = Arguments.Select(a => a.Substitute(classArguments)).ToList() };
    }

    /// <summary>
    /// Matches this (parameter) type against a closed type, recording what the method's type variables have to be.
    /// </summary>
    public bool Unify(TypeSignature closed, Dictionary<int, TypeSignature> bindings)
    {
        // a type variable of the declaring class: whatever the instance was created with, which is not known here
        if (IsTypeVariable)
            return true;
        if (IsMethodVariable && int.TryParse(Name.AsSpan(2), out int index))
        {
            if (bindings.TryGetValue(index, out TypeSignature? bound))
                return bound.ToString() == closed.ToString();
            bindings[index] = closed;
            return true;
        }
        if (Name != closed.Name || ArrayRank != closed.ArrayRank || Arguments.Count != closed.Arguments.Count)
            return false;
        for (int i = 0; i < Arguments.Count; i++)
        {
            if (!Arguments[i].Unify(closed.Arguments[i], bindings))
                return false;
        }
        return true;
    }

    public bool ContainsMethodVariable => IsMethodVariable || Arguments.Any(a => a.ContainsMethodVariable);

    public override string ToString() => ArrayRank > 0
        ? Arguments[0] + "[" + new string(',', ArrayRank - 1) + "]"
        : Arguments.Count == 0 ? Name : Name + "<" + string.Join(",", Arguments) + ">";
}
