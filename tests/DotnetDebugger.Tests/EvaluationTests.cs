using DotnetDebugger.Protocol;

namespace DotnetDebugger.Tests;

public class EvaluationTests
{
    [Theory]
    // literals and operators
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(number + 8) / 5", "10")]
    [InlineData("number % 5", "2")]
    [InlineData("-number", "-42")]
    [InlineData("ratio * 2", "3")]
    [InlineData("number / 4.0", "10.5")]
    [InlineData("number > 40 && flag", "true")]
    [InlineData("number < 40 || !flag", "false")]
    [InlineData("number == 42", "true")]
    [InlineData("number != 42", "false")]
    [InlineData("flag ? number : 0", "42")]
    [InlineData("\"a\" + \"b\"", "\"ab\"")]
    [InlineData("text + number", "\"hello \\\"world\\\"42\"")]
    [InlineData("'x' == letter", "true")]
    [InlineData("null == nobody", "true")]
    // locals, fields, properties
    [InlineData("number", "42")]
    [InlineData("text.Length", "13")]
    [InlineData("person.Name", "\"Ann\"")]
    [InlineData("person.Friend.Age * 2", "62")]
    [InlineData("person.Friend != null", "true")]
    [InlineData("nobody == null", "true")]
    [InlineData("nobody?.Name", "null")]
    [InlineData("person?.Name", "\"Ann\"")]
    [InlineData("nobody ?? person", "{Person:Ann}")]
    [InlineData("person.Summary", "\"Ann (30)\"")]
    [InlineData("point.X + point.Y", "3")]
    [InlineData("point.Sum", "3")]
    [InlineData("employee.Name", "\"Eve\"")]
    [InlineData("((TestApp.Employee)employee).Company", "\"Initech\"")]
    [InlineData("(double)number / 5", "8.4")]
    [InlineData("(int)ratio", "1")]
    [InlineData("maybe", "7")]
    [InlineData("maybe.Value + 1", "8")]
    [InlineData("maybe.HasValue", "true")]
    [InlineData("nothing.HasValue", "false")]
    [InlineData("money", "12.34")]
    [InlineData("boxed", "99")]
    // arrays, indexers
    [InlineData("numbers[1]", "20")]
    [InlineData("numbers.Length", "3")]
    [InlineData("numbers[numbers.Length - 1]", "30")]
    [InlineData("grid[1, 0]", "3")]
    [InlineData("list[0]", "\"a\"")]
    [InlineData("list.Count", "2")]
    [InlineData("map[\"one\"]", "1")]
    [InlineData("text[0]", "'h'")]
    // method calls
    [InlineData("person.Greet(\"hi\")", "\"hi, Ann\"")]
    [InlineData("person.ToString()", "\"Person:Ann\"")]
    [InlineData("number.ToString()", "\"42\"")]
    [InlineData("text.ToUpper()", "\"HELLO \\\"WORLD\\\"\"")]
    [InlineData("text.Contains(\"world\")", "true")]
    [InlineData("text.Substring(1, 3)", "\"ell\"")]
    [InlineData("list.Contains(\"b\")", "true")]
    [InlineData("Add(number, 8)", "50")]
    [InlineData("Program.Add(1, 2)", "3")]
    [InlineData("TestApp.Program.Add(1, 2) + 1", "4")]
    [InlineData("string.Concat(\"x\", \"y\")", "\"xy\"")]
    [InlineData("System.Math.Max(3, number)", "42")]
    [InlineData("Math.Abs(-5)", "5")]
    // statics, enums, constants
    [InlineData("Person.Instances", "3")]
    [InlineData("TestApp.Person.Instances", "3")]
    [InlineData("Person.Species", "\"human\"")]
    [InlineData("color", "Green")]
    [InlineData("color == Color.Green", "true")]
    [InlineData("color == TestApp.Color.Red", "false")]
    [InlineData("Color.Blue", "Blue")]
    [InlineData("(int)color", "1")]
    [InlineData("access", "Read | Write")]
    [InlineData("int.MaxValue", "2147483647")]
    [InlineData("string.Empty", "\"\"")]
    // type tests
    [InlineData("employee is TestApp.Employee", "true")]
    [InlineData("person is Employee", "false")]
    [InlineData("(employee as Employee).Company", "\"Initech\"")]
    public void EvaluatesExpression(string expression, string expected)
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("basic", "locals");
        Assert.Equal(expected, client.Evaluate(expression, top.Id).Result);
    }

    [Fact]
    public void EvaluatesManyExpressionsInOneStop()
    {
        // func-evals resume the process; frames and handles must survive that
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("basic", "locals");

        Dictionary<string, Variable> before = client.Locals(top.Id);
        Assert.Equal("\"Person:Ann\"", client.Evaluate("person.ToString()", top.Id).Result);
        Assert.Equal("\"hi, Bob\"", client.Evaluate("person.Friend.Greet(\"hi\")", top.Id).Result);
        Assert.Equal("43", client.Evaluate("number + 1", top.Id).Result);

        // handles obtained before the evals are still usable
        Assert.Equal("\"Ann\"", client.Variables(before["person"].VariablesReference)["Name"].Value);
        Assert.Equal("2", client.Variables(before["point"].VariablesReference)["Y"].Value);
        Assert.Equal("TestApp.Program.Main()", client.StackTrace(threadId)[1].Name);

        // evaluation in the caller's frame
        StackFrame main = client.StackTrace(threadId)[1];
        Assert.Equal("\"basic\"", client.Evaluate("mode", main.Id).Result);
        Assert.Equal("\"basic\"", client.Evaluate("args[0]", main.Id).Result);
    }

    [Fact]
    public void EvaluationResultIsExpandable()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("basic", "locals");

        EvaluateResponseBody person = client.Evaluate("person.Friend", top.Id);
        Assert.Equal("{Person:Bob}", person.Result);
        Assert.Equal("TestApp.Person", person.Type);
        Assert.True(person.VariablesReference > 0);
        Assert.Equal("\"Bob\"", client.Variables(person.VariablesReference)["Name"].Value);

        EvaluateResponseBody array = client.Evaluate("numbers", top.Id);
        Assert.Equal(3, array.IndexedVariables);
        Assert.Equal("30", client.Variables(array.VariablesReference)["[2]"].Value);
    }

    [Theory]
    [InlineData("missing", "missing")]
    [InlineData("person.Missing", "Missing")]
    [InlineData("person.Broken", "NotSupportedException")]
    [InlineData("nobody.Name", "NullReferenceException")]
    [InlineData("numbers[10]", "IndexOutOfRangeException")]
    [InlineData("number +", "")]
    [InlineData("number / 0", "DivideByZeroException")]
    public void ReportsEvaluationErrors(string expression, string expectedInMessage)
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("basic", "locals");
        Assert.Contains(expectedInMessage, client.EvaluateError(expression, top.Id));

        // a failed evaluation must leave the session usable
        Assert.Equal("42", client.Evaluate("number", top.Id).Result);
        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
    }

    [Fact]
    public void EvaluatesInInstanceGenericLambdaAndAsyncFrames()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("advanced", "lambdaBody", "generic");
        Assert.Equal("7", client.Evaluate("x + factor", top.Id).Result);

        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal("\"payload\"", client.Evaluate("_value", top.Id).Result);
        Assert.Equal("7", client.Evaluate("this._value.Length", top.Id).Result);
        Assert.Equal("8", client.Evaluate("extra + 1", top.Id).Result);
        Assert.Equal("\"payload:1\"", client.Evaluate("Describe(1)", top.Id).Result);

        using var asyncClient = new DapClient();
        (_, top) = asyncClient.RunTo("async", "async");
        Assert.Equal("15", asyncClient.Evaluate("doubled + input", top.Id).Result);
    }

    [Fact]
    public void HoverEvaluationHasNoSideEffects()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("basic", "locals");
        Assert.Equal("42", client.Evaluate("number", top.Id, "hover").Result);
        Assert.Equal("\"Ann\"", client.Evaluate("person.Name", top.Id, "hover").Result);
        // method calls are not something a mouse-over should trigger
        DapMessage response = client.RequestRaw("evaluate", new { expression = "person.Greet(\"x\")", frameId = top.Id, context = "hover" });
        Assert.False(response.Success);
    }
}
