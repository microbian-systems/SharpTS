using System.Reflection.Emit;
using System.Runtime.InteropServices;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters.Modules;

/// <summary>
/// Emits IL code for the Node.js 'process' module.
/// Delegates to ProcessStaticEmitter for most operations.
/// </summary>
public sealed class ProcessModuleEmitter : IBuiltInModuleEmitter
{
    public string ModuleName => "process";

    private static readonly string[] _exportedMembers =
    [
        "platform", "arch", "pid", "version", "env", "argv", "exitCode",
        "stdin", "stdout", "stderr",
        "cwd", "chdir", "exit", "hrtime", "uptime", "memoryUsage", "nextTick"
    ];

    public IReadOnlyList<string> GetExportedMembers() => _exportedMembers;

    public bool TryEmitMethodCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        return methodName switch
        {
            "cwd" => EmitCwd(emitter),
            "chdir" => EmitChdir(emitter, arguments),
            "exit" => EmitExit(emitter, arguments),
            "hrtime" => EmitHrtime(emitter, arguments),
            "uptime" => EmitUptime(emitter),
            "memoryUsage" => EmitMemoryUsage(emitter),
            "nextTick" => EmitNextTick(emitter, arguments),
            _ => false
        };
    }

    public bool TryEmitPropertyGet(IEmitterContext emitter, string propertyName)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        return propertyName switch
        {
            "platform" => EmitPlatform(emitter),
            "arch" => EmitArch(emitter),
            "pid" => EmitPid(emitter),
            "version" => EmitVersion(emitter),
            "env" => EmitEnv(emitter),
            "argv" => EmitArgv(emitter),
            "exitCode" => EmitExitCode(emitter),
            "stdin" => EmitStdin(emitter),
            "stdout" => EmitStdout(emitter),
            "stderr" => EmitStderr(emitter),
            "nextTick" => EmitNextTickProperty(emitter),
            _ => false
        };
    }

    #region Method Emitters

    private static bool EmitCwd(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        il.Emit(OpCodes.Call, ctx.Types.GetMethodNoParams(ctx.Types.Directory, "GetCurrentDirectory"));
        return true;
    }

    private static bool EmitChdir(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            il.Emit(OpCodes.Callvirt, ctx.Types.GetMethodNoParams(ctx.Types.Object, "ToString"));
            il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.Directory, "SetCurrentDirectory", ctx.Types.String));
        }
        il.Emit(OpCodes.Ldnull);
        return true;
    }

    private static bool EmitExit(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpressionAsDouble(arguments[0]);
            il.Emit(OpCodes.Conv_I4);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_0);
        }
        il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.Environment, "Exit", ctx.Types.Int32));
        il.Emit(OpCodes.Ldnull);
        return true;
    }

    private static bool EmitHrtime(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.ProcessHrtime);
        return true;
    }

    private static bool EmitUptime(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        il.Emit(OpCodes.Call, ctx.Runtime!.ProcessUptime);
        il.Emit(OpCodes.Box, ctx.Types.Double);
        return true;
    }

    private static bool EmitMemoryUsage(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        il.Emit(OpCodes.Call, ctx.Runtime!.ProcessMemoryUsage);
        return true;
    }

    /// <summary>
    /// Emits: process.nextTick(callback, ...args)
    /// Implemented as setTimeout(callback, 0, ...args) - runs as soon as possible.
    /// Throws if callback is absent or null, matching the interpreter and Node.
    /// </summary>
    private static bool EmitNextTick(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Zero-arg call: throw at runtime. Matches ProcessModuleInterpreter.NextTick.
        if (arguments.Count == 0)
        {
            il.Emit(OpCodes.Ldstr, "Runtime Error: process.nextTick requires at least 1 argument");
            il.Emit(OpCodes.Newobj, ctx.Types.ArgumentException.GetConstructor([ctx.Types.String])!);
            il.Emit(OpCodes.Throw);
            // Throw is terminal, but the verifier needs a value-producing expression.
            il.Emit(OpCodes.Ldnull);
            return true;
        }

        // Emit callback, save to local so we can null-check and reuse.
        emitter.EmitExpression(arguments[0]);
        emitter.EmitBoxIfNeeded(arguments[0]);
        var cbLocal = il.DeclareLocal(ctx.Types.Object);
        il.Emit(OpCodes.Stloc, cbLocal);

        // if (cb == null) throw — matches interpreter "callback must be a function".
        var callbackOkLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, cbLocal);
        il.Emit(OpCodes.Brtrue, callbackOkLabel);
        il.Emit(OpCodes.Ldstr, "Runtime Error: process.nextTick callback must be a function");
        il.Emit(OpCodes.Newobj, ctx.Types.ArgumentException.GetConstructor([ctx.Types.String])!);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(callbackOkLabel);

        // Push validated callback for SetTimeout.
        il.Emit(OpCodes.Ldloc, cbLocal);

        // Delay is always 0 for nextTick
        il.Emit(OpCodes.Ldc_R8, 0.0);

        // Emit args array - remaining arguments (starting from index 1)
        EmitArgsArray(emitter, arguments, 1);

        // Call $Runtime.SetTimeout(callback, 0, args)
        il.Emit(OpCodes.Call, ctx.Runtime!.SetTimeout);

        // nextTick returns undefined, so pop the result and push null
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldnull);

        return true;
    }

    /// <summary>
    /// Emits an object[] array with remaining arguments starting from startIndex.
    /// </summary>
    private static void EmitArgsArray(IEmitterContext emitter, List<Expr> arguments, int startIndex)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        int extraArgCount = Math.Max(0, arguments.Count - startIndex);

        if (extraArgCount > 0)
        {
            // Create array with remaining arguments
            il.Emit(OpCodes.Ldc_I4, extraArgCount);
            il.Emit(OpCodes.Newarr, ctx.Types.Object);

            for (int i = startIndex; i < arguments.Count; i++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i - startIndex);
                emitter.EmitExpression(arguments[i]);
                emitter.EmitBoxIfNeeded(arguments[i]);
                il.Emit(OpCodes.Stelem_Ref);
            }
        }
        else
        {
            // Empty args array
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Newarr, ctx.Types.Object);
        }
    }

    #endregion

    #region Property Emitters

    private static bool EmitPlatform(IEmitterContext emitter)
    {
        var il = emitter.Context.IL;
        string platform;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            platform = "win32";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            platform = "linux";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            platform = "darwin";
        else
            platform = "unknown";
        il.Emit(OpCodes.Ldstr, platform);
        return true;
    }

    private static bool EmitArch(IEmitterContext emitter)
    {
        var il = emitter.Context.IL;
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "ia32",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "unknown"
        };
        il.Emit(OpCodes.Ldstr, arch);
        return true;
    }

    private static bool EmitPid(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        il.Emit(OpCodes.Call, ctx.Types.GetPropertyGetter(ctx.Types.Environment, "ProcessId"));
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, ctx.Types.Double);
        return true;
    }

    private static bool EmitVersion(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        il.Emit(OpCodes.Ldstr, "v");
        il.Emit(OpCodes.Call, ctx.Types.GetPropertyGetter(ctx.Types.Environment, "Version"));
        var versionLocal = il.DeclareLocal(ctx.Types.Version);
        il.Emit(OpCodes.Stloc, versionLocal);
        il.Emit(OpCodes.Ldloca, versionLocal);
        il.Emit(OpCodes.Constrained, ctx.Types.Version);
        il.Emit(OpCodes.Callvirt, ctx.Types.GetMethodNoParams(ctx.Types.Object, "ToString"));
        il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.String, "Concat", ctx.Types.String, ctx.Types.String));
        return true;
    }

    private static bool EmitEnv(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        il.Emit(OpCodes.Call, ctx.Runtime!.ProcessGetEnv);
        return true;
    }

    private static bool EmitArgv(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        il.Emit(OpCodes.Call, ctx.Runtime!.ProcessGetArgv);
        return true;
    }

    private static bool EmitExitCode(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        il.Emit(OpCodes.Call, ctx.Types.GetPropertyGetter(ctx.Types.Environment, "ExitCode"));
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, ctx.Types.Double);
        return true;
    }

    // process.stdio singletons depend on $Readable/$Writable, gated on
    // UsesNodeStreams. When the gate is off, runtime.GetStdin/Stdout/Stderr
    // are null MethodBuilders. Emit `null` (-> JS `undefined` on read) instead
    // of crashing at IL-emit time.
    //
    // The stdlib `process` shim re-exports stdin/stdout/stderr eagerly even
    // when downstream user code only uses `nextTick`; without this null-emit
    // path the shim wouldn't compile under conservative gating. Programs that
    // actually USE process.stdout.write etc. flip UsesNodeStreams via the
    // member-access trigger in HandleMemberAccess, keeping the helpers alive.
    private static bool EmitStdin(IEmitterContext emitter)
    {
        var il = emitter.Context.IL;
        var getStdin = emitter.Context.Runtime?.GetStdin;
        if (getStdin is null) il.Emit(OpCodes.Ldnull);
        else il.Emit(OpCodes.Call, getStdin);
        return true;
    }

    private static bool EmitStdout(IEmitterContext emitter)
    {
        var il = emitter.Context.IL;
        var getStdout = emitter.Context.Runtime?.GetStdout;
        if (getStdout is null) il.Emit(OpCodes.Ldnull);
        else il.Emit(OpCodes.Call, getStdout);
        return true;
    }

    private static bool EmitStderr(IEmitterContext emitter)
    {
        var il = emitter.Context.IL;
        var getStderr = emitter.Context.Runtime?.GetStderr;
        if (getStderr is null) il.Emit(OpCodes.Ldnull);
        else il.Emit(OpCodes.Call, getStderr);
        return true;
    }

    private static bool EmitNextTickProperty(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        // Return a TSFunction wrapper for nextTick
        il.Emit(OpCodes.Call, ctx.Runtime!.ProcessGetNextTick);
        return true;
    }

    #endregion

    public bool IsExportedProperty(string memberName) => memberName is
        "platform" or "arch" or "pid" or "version" or "env" or "argv" or "exitCode" or
        "stdin" or "stdout" or "stderr";
}
