using DotnetDebugger.Protocol;

namespace DotnetDebugger.Tests;

public class InspectionTests
{
    [Fact]
    public void ObjectExpansionShowsPropertiesBaseMembersAndStatics()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("basic", "locals");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        Dictionary<string, Variable> person = client.Variables(locals["person"].VariablesReference);
        Assert.Equal("\"Ann\"", person["Name"].Value);
        Assert.Equal("30", person["Age"].Value);
        Assert.Equal("\"Ann (30)\"", person["Summary"].Value); // computed property => func-eval
        Assert.Contains("NotSupportedException", person["Broken"].Value); // throwing getter must not break the list
        Assert.Equal("{Person:Bob}", person["Friend"].Value);
        Assert.DoesNotContain(person.Keys, k => k.Contains("k__BackingField"));

        Dictionary<string, Variable> statics = client.Variables(person["Static members"].VariablesReference);
        Assert.Equal("3", statics["Instances"].Value);
        Assert.Equal("\"human\"", statics["Species"].Value);

        // declared as Person, shown as the runtime type with derived + inherited members
        Assert.Equal("{Person:Eve}", locals["employee"].Value);
        Assert.Equal("TestApp.Employee", locals["employee"].Type);
        Dictionary<string, Variable> employee = client.Variables(locals["employee"].VariablesReference);
        Assert.Equal("\"Initech\"", employee["Company"].Value);
        Assert.Equal("\"Eve\"", employee["Name"].Value);
        Assert.Equal("\"Eve (40)\"", employee["Summary"].Value);

        // struct with a computed property
        Dictionary<string, Variable> point = client.Variables(locals["point"].VariablesReference);
        Assert.Equal("3", point["Sum"].Value);
    }

    [Fact]
    public void CollectionsAndSpecialValues()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("basic", "locals");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        Assert.Equal("Read | Write", locals["access"].Value);
        Assert.Equal("{int[2, 2]}", locals["grid"].Value);
        Dictionary<string, Variable> grid = client.Variables(locals["grid"].VariablesReference);
        Assert.Equal("3", grid["[1, 0]"].Value);

        Assert.Equal("Count = 2", locals["list"].Value);
        Dictionary<string, Variable> list = client.Variables(locals["list"].VariablesReference);
        Assert.Equal("\"a\"", list["[0]"].Value);
        Assert.Equal("\"b\"", list["[1]"].Value);

        Assert.Equal("Count = 1", locals["map"].Value);
        Dictionary<string, Variable> map = client.Variables(locals["map"].VariablesReference);
        Assert.Equal("1", map["[\"one\"]"].Value);
    }

    [Fact]
    public void ArrayPaging()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("basic", "locals");
        Variable numbers = client.Locals(top.Id)["numbers"];
        Assert.Equal(3, numbers.IndexedVariables);

        Variable[] page = client.Request<VariablesResponseBody>("variables",
            new { variablesReference = numbers.VariablesReference, filter = "indexed", start = 1, count = 2 }).Variables;
        Assert.Equal(["[1]", "[2]"], page.Select(v => v.Name));
        Assert.Equal(["20", "30"], page.Select(v => v.Value));
    }

    [Fact]
    public void VariablesCarryEvaluateNames()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("basic", "locals");
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("person", locals["person"].EvaluateName);

        Dictionary<string, Variable> person = client.Variables(locals["person"].VariablesReference);
        Assert.Equal("person.Friend", person["Friend"].EvaluateName);
        Assert.Equal("numbers[1]", client.Variables(locals["numbers"].VariablesReference)["[1]"].EvaluateName);
        Assert.Equal("31", client.Evaluate(person["Friend"].EvaluateName + ".Age", top.Id).Result);
    }

    [Fact]
    public void SetVariable()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("basic", "locals");
        int scope = Assert.Single(client.Request<ScopesResponseBody>("scopes", new { frameId = top.Id }).Scopes).VariablesReference;
        Dictionary<string, Variable> locals = client.Variables(scope);

        SetVariableResponseBody Set(int reference, string name, string value) =>
            client.Request<SetVariableResponseBody>("setVariable", new { variablesReference = reference, name, value });

        Assert.Equal("100", Set(scope, "number", "100").Value);
        Assert.Equal("\"changed\"", Set(scope, "text", "\"changed\"").Value);
        Assert.Equal("2.25", Set(scope, "ratio", "2.25").Value);
        Assert.Equal("false", Set(scope, "flag", "false").Value);
        Assert.Equal("55", Set(locals["person"].VariablesReference, "Age", "50 + 5").Value);
        Assert.Equal("9", Set(locals["point"].VariablesReference, "X", "9").Value);
        Assert.Equal("21", Set(locals["numbers"].VariablesReference, "[1]", "number - 79").Value);

        Assert.Equal("100", client.Evaluate("number", top.Id).Result);
        Assert.False(client.RequestRaw("setVariable", new { variablesReference = scope, name = "number", value = "\"text\"" }).Success);

        // the debuggee really sees the new values
        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.Contains("state: 100 changed 2.25 False 55 9 21", client.Output("stdout"));
    }

    [Fact]
    public void ExceptionDetails()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("exception");
        client.Request("setExceptionBreakpoints", new { filters = new[] { "unhandled" } });
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("exception");
        Assert.Contains("System.ArgumentException", stop.Description);
        Assert.Equal("fatal one", stop.Text);

        var info = client.Request<ExceptionInfoResponseBody>("exceptionInfo", new { threadId = stop.ThreadId });
        Assert.Equal("unhandled", info.BreakMode);
        Assert.Equal("System.ArgumentException", info.Details!.FullTypeName);
        Assert.Equal("ArgumentException", info.Details.TypeName);
        Assert.Equal("fatal one", info.Details.Message);
        Assert.Contains("TestApp.Program.Exceptions", info.Details.StackTrace);
        ExceptionDetails inner = Assert.Single(info.Details.InnerException!);
        Assert.Equal("System.FormatException", inner.FullTypeName);
        Assert.Equal("inner cause", inner.Message);

        // the exception object is inspectable
        StackFrame top = client.StackTrace(stop.ThreadId!.Value).First(f => f.Source != null);
        Assert.Equal("\"fatal one\"", client.Evaluate("$exception.Message", top.Id).Result);
    }

    [Fact]
    public void ThreadsHaveNamesAndIndependentStacks()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("threads", "worker");

        Protocol.Thread[] threads = client.Request<ThreadsResponseBody>("threads").Threads;
        Assert.Equal("worker-1", threads.Single(t => t.Id == threadId).Name);
        Assert.Equal("11", client.Locals(top.Id)["workerValue"].Value);

        Protocol.Thread main = threads.Single(t => t.Name == "Main Thread");
        StackFrame[] mainFrames = client.StackTrace(main.Id);
        Assert.Equal("[External Code]", mainFrames[0].Name); // Thread.Join
        Assert.Equal("TestApp.Program.Threads()", mainFrames[1].Name);
        Assert.Equal("\"threads\"", client.Evaluate("mode", mainFrames[2].Id).Result);
    }

    [Fact]
    public void JustMyCodeOffShowsFrameworkFrames()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("advanced", justMyCode: false);
        client.SetBreakpoints("linqWhere");
        client.Request("configurationDone");

        var (threadId, _) = client.Top(client.WaitForStop("breakpoint"));
        StackFrame[] frames = client.StackTrace(threadId);
        Assert.DoesNotContain(frames, f => f.Name == "[External Code]");
        Assert.Contains(frames, f => f.Name.StartsWith("System.Linq.Enumerable", StringComparison.Ordinal) && f.Source == null);
    }

    [Fact]
    public void StackTracePaging()
    {
        using var client = new DapClient();
        var (threadId, _) = client.RunTo("basic", "add");
        var all = client.Request<StackTraceResponseBody>("stackTrace", new { threadId });
        Assert.Equal(3, all.TotalFrames);
        var page = client.Request<StackTraceResponseBody>("stackTrace", new { threadId, startFrame = 1, levels = 1 });
        Assert.Equal(3, page.TotalFrames);
        Assert.Equal("TestApp.Program.Basic()", Assert.Single(page.StackFrames).Name);
    }
}
