using DotnetDebugger.Protocol;

namespace DotnetDebugger.Tests;

/// <summary>
/// Lambdas in evaluated expressions (finding 18 of the DAP probe): LINQ methods with predicates and selectors,
/// capturing locals and parameters of the frame. Scenario: tests/TestApp/Lambdas.cs.
/// </summary>
public class LambdaEvaluationTests
{
    [Theory]
    // Where, Count, Any, All, First and friends
    [InlineData("numbers.Where(x => x > 2).Count()", "3")]
    [InlineData("numbers.Where(x => x > threshold).Count()", "3")] // a captured local
    [InlineData("numbers.Where(x => x % 2 == 0)", "{int[2]}")]
    [InlineData("numbers.Where(x => x % 2 == 0)[1]", "4")]
    [InlineData("numbers.Count(x => x > threshold * 2)", "1")]
    [InlineData("numbers.Any(x => x == 4)", "true")]
    [InlineData("numbers.Any(x => x > factor)", "false")]
    [InlineData("numbers.All(x => x > 0)", "true")]
    [InlineData("numbers.First(x => x > threshold)", "3")]
    [InlineData("numbers.Last(x => x < threshold)", "1")]
    [InlineData("numbers.FirstOrDefault(x => x > factor)", "0")]
    [InlineData("numbers.Single(x => x == 5)", "5")]
    [InlineData("empty.Any(x => x > 0)", "false")]
    [InlineData("empty.Where(x => x > 0).Count()", "0")]
    // Select, with the rest of the chain evaluated in the debuggee
    [InlineData("numbers.Select(x => x * factor).Sum()", "150")]
    [InlineData("numbers.Select(x => x * factor).ToList().Count", "5")]
    [InlineData("numbers.Select((x, i) => x * i).Sum()", "40")]
    [InlineData("numbers.Where(x => x > 2).Select(x => x * 2).Max()", "10")]
    [InlineData("words.Select(w => w.Length).Max()", "6")]
    [InlineData("words.Select(w => w.ToUpper()).First()", "\"APPLE\"")]
    [InlineData("numbers.Sum(x => x * 2)", "30")]
    [InlineData("numbers.Max(x => -x)", "-1")]
    [InlineData("numbers.Min(x => x + factor)", "11")]
    [InlineData("numbers.Average(x => x)", "3")]
    // objects of the debuggee: members, method calls, properties
    [InlineData("people.First(p => p.Age > 30).Name", "\"Bob\"")]
    [InlineData("people.Where(p => p.Name.StartsWith(\"A\")).Count()", "1")]
    [InlineData("people.Count(p => p.Age < 40)", "2")]
    [InlineData("people.FirstOrDefault(p => p.Age > 100)", "null")]
    [InlineData("people.Where(p => p.Age > 26).Select(p => p.Name).Count()", "2")]
    [InlineData("people.Select(p => p.Age).Sum()", "96")]
    [InlineData("people.Any(p => p.Name == \"Cid\")", "true")]
    // OrderBy
    [InlineData("people.OrderBy(p => p.Age).First().Name", "\"Cid\"")]
    [InlineData("people.OrderByDescending(p => p.Age).First().Name", "\"Bob\"")]
    [InlineData("people.OrderBy(p => p.Name).Last().Name", "\"Cid\"")]
    [InlineData("numbers.OrderByDescending(x => x).First()", "5")]
    [InlineData("words.OrderBy(w => w.Length).First()", "\"fig\"")]
    // other sequences: dictionaries, strings, delegates in locals, parenthesized and anonymous-method forms
    [InlineData("map.Where(kv => kv.Value > 1).Count()", "2")]
    [InlineData("map.First(kv => kv.Key == \"two\").Value", "2")]
    [InlineData("map.Where(kv => kv.Value > 1)", "{System.Collections.Generic.KeyValuePair<string, int>[2]}")] // an array of structs
    [InlineData("map.Select(kv => kv)", "{System.Collections.Generic.KeyValuePair<string, int>[3]}")] // one element type, not object[]
    [InlineData("map.Select(kv => kv)[1].Key", "\"two\"")]
    [InlineData("\"hello\".Count(c => c == 'l')", "2")]
    [InlineData("numbers.Where(x => isBig(x)).Count()", "2")]
    [InlineData("numbers.Where((x) => x > 4).Count()", "1")]
    [InlineData("numbers.Where(delegate (int x) { return x > 4; }).Count()", "1")]
    [InlineData("numbers.Where(x => { return x > 4; }).Count()", "1")]
    [InlineData("numbers.TakeWhile(x => x < 3).Count()", "2")]
    [InlineData("numbers.SkipWhile(x => x < 3).Count()", "3")]
    [InlineData("numbers.Where(x => x > 1).Where(x => x < 5).Count()", "3")]
    public void LinqWithLambdas(string expression, string expected)
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("lambdas", "lambdas");
        Assert.Equal(expected, client.Evaluate(expression, top.Id).Result);
    }

    [Fact]
    public void TheResultOfWhereCanBeExpanded()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("lambdas", "lambdas");
        EvaluateResponseBody result = client.Evaluate("people.Where(p => p.Age > 26)", top.Id);
        Assert.Equal(2, result.IndexedVariables);
        Dictionary<string, Variable> elements = client.Variables(result.VariablesReference);
        Assert.Equal("\"Ann\"", client.Variables(elements["[0]"].VariablesReference)["Name"].Value);
        Assert.Equal("\"Bob\"", client.Variables(elements["[1]"].VariablesReference)["Name"].Value);
    }

    [Fact]
    public void LambdasWorkInConditionsAndLogpoints()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("lambdas");
        client.SetBreakpoints(new BreakpointSpec("lambdas", Condition: "people.Any(p => p.Age > 40)", LogMessage: "big: {numbers.Count(x => x > threshold)}"));
        client.Request("configurationDone");
        client.WaitForEvent("exited");
        Assert.Contains("big: 3", client.Output("console"));
    }

    [Theory]
    [InlineData("x => x + 1", "has no value by itself")]
    [InlineData("numbers.Where(x => x)", "must return bool")]
    [InlineData("numbers.Where(x => { var y = x; return y > 1; })", "expression body")]
    [InlineData("numbers.GroupBy(x => x % 2).Count()", "LINQ methods")]
    [InlineData("Add(x => x, 1)", "LINQ methods")]
    [InlineData("threshold.Where(x => x > 1)", "collection")]
    [InlineData("numbers.Where(x => x > 1, 2)", "exactly one argument")]
    [InlineData("numbers.Where(x => y > 1)", "'y' does not exist")]
    [InlineData("numbers.First(x => x > 100)", "no matching element")]
    public void UnsupportedLambdasAreExplained(string expression, string expectedMessage)
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("lambdas", "lambdas");
        Assert.Contains(expectedMessage, client.EvaluateError(expression, top.Id));
    }

    [Fact]
    public void LambdasAreNotEvaluatedOnHover()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("lambdas", "lambdas");
        DapMessage response = client.RequestRaw("evaluate", new { expression = "numbers.Where(x => x > 2).Count()", frameId = top.Id, context = "hover" });
        Assert.False(response.Success);
    }
}
