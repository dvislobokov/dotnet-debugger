using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;
using DotnetDebugger.Engine.Launch;
using DotnetDebugger.Engine.Symbols;
using DotnetDebugger.Engine.Values;

namespace DotnetDebugger.Engine;

public enum StepKind
{
    In,
    Over,
    Out,
}

internal enum EventAction
{
    /// <summary>Resume the debuggee.</summary>
    Continue,

    /// <summary>Stay stopped; the user has been (or is being) told.</summary>
    Stop,

    /// <summary>Stay stopped for the engine's own purposes (func-eval finished, breakpoint condition pending).</summary>
    InternalStop,
}

/// <summary>
/// Managed debugger built on ICorDebug. All public members are thread-safe; events are raised
/// outside of the engine lock, either from the ICorDebug callback thread or from the caller's thread.
/// </summary>
public sealed partial class DebugEngine : IDisposable
{
    private sealed class LoadedModule
    {
        public required int Id { get; init; }
        public required CorDebugModule Module { get; init; }
        public required string Path { get; init; }
        public ModuleMetadata? Metadata { get; init; }
    }

    private readonly object _lock = new();
    private readonly CorDebugManagedCallback _callback = new();
    private readonly ValueInspector _values;
    private readonly Dictionary<ulong, LoadedModule> _modules = [];
    private readonly ManualResetEventSlim _processWatcherDone = new();
    private bool _processWatcherStarted;
    private readonly List<Task> _outputPumps = [];

    private SymbolLocator? _symbolLocator;
    private DbgShim? _dbgShim;
    private CorDebug? _corDebug;
    private CorDebugProcess? _process;
    private LaunchedProcess? _launched;
    private Action? _resumeAction;
    private Task<int?>? _externalExitCode;
    private bool _launching;
    private IntPtr _unregisterToken;
    private int _processId;
    private bool _isAttach;
    private bool _justMyCode = true;
    private bool _stopAtEntry;
    private bool _configurationDone;
    private bool _resumed;
    private bool _stopped;
    private bool _processExited;
    private int _exitedRaised;
    private int _nextModuleId;
    private int _firstThreadId;
    private List<Action>? _pendingEvents;

    public event Action<StopInfo>? Stopped;
    public event Action<int>? Exited;

    /// <summary>
    /// The debuggee runs again although nobody asked for that (continue and the steps do; their responses say so).
    /// The one way there: an evaluation after which the runtime could not be stopped anymore.
    /// </summary>
    public event Action<int>? Continued;
    public event Action<string, string>? Output;
    public event Action<int, bool>? ThreadChanged;
    public event Action<ModuleLoadInfo>? ModuleLoaded;

    /// <summary>Symbols of an already loaded module became available.</summary>
    public event Action<ModuleLoadInfo>? ModuleChanged;
    public event Action<BreakpointInfo>? BreakpointChanged;

    /// <summary>Diagnostic messages of the engine itself.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Operating system id of the debuggee, 0 before launch/attach.</summary>
    public int ProcessId => _processId;

    public DebugEngine()
    {
        _values = new ValueInspector(GetMetadata, this);
        _callback.OnAnyEvent += OnManagedEvent;
    }

    // ---------------------------------------------------------------- session start

    public void Launch(LaunchOptions options)
    {
        lock (_lock)
        {
            if (_processId != 0 || _launching)
                throw new DebuggerException("A debuggee is already running.");
            if (!File.Exists(options.Program))
                throw new DebuggerException($"Program '{options.Program}' does not exist.");
            _launching = true;
            _dbgShim ??= LoadDbgShim();
        }

        // Process creation happens outside the lock: an external launcher waits for the DAP client, whose other
        // requests (breakpoints, configurationDone) must not pile up behind it.
        LaunchedProcess? launched = null;
        ExternalLaunch? external = null;
        try
        {
            if (options.ExternalLauncher != null)
            {
                external = options.ExternalLauncher();
            }
            else
            {
                launched = ProcessLauncher.Launch(_dbgShim, options.Program, options.Args,
                    options.WorkingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(options.Program)), options.Environment);
            }
        }
        catch (Exception)
        {
            lock (_lock)
                _launching = false;
            throw;
        }

        lock (_lock)
        {
            _justMyCode = options.JustMyCode;
            _stepFiltering = options.StepFiltering;
            _symbolLocator = new SymbolLocator(options.SymbolOptions ?? new SymbolOptions(), Log);
            SetSourceFileMap(options.SourceFileMap);
            _stopAtEntry = options.StopAtEntry;
            _launched = launched;
            _processId = launched?.ProcessId ?? external!.ProcessId;
            _resumeAction = launched?.Resume ?? external!.Resume;
            _externalExitCode = external?.ExitCode ?? launched?.ExitCode;

            if (launched != null)
            {
                StartOutputPump(launched.StdOut, "stdout");
                StartOutputPump(launched.StdErr, "stderr");
            }
            StartProcessWatcher();

            _unregisterToken = RegisterForRuntimeStartup(_processId);
            ResumeIfReady();
        }
    }

    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(20);
    private TaskCompletionSource<Exception?>? _attachOutcome;

    public void Attach(int processId, bool justMyCode = true, IReadOnlyDictionary<string, string>? sourceFileMap = null)
    {
        TaskCompletionSource<Exception?> outcome;
        lock (_lock)
        {
            if (_processId != 0)
                throw new DebuggerException("A debuggee is already running.");
            _dbgShim ??= LoadDbgShim();
            AttachChecks.Verify(processId, _dbgShim, Log);

            _justMyCode = justMyCode;
            SetSourceFileMap(sourceFileMap);
            _isAttach = true;
            _resumed = true;
            _processId = processId;
            _attachOutcome = outcome = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _unregisterToken = RegisterForRuntimeStartup(processId);
            }
            catch (Exception e)
            {
                // on Unix this is where a second debugger is turned away
                outcome.TrySetResult(e);
            }
        }

        // The runtime is there (checked), so the callback is due at once. Whether the attach worked is only known then.
        Exception? failure = outcome.Task.Wait(AttachTimeout)
            ? outcome.Task.Result
            : new DebuggerException("the runtime did not answer");
        lock (_lock)
        {
            _attachOutcome = null;
            if (failure == null)
            {
                StartProcessWatcher();
                return;
            }
            try
            {
                if (_unregisterToken != IntPtr.Zero)
                    _dbgShim.UnregisterForRuntimeStartup(_unregisterToken);
            }
            catch (Exception e)
            {
                Log?.Invoke("UnregisterForRuntimeStartup failed: " + e.Message);
            }
            _unregisterToken = IntPtr.Zero;
            _processId = 0;
            _isAttach = false;
            _resumed = false;
        }
        throw new DebuggerException($"Cannot attach to process {processId}: {ErrorText.Describe(failure)}");
    }

    /// <summary>Signals that initial breakpoints are in place and the debuggee may run.</summary>
    public void ConfigurationDone()
    {
        lock (_lock)
        {
            _configurationDone = true;
            ResumeIfReady();
        }
    }

    private DebuggingLibraryProvider? _libraryProvider;

    // The "3" variant lets us tell dbgshim where mscordbi/mscordaccore are, which single-file applications need.
    private IntPtr RegisterForRuntimeStartup(int processId)
    {
        _libraryProvider ??= new DebuggingLibraryProvider(Log);
        try
        {
            return _dbgShim!.RegisterForRuntimeStartup3(processId, null!, _libraryProvider, (RuntimeStartupCallback)OnRuntimeStartup, IntPtr.Zero);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or MissingMethodException or NotSupportedException)
        {
            Log?.Invoke("RegisterForRuntimeStartup3 is not available: " + e.Message);
            return _dbgShim!.RegisterForRuntimeStartup(processId, OnRuntimeStartup);
        }
    }

    private void SetSourceFileMap(IReadOnlyDictionary<string, string>? map)
    {
        _sourceFileMap.Clear();
        // longest prefix first, so nested mappings win over their parents
        foreach (var (pdbPrefix, localPrefix) in (map ?? new Dictionary<string, string>()).OrderByDescending(m => m.Key.Length))
            _sourceFileMap.Add((NormalizeSeparators(pdbPrefix), localPrefix));
    }

    private void ResumeIfReady()
    {
        if (_resumed || !_configurationDone || _resumeAction == null)
            return;
        _resumed = true;
        _resumeAction();
    }

    internal static DbgShim LoadDbgShim()
    {
        string file = OperatingSystem.IsWindows() ? "dbgshim.dll" : OperatingSystem.IsMacOS() ? "libdbgshim.dylib" : "libdbgshim.so";
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, file),
            Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file),
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return new DbgShim(NativeLibrary.Load(candidate));
        }
        if (NativeLibrary.TryLoad("dbgshim", typeof(DebugEngine).Assembly, null, out IntPtr handle))
            return new DbgShim(handle);
        throw new DebuggerException($"Unable to locate {file} next to the debugger.");
    }

    private void OnRuntimeStartup(CorDebug? corDebug, IntPtr parameter, HRESULT hr)
    {
        try
        {
            if (corDebug == null || hr != HRESULT.S_OK)
                throw new DebuggerException($"The .NET runtime could not be debugged: {ErrorText.Explain(hr.ToString())}.");
            // the debuggee waits for this callback to return: its console exists, managed code has not run yet
            if (_launched != null)
                ConsoleCodePage.SwitchToUtf8(_processId, Log);
            lock (_lock)
            {
                _corDebug = corDebug;
                corDebug.Initialize();
                corDebug.SetManagedHandler(_callback);
                _process = corDebug.DebugActiveProcess(_processId, false);
            }
            _attachOutcome?.TrySetResult(null);
        }
        catch (Exception e)
        {
            Log?.Invoke("Runtime startup failed: " + e);
            // a process somebody else started stays alive; the attach request reports the failure
            if (_attachOutcome is { } attach)
            {
                attach.TrySetResult(e);
                return;
            }
            Output?.Invoke("stderr", "Failed to start debugging: " + ErrorText.Describe(e) + Environment.NewLine);
            KillProcess();
        }
    }

    private void StartOutputPump(Stream stream, string category)
    {
        _outputPumps.Add(Task.Factory.StartNew(() =>
        {
            var buffer = new byte[4096];
            var chars = new char[4096];
            Decoder decoder = Encoding.UTF8.GetDecoder();
            try
            {
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    int count = decoder.GetChars(buffer, 0, n, chars, 0);
                    if (count > 0)
                        Output?.Invoke(category, new string(chars, 0, count));
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
            }
        }, TaskCreationOptions.LongRunning));
    }

    // The ExitProcess callback is never delivered when the runtime fails to start (or the process
    // dies abruptly), so process exit is observed independently of ICorDebug.
    private void StartProcessWatcher()
    {
        Process process;
        try
        {
            process = Process.GetProcessById(_processId);
            _ = process.SafeHandle;
        }
        catch (Exception e)
        {
            Log?.Invoke("Process watcher unavailable: " + e.Message);
            _processWatcherDone.Set();
            return;
        }

        _processWatcherStarted = true;
        bool isOurChild = !OperatingSystem.IsWindows() && _launched is { ExitCode: null };
        Task.Factory.StartNew(() =>
        {
            int? exitCode = null;
            try
            {
                // On Unix only the parent gets to know an exit code. The debuggee was forked by dbgshim inside this
                // process, so it is our child even though System.Diagnostics.Process does not know that.
                if (isOurChild)
                {
                    exitCode = WaitForChild(process.Id, out int error);
                    if (exitCode == null)
                        Log?.Invoke($"waitpid({process.Id}) failed with errno {error}: the exit code is lost");
                }
                process.WaitForExit();
                try
                {
                    exitCode ??= process.ExitCode;
                }
                catch (Exception)
                {
                    // Exit codes of non-child processes are not available on Unix.
                }
            }
            finally
            {
                _processWatcherDone.Set();
                RaiseExited(exitCode);
                process.Dispose();
            }
        }, TaskCreationOptions.LongRunning);
    }

    private static int? WaitForChild(int processId, out int error)
    {
        error = 0;
        while (true)
        {
            int waited = waitpid(processId, out int status, 0);
            if (waited == processId)
                return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
            if (waited < 0 && (error = Marshal.GetLastPInvokeError()) != 4 /* EINTR */)
                return null; // not our child, or somebody else reaped it
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    private void RaiseExited(int? exitCode)
    {
        if (Interlocked.Exchange(ref _exitedRaised, 1) != 0)
            return;
        lock (_lock)
        {
            _processExited = true;
            _stopped = false;
            Monitor.PulseAll(_lock);
        }
        Task.WaitAll(_outputPumps.ToArray(), TimeSpan.FromSeconds(2));
        // Whoever started the process knows best: for a process that is not our child Unix reports no (or a bogus 0) exit code.
        if (_externalExitCode is { } external && external.Wait(TimeSpan.FromSeconds(2)) && external.Result is { } reported)
            exitCode = reported;
        // On Windows the debugging pipeline is the native debugger of the process: when the exception arrives there
        // unhandled, it ends the process itself, with code 0. Without a debugger the same death is 0xE0434352.
        if (OperatingSystem.IsWindows() && _diedOfUnhandledException && exitCode is null or 0)
            exitCode = unchecked((int)0xE0434352);
        Exited?.Invoke(exitCode ?? 0);
    }

    // ---------------------------------------------------------------- session end

    /// <summary>What a killed process exits with: the code of Process.Kill on Windows. Elsewhere the signal decides (137).</summary>
    private const int TerminatedExitCode = -1;

    private bool _terminating;

    public void Terminate()
    {
        bool viaCorDebug = false;
        lock (_lock)
        {
            if (_processExited || _processId == 0)
                return;
            // "terminate" is followed by "disconnect", and that by Dispose: the process is on its way out already,
            // and a second ICorDebugProcess.Terminate only fails (the exit has not been reported yet)
            if (_terminating)
                return;
            if (_process != null && !_cannotSynchronize)
            {
                try
                {
                    if (!_stopped)
                        Synchronize(_process);
                    _process.Terminate(TerminatedExitCode);
                    viaCorDebug = true;
                    _terminating = true;
                }
                catch (DebugException e) when (e.HResult is HRESULT.CORDBG_E_PROCESS_TERMINATED)
                {
                    return; // it ended by itself in the meantime
                }
                catch (Exception e)
                {
                    Log?.Invoke("ICorDebugProcess.Terminate failed: " + e.Message);
                }
            }
        }
        if (!viaCorDebug)
            KillProcess();
    }

    public void Detach()
    {
        lock (_lock)
        {
            if (_processExited || _process == null)
                return;
            try
            {
                if (!_stopped)
                    Synchronize(_process);
                foreach (UserBreakpoint bp in AllBreakpoints)
                    Unbind(bp);
                DeactivateStepper();
                ClearAsyncStep();
                _process.Detach();
            }
            catch (Exception e)
            {
                Log?.Invoke("Detach failed: " + e.Message);
            }
            _process = null;
            _processExited = true;
            ClearStopState();
        }
    }

    private static readonly TimeSpan SynchronizeTimeout = TimeSpan.FromSeconds(10);
    private bool _cannotSynchronize;

    /// <summary>
    /// ICorDebugProcess.Stop with a way out. The runtime stops a thread at a safe point; .NET 8 on Unix does not interrupt
    /// a loop that has none (no calls, no allocations), so Stop never returns there (.NET 9 fixed it). The call cannot be
    /// taken back: the debugging interface stays blocked, and all that is left is to say so and to kill the process when asked.
    /// </summary>
    private void Synchronize(CorDebugProcess process)
    {
        const string Message = "The debuggee cannot be stopped: the runtime did not suspend its threads in time. The usual cause is a thread " +
            "in a loop without calls or allocations, which runtimes before .NET 9 cannot interrupt on Linux and macOS. The session can only be terminated.";
        if (_cannotSynchronize)
            throw new DebuggerException(Message);
        Task stop = Task.Run(() => process.Stop(0));
        bool finished;
        try
        {
            finished = stop.Wait(SynchronizeTimeout);
        }
        catch (AggregateException e)
        {
            throw e.InnerException ?? e;
        }
        if (finished)
            return;
        _cannotSynchronize = true;
        Log?.Invoke("ICorDebugProcess.Stop did not return.");
        Output?.Invoke("console", Message + Environment.NewLine);
        throw new DebuggerException(Message);
    }

    private void KillProcess()
    {
        try
        {
            using Process process = Process.GetProcessById(_processId);
            process.Kill();
        }
        catch (Exception)
        {
            // already gone
        }
    }

    public void Dispose()
    {
        if (_isAttach)
            Detach();
        else
            Terminate();

        lock (_lock)
        {
            try
            {
                if (_unregisterToken != IntPtr.Zero)
                    _dbgShim?.UnregisterForRuntimeStartup(_unregisterToken);
            }
            catch (Exception)
            {
            }
            _unregisterToken = IntPtr.Zero;
        }

        // no debuggee is left behind, whatever became of the polite way
        if (_processWatcherStarted && !_processWatcherDone.Wait(TimeSpan.FromSeconds(3)) && !_isAttach)
        {
            KillProcess();
            _processWatcherDone.Wait(TimeSpan.FromSeconds(3));
        }
        lock (_lock)
        {
            try
            {
                // behind a Stop that never returned this would block as well: the process is being killed, which is enough
                if (!_cannotSynchronize)
                    _corDebug?.Terminate();
            }
            catch (Exception)
            {
                // fails if the process has not been fully cleaned up yet; nothing to do about it
            }
            _corDebug = null;
            _launched?.StdIn.Dispose();
            _launched?.Abandon?.Invoke();
            foreach (LoadedModule module in _modules.Values)
                module.Metadata?.Dispose();
            _modules.Clear();
        }
    }

    // ---------------------------------------------------------------- callbacks

    private void OnManagedEvent(object? sender, CorDebugManagedCallbackEventArgs e)
    {
        List<Action> events = [];
        lock (_lock)
        {
            _pendingEvents = events;
            EventAction action = EventAction.Continue;
            try
            {
                action = HandleEvent(e);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Error in {e.Kind} handler: {ex}");
            }
            finally
            {
                _pendingEvents = null;
            }

            if (action != EventAction.Continue)
            {
                _stopped = true;
                if (action == EventAction.Stop)
                {
                    DeactivateStepper();
                    ClearAsyncStep();
                    ClearReturnSites();
                }
            }
            else if (e.Kind != CorDebugManagedCallbackKind.ExitProcess)
            {
                try
                {
                    e.Controller.Continue(false);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Continue after {e.Kind} failed: {ex.Message}");
                }
            }
            Monitor.PulseAll(_lock);
        }

        foreach (Action action in events)
            action();
    }

    private void Post(Action action)
    {
        if (_pendingEvents != null)
            _pendingEvents.Add(action);
        else
            action();
    }

    private EventAction HandleEvent(CorDebugManagedCallbackEventArgs e)
    {
        // While a func-eval runs nothing may stop the debuggee except the end of that evaluation.
        bool evaluating = _pendingEval != null;
        switch (e)
        {
            case CreateAppDomainCorDebugManagedCallbackEventArgs appDomain:
                appDomain.AppDomain.Attach();
                return EventAction.Continue;

            case LoadModuleCorDebugManagedCallbackEventArgs load:
                OnModuleLoaded(load.Module);
                return EventAction.Continue;

            case UnloadModuleCorDebugManagedCallbackEventArgs unload:
                if (_modules.Remove(unload.Module.BaseAddress.Value, out LoadedModule? removed))
                    removed.Metadata?.Dispose();
                _typeCache.Clear();
                return EventAction.Continue;

            case CreateThreadCorDebugManagedCallbackEventArgs thread:
            {
                int id = thread.Thread.Id;
                if (_firstThreadId == 0)
                    _firstThreadId = id;
                Post(() => ThreadChanged?.Invoke(id, true));
                return EventAction.Continue;
            }

            case ExitThreadCorDebugManagedCallbackEventArgs thread:
            {
                int id = thread.Thread.Id;
                Post(() => ThreadChanged?.Invoke(id, false));
                return EventAction.Continue;
            }

            case EvalCompleteCorDebugManagedCallbackEventArgs eval:
                return OnEvalFinished(eval.Eval, threwException: false) ? EventAction.InternalStop : EventAction.Continue;

            case EvalExceptionCorDebugManagedCallbackEventArgs eval:
                return OnEvalFinished(eval.Eval, threwException: true) ? EventAction.InternalStop : EventAction.Continue;

            case BreakpointCorDebugManagedCallbackEventArgs bp:
                return evaluating ? EventAction.Continue : OnBreakpointHit(bp);

            case StepCompleteCorDebugManagedCallbackEventArgs step:
                return evaluating ? EventAction.Continue : OnStepComplete(step);

            case BreakCorDebugManagedCallbackEventArgs brk:
            {
                if (evaluating)
                    return EventAction.Continue;
                int id = brk.Thread.Id;
                Post(() => Stopped?.Invoke(new StopInfo("pause", id, "Debugger.Break")));
                return EventAction.Stop;
            }

            case Exception2CorDebugManagedCallbackEventArgs exception:
                return evaluating ? EventAction.Continue : OnException(exception);

            case LogMessageCorDebugManagedCallbackEventArgs log:
            {
                string message = log.Message;
                Post(() => Output?.Invoke("console", message));
                return EventAction.Continue;
            }

            case ExitProcessCorDebugManagedCallbackEventArgs:
                _processExited = true;
                _stopped = false;
                _process = null;
                Task.Run(() =>
                {
                    _processWatcherDone.Wait(TimeSpan.FromSeconds(2));
                    RaiseExited(null);
                });
                return EventAction.Continue;

            default:
                return EventAction.Continue;
        }
    }

    private void OnModuleLoaded(CorDebugModule module)
    {
        string path = module.Name;
        ModuleMetadata? metadata = module.IsDynamic ? null
            : (module.IsInMemory ? null : ModuleMetadata.TryOpen(path)) ?? ReadModuleFromMemory(module, path);
        var loaded = new LoadedModule { Id = ++_nextModuleId, Module = module, Path = path, Metadata = metadata };
        _modules[module.BaseAddress.Value] = loaded;
        _typeCache.Clear();

        // Symbols that are not next to the module: directories and the cache are always worth a look, servers only
        // when the user wants to see code that is not theirs.
        if (metadata is { HasSymbols: false } && _symbolLocator?.Find(metadata, allowServers: !_justMyCode) is { } found)
            metadata.AttachSymbols(found.Provider, found.Foreign);

        bool hasSymbols = metadata?.HasSymbols == true;
        if (hasSymbols)
        {
            // Only possible before any code of the module has been jitted, i.e. not on attach.
            try
            {
                module.JITCompilerFlags = CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION;
            }
            catch (Exception e)
            {
                Log?.Invoke($"Optimizations stay enabled for {path}: {e.Message}");
            }
        }
        ApplySymbols(loaded);

        var info = new ModuleLoadInfo(loaded.Id, Path.GetFileName(path), path, hasSymbols);
        Post(() => ModuleLoaded?.Invoke(info));
    }

    private string? _programDirectory;

    private string? GetProgramDirectory()
    {
        if (_programDirectory != null)
            return _programDirectory;
        try
        {
            using Process process = Process.GetProcessById(_processId);
            return _programDirectory = Path.GetDirectoryName(process.MainModule?.FileName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Not a file on disk: part of a single-file bundle, or loaded from a byte array.
    private ModuleMetadata? ReadModuleFromMemory(CorDebugModule module, string path)
    {
        try
        {
            int size = module.Size;
            if (size <= 0 || size > 512 * 1024 * 1024 || _process == null)
                return null;
            // bundled assemblies are only known by name; their PDBs are next to the executable
            if (!Path.IsPathRooted(path) && GetProgramDirectory() is { } directory)
                path = Path.Combine(directory, path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? path : path + ".dll");
            byte[] image = _process.ReadMemory(module.BaseAddress, size);
            bool mapped = false;
            try
            {
                mapped = module.IsMappedLayout;
            }
            catch (Exception)
            {
            }
            return ModuleMetadata.TryOpenFromMemory(path, image, mapped);
        }
        catch (Exception e)
        {
            Log?.Invoke($"Cannot read module {path} from memory: {e.Message}");
            return null;
        }
    }

    /// <summary>Everything that depends on a module having symbols: "my code" status and breakpoints.</summary>
    private void ApplySymbols(LoadedModule loaded)
    {
        ModuleMetadata? metadata = loaded.Metadata;
        bool hasSymbols = metadata?.HasSymbols == true;
        // symbols fetched from a server or cache describe somebody else's code
        bool userCode = hasSymbols && !metadata!.SymbolsAreForeign;
        try
        {
            loaded.Module.SetJMCStatus(userCode, 0, null!);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"SetJMCStatus failed for {loaded.Path}: {ex.Message}");
        }

        if (!hasSymbols)
            return;
        if (userCode)
            MarkNonUserCode(loaded.Module, metadata!);
        BindBreakpoints(loaded);
        if (userCode && _stopAtEntry && _entryBreakpoint == null)
            TrySetEntryBreakpoint(loaded);
    }

    /// <summary>Looks for the symbols of one module everywhere, public symbol servers included.</summary>
    public bool LoadSymbols(int moduleId)
    {
        ModuleLoadInfo? changed = null;
        lock (_lock)
        {
            LoadedModule loaded = _modules.Values.FirstOrDefault(m => m.Id == moduleId)
                ?? throw new DebuggerException($"Unknown module {moduleId}.");
            if (loaded.Metadata == null)
                throw new DebuggerException("The module has no metadata on disk.");
            if (loaded.Metadata.HasSymbols)
                return true;

            _symbolLocator ??= new SymbolLocator(new SymbolOptions(), Log);
            if (_symbolLocator.Find(loaded.Metadata, allowServers: true, includePublicServers: true) is not { } found
                // a module the user asks symbols for is one they want to debug: it counts as their code from now on
                || !loaded.Metadata.AttachSymbols(found.Provider, foreign: false))
            {
                return false;
            }

            WhileSynchronized(() =>
            {
                ApplySymbols(loaded);
                return 0;
            });
            _typeCache.Clear();
            _threadFrames.Clear(); // frames of this module have source lines now
            changed = new ModuleLoadInfo(loaded.Id, Path.GetFileName(loaded.Path), loaded.Path, true);
        }
        ModuleChanged?.Invoke(changed);
        return true;
    }

    // [DebuggerHidden], [DebuggerStepThrough] and [DebuggerNonUserCode] take code out of "my code": steppers pass
    // through it and the call stack folds it into [External Code].
    private void MarkNonUserCode(CorDebugModule module, ModuleMetadata metadata)
    {
        try
        {
            foreach (int methodToken in metadata.GetNonUserCodeMethods())
                module.GetFunctionFromToken(new mdMethodDef(methodToken)).JMCStatus = false;
        }
        catch (Exception e)
        {
            Log?.Invoke($"Failed to apply non-user-code attributes of {metadata.Path}: {e.Message}");
        }
    }

    public IReadOnlyList<ModuleLoadInfo> GetModules()
    {
        lock (_lock)
        {
            return _modules.Values.OrderBy(m => m.Id)
                .Select(m => new ModuleLoadInfo(m.Id, Path.GetFileName(m.Path), m.Path, m.Metadata?.HasSymbols == true))
                .ToList();
        }
    }

    /// <summary>Source files the loaded symbols know about.</summary>
    public IReadOnlyList<SourceFileInfo> GetLoadedSources()
    {
        lock (_lock)
        {
            return _modules.Values.OrderBy(m => m.Id)
                .SelectMany(m => (m.Metadata?.GetDocuments() ?? []).Select(ToLocalPath))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Select(path => new SourceFileInfo(path))
                .ToList();
        }
    }

    private ModuleMetadata? GetMetadata(CorDebugModule module) =>
        _modules.TryGetValue(module.BaseAddress.Value, out LoadedModule? loaded) ? loaded.Metadata : null;

    // ---------------------------------------------------------------- execution control

    private CorDebugProcess RequireProcess() =>
        _process ?? throw new DebuggerException("The debuggee is not running.");

    private CorDebugThread RequireThread(int threadId)
    {
        CorDebugProcess process = RequireProcess();
        try
        {
            return process.GetThread(threadId);
        }
        catch (DebugException)
        {
            throw new DebuggerException($"Unknown thread {threadId}.");
        }
    }

    private CorDebugProcess RequireStopped()
    {
        CorDebugProcess process = RequireProcess();
        if (!_stopped)
            throw new DebuggerException("The debuggee is running.");
        return process;
    }

    private void ClearStopState()
    {
        _stopped = false;
        _frames.Clear();
        _externalFrames.Clear();
        _threadFrames.Clear();
        _variableHandles.Clear();
        _logicalFrames.Clear();
        _exceptionStops.Clear();
        _returnValue = null;
        _implicitEvalTimedOut = false;
        ReleaseStrongHandles();
    }

    private void ResumeProcess(CorDebugProcess process)
    {
        ClearStopState();
        process.Continue(false);
    }

    public void Continue()
    {
        lock (_lock)
        {
            if (_process == null || !_stopped)
                return;
            ClearAsyncStep();
            ResumeProcess(_process);
        }
    }

    public void Pause()
    {
        StopInfo? stop = null;
        lock (_lock)
        {
            CorDebugProcess process = RequireProcess();
            if (_stopped)
            {
                _pauseRequested |= _resolvingBreakpoint;
                return;
            }
            Synchronize(process);
            _stopped = true;
            stop = new StopInfo("pause", PickThreadForPause(process));
        }
        Stopped?.Invoke(stop);
    }

    private int PickThreadForPause(CorDebugProcess process)
    {
        CorDebugThread[] threads = process.Threads;
        foreach (CorDebugThread thread in threads)
        {
            try
            {
                if (thread.ActiveFrame is CorDebugILFrame frame && GetMetadata(frame.Function.Module)?.HasSymbols == true)
                    return thread.Id;
            }
            catch (Exception)
            {
            }
        }
        return threads.FirstOrDefault(t => t.Id == _firstThreadId)?.Id ?? threads.FirstOrDefault()?.Id ?? 0;
    }

    public IReadOnlyList<ThreadInfo> GetThreads()
    {
        lock (_lock)
        {
            if (_process == null)
                return [];

            bool stoppedHere = false;
            if (!_stopped)
            {
                Synchronize(_process);
                stoppedHere = true;
            }
            try
            {
                var result = new List<ThreadInfo>();
                foreach (CorDebugThread thread in _process.Threads)
                {
                    int id = thread.Id;
                    string? name = null;
                    try
                    {
                        name = _values.ReadString(_values.GetFieldByName(thread.Object, "_name"));
                    }
                    catch (Exception)
                    {
                    }
                    name ??= id == _firstThreadId ? "Main Thread" : ".NET Thread";
                    result.Add(new ThreadInfo(id, _frozenThreads.Contains(id) ? name + " (frozen)" : name));
                }
                return result;
            }
            finally
            {
                if (stoppedHere)
                    _process.Continue(false);
            }
        }
    }
}
