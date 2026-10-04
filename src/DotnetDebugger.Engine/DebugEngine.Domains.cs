using System.Diagnostics.CodeAnalysis;
using ClrDebug;
using DotnetDebugger.Engine.Values;

namespace DotnetDebugger.Engine;

// Several AppDomains in one process (.NET Framework: ASP.NET on IIS, test runners). Every domain gets its own
// instance of each module it loads, but instances of one image may share a base address: domain-neutral assemblies
// (mscorlib, anything loaded with LoaderOptimization.MultiDomain) and the exe itself. A module is therefore identified
// by base address and AppDomain, and everything bound to a module (breakpoints, types for the evaluator) is bound to
// the instance in the right domain. .NET (Core) has a single AppDomain, so there the base address alone decides.
public sealed partial class DebugEngine
{
    /// <summary>The default AppDomain: the only one of .NET (Core), the first one of a .NET Framework process.</summary>
    private const int DefaultAppDomainId = 1;

    private readonly record struct ModuleKey(ulong BaseAddress, int AppDomainId);

    /// <summary>The loaded module instances, looked up by the ICorDebug module of any of them.</summary>
    private sealed class ModuleTable
    {
        private readonly Dictionary<ModuleKey, LoadedModule> _byKey = [];
        private readonly Dictionary<ulong, List<LoadedModule>> _byAddress = [];

        public IEnumerable<LoadedModule> Values => _byKey.Values;

        /// <returns>The instance this one replaces (the same module reported twice), for the caller to dispose.</returns>
        public LoadedModule? Add(LoadedModule module)
        {
            LoadedModule? replaced = Remove(module.Key);
            _byKey[module.Key] = module;
            if (!_byAddress.TryGetValue(module.Key.BaseAddress, out List<LoadedModule>? list))
                _byAddress[module.Key.BaseAddress] = list = [];
            list.Add(module);
            return replaced;
        }

        public bool TryGetValue(CorDebugModule module, [NotNullWhen(true)] out LoadedModule? loaded)
        {
            loaded = null;
            if (!_byAddress.TryGetValue(module.BaseAddress.Value, out List<LoadedModule>? list))
                return false;
            // the common case, and the only one of .NET (Core): no need to ask which domain the module is in
            if (list.Count == 1)
            {
                loaded = list[0];
            }
            else
            {
                // a module whose domain cannot be told matches none rather than the default domain's instance
                int appDomainId = AppDomainIdOf(module, fallback: -1);
                loaded = list.Find(m => m.Key.AppDomainId == appDomainId);
            }
            return loaded != null;
        }

        public LoadedModule this[CorDebugModule module] =>
            TryGetValue(module, out LoadedModule? loaded) ? loaded : throw new DebuggerException("The module is not loaded.");

        public LoadedModule? Remove(ModuleKey key)
        {
            if (!_byKey.Remove(key, out LoadedModule? removed))
                return null;
            List<LoadedModule> list = _byAddress[key.BaseAddress];
            list.Remove(removed);
            if (list.Count == 0)
                _byAddress.Remove(key.BaseAddress);
            return removed;
        }

        public void Clear()
        {
            _byKey.Clear();
            _byAddress.Clear();
        }
    }

    private static int AppDomainIdOf(CorDebugModule module, int fallback = DefaultAppDomainId)
    {
        try
        {
            return module.Assembly.AppDomain.Id;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>Friendly name of the AppDomain of a module outside the default domain, null otherwise.</summary>
    private static string? AppDomainNameOf(CorDebugModule module, int appDomainId)
    {
        if (appDomainId == DefaultAppDomainId)
            return null;
        try
        {
            return module.Assembly.AppDomain.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private ModuleKey KeyOf(CorDebugModule module) =>
        _modules.TryGetValue(module, out LoadedModule? loaded) ? loaded.Key : new ModuleKey(module.BaseAddress.Value, AppDomainIdOf(module));

    /// <summary>"mscorlib.dll", or "mscorlib.dll [SecondDomain]" for the instance of another domain.</summary>
    /// <param name="refresh">
    /// Ask for the domain's name again: the first modules of a new domain load before AppDomain.CreateDomain gives
    /// the domain its name (until then the runtime calls it "Domain 2").
    /// </param>
    private static ModuleLoadInfo DescribeModule(LoadedModule module, bool refresh = true)
    {
        int appDomainId = module.Key.AppDomainId;
        if (refresh && appDomainId != DefaultAppDomainId)
            module.AppDomainName = AppDomainNameOf(module.Module, appDomainId) ?? module.AppDomainName;
        string domain = appDomainId == DefaultAppDomainId ? "" : $" [{module.AppDomainName ?? "AppDomain " + appDomainId}]";
        return new(module.Id, Path.GetFileName(module.Path) + domain, module.Path, module.Metadata?.HasSymbols == true);
    }

    /// <summary>The AppDomain the code of a frame runs in.</summary>
    private int AppDomainOf(FrameRef frame)
    {
        try
        {
            return GetILFrame(frame) is { } ilFrame ? KeyOf(ilFrame.Function.Module).AppDomainId : DefaultAppDomainId;
        }
        catch (Exception)
        {
            return DefaultAppDomainId;
        }
    }

    /// <summary>The AppDomain of the frame a value was found in, or else the one its thread currently runs in.</summary>
    private int AppDomainOf(InspectionContext context)
    {
        if (context.Frame != null)
            return AppDomainOf(context.Frame);
        try
        {
            return RequireThread(context.ThreadId).AppDomain.Id;
        }
        catch (Exception)
        {
            return DefaultAppDomainId;
        }
    }

    /// <summary>
    /// A domain got its name after some of its modules were announced (mscorlib loads into it as "Domain 2"):
    /// those modules are announced again under the right name.
    /// </summary>
    private void RefreshAppDomainName(int appDomainId)
    {
        if (appDomainId == DefaultAppDomainId)
            return;
        foreach (LoadedModule module in _modules.Values.Where(m => m.Key.AppDomainId == appDomainId))
        {
            string? known = module.AppDomainName;
            ModuleLoadInfo info = DescribeModule(module);
            if (known != module.AppDomainName)
                Post(() => ModuleChanged?.Invoke(info));
        }
    }

    /// <summary>
    /// A module goes away with its AppDomain (or its collectible AssemblyLoadContext): whatever was bound to this
    /// instance goes with it. Instances in other domains stay as they are.
    /// </summary>
    private void OnModuleUnloaded(CorDebugModule module)
    {
        _typeCache.Clear();
        if (_modules.Remove(KeyOf(module)) is not { } removed)
            return;

        foreach (UserBreakpoint bp in AllBreakpoints)
        {
            if (bp.Bound.RemoveAll(l => l.Module == removed.Key) == 0)
                continue;
            if (!bp.Verified)
            {
                BreakpointInfo info = bp.ToInfo();
                Post(() => BreakpointChanged?.Invoke(info));
            }
        }
        foreach (CodeLocation location in _nativeBreakpoints.Keys.Where(l => l.Module == removed.Key).ToList())
            _nativeBreakpoints.Remove(location);
        if (_entryBreakpoint?.Module == removed.Key)
            _entryBreakpoint = null;
        foreach (int id in _gotoTargets.Where(t => t.Value.Module == removed).Select(t => t.Key).ToList())
            _gotoTargets.Remove(id);
        foreach (int id in _sourceReferences.Where(s => s.Value.Module == removed).Select(s => s.Key).ToList())
            _sourceReferences.Remove(id);

        removed.Metadata?.Dispose();
        ModuleLoadInfo unloaded = DescribeModule(removed, refresh: false);
        Post(() => ModuleUnloaded?.Invoke(unloaded));
    }

    // ---------------------------------------------------------------- evaluator

    private sealed partial class Evaluator
    {
        private int? _appDomainId;

        /// <summary>Types are looked up in the AppDomain of the frame: each domain has its own copy of every type.</summary>
        private (LoadedModule Module, int Token)? FindType(string fullName) =>
            engine.FindType(fullName, _appDomainId ??= engine.AppDomainOf(frame));

        private bool InFrameDomain(LoadedModule module) => module.Key.AppDomainId == (_appDomainId ??= engine.AppDomainOf(frame));

        private CorDebugAppDomain FrameAppDomain() =>
            engine.GetILFrame(frame)?.Function.Module.Assembly.AppDomain ?? engine.RequireProcess().AppDomains.First();
    }
}
