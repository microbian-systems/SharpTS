using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters;

/// <summary>
/// Emitter strategy for process static method calls and property access.
/// Handles process.cwd(), process.exit(), process.platform, process.env, etc.
/// </summary>
public sealed class ProcessStaticEmitter : IStaticTypeEmitterStrategy
{
    /// <summary>
    /// Attempts to emit IL for a process static method call.
    /// </summary>
    public bool TryEmitStaticCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        switch (methodName)
        {
            case "cwd":
                // Directory.GetCurrentDirectory()
                il.Emit(OpCodes.Call, ctx.Types.GetMethodNoParams(ctx.Types.Directory, "GetCurrentDirectory"));
                return true;

            case "chdir":
                // Directory.SetCurrentDirectory(path)
                if (arguments.Count > 0)
                {
                    emitter.EmitExpression(arguments[0]);
                    // Convert to string if needed
                    il.Emit(OpCodes.Callvirt, ctx.Types.GetMethodNoParams(ctx.Types.Object, "ToString"));
                    il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.Directory, "SetCurrentDirectory", ctx.Types.String));
                }
                il.Emit(OpCodes.Ldnull);
                return true;

            case "exit":
                // Environment.Exit(code)
                if (arguments.Count > 0)
                {
                    emitter.EmitExpressionAsDouble(arguments[0]);
                    il.Emit(OpCodes.Conv_I4); // Convert to int
                }
                else
                {
                    il.Emit(OpCodes.Ldc_I4_0); // Default exit code 0
                }
                il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.Environment, "Exit", ctx.Types.Int32));
                // Exit never returns, but we need to push something for the stack
                il.Emit(OpCodes.Ldnull);
                return true;

            case "hrtime":
                EmitHrtime(emitter, arguments);
                return true;

            case "uptime":
                EmitUptime(emitter);
                return true;

            case "memoryUsage":
                EmitMemoryUsage(emitter);
                return true;

            case "nextTick":
                EmitNextTick(emitter, arguments);
                return true;

            case "on":
            case "addListener":
            case "once":
            case "off":
            case "removeListener":
            case "removeAllListeners":
            case "prependListener":
            case "prependOnceListener":
            case "emit":
            case "listeners":
            case "listenerCount":
            case "eventNames":
            case "setMaxListeners":
            case "getMaxListeners":
                EmitProcessEventEmitterCall(emitter, methodName, arguments);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Attempts to emit IL for a process static property get.
    /// </summary>
    public bool TryEmitStaticPropertyGet(IEmitterContext emitter, string propertyName)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        switch (propertyName)
        {
            case "platform":
                // Emit platform string based on current OS
                EmitPlatformString(il);
                return true;

            case "arch":
                // Emit architecture string based on current architecture
                EmitArchString(il);
                return true;

            case "pid":
                // Environment.ProcessId
                il.Emit(OpCodes.Call, ctx.Types.GetPropertyGetter(ctx.Types.Environment, "ProcessId"));
                il.Emit(OpCodes.Conv_R8); // Convert to double for JS number
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "version":
                // "v" + Environment.Version.ToString()
                il.Emit(OpCodes.Ldstr, "v");
                il.Emit(OpCodes.Call, ctx.Types.GetPropertyGetter(ctx.Types.Environment, "Version"));
                var versionLocal = il.DeclareLocal(ctx.Types.Version);
                il.Emit(OpCodes.Stloc, versionLocal);
                il.Emit(OpCodes.Ldloca, versionLocal);
                il.Emit(OpCodes.Constrained, ctx.Types.Version);
                il.Emit(OpCodes.Callvirt, ctx.Types.GetMethodNoParams(ctx.Types.Object, "ToString"));
                il.Emit(OpCodes.Call, ctx.Types.GetMethod(ctx.Types.String, "Concat", ctx.Types.String, ctx.Types.String));
                return true;

            case "env":
                // Call runtime helper to create env object
                il.Emit(OpCodes.Call, ctx.Runtime!.ProcessGetEnv);
                return true;

            case "argv":
                // Call runtime helper to create argv array
                il.Emit(OpCodes.Call, ctx.Runtime!.ProcessGetArgv);
                return true;

            case "exitCode":
                // Environment.ExitCode
                il.Emit(OpCodes.Call, ctx.Types.GetPropertyGetter(ctx.Types.Environment, "ExitCode"));
                il.Emit(OpCodes.Conv_R8); // Convert to double for JS number
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            // Stream objects - return cached $Writable/$Readable singleton instances.
            // Member access (`process.stdin`) flips UsesNodeStreams via the detector's
            // HandleMemberAccess hook, so the helper is normally non-null when this
            // path runs. Null-guard regardless for defense-in-depth, mirroring
            // ProcessModuleEmitter's tolerant emission — emit Ldnull (-> JS undefined)
            // if the helper somehow wasn't generated.
            case "stdin":
                {
                    var m = ctx.Runtime?.GetStdin;
                    if (m is null) il.Emit(OpCodes.Ldnull); else il.Emit(OpCodes.Call, m);
                    return true;
                }

            case "stdout":
                {
                    var m = ctx.Runtime?.GetStdout;
                    if (m is null) il.Emit(OpCodes.Ldnull); else il.Emit(OpCodes.Call, m);
                    return true;
                }

            case "stderr":
                {
                    var m = ctx.Runtime?.GetStderr;
                    if (m is null) il.Emit(OpCodes.Ldnull); else il.Emit(OpCodes.Call, m);
                    return true;
                }

            // Methods accessible as properties (for typeof checks)
            case "nextTick":
                // Return a TSFunction wrapper for nextTick
                il.Emit(OpCodes.Call, ctx.Runtime!.ProcessGetNextTick);
                return true;

            default:
                return false;
        }
    }

    private static void EmitPlatformString(ILGenerator il)
    {
        // At compile time, we know the platform, so emit the string directly
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
    }

    private static void EmitArchString(ILGenerator il)
    {
        // At compile time, we know the architecture, so emit the string directly
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "ia32",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "unknown"
        };

        il.Emit(OpCodes.Ldstr, arch);
    }

    /// <summary>
    /// Emits IL for process.hrtime(prev?).
    /// Returns a [seconds, nanoseconds] array.
    /// </summary>
    private static void EmitHrtime(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Call runtime helper that handles hrtime logic
        // The helper takes an optional previous time array
        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }
        il.Emit(OpCodes.Call, ctx.Runtime!.ProcessHrtime);
    }

    /// <summary>
    /// Emits IL for process.uptime().
    /// Returns the number of seconds the process has been running.
    /// </summary>
    private static void EmitUptime(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Call runtime helper
        il.Emit(OpCodes.Call, ctx.Runtime!.ProcessUptime);
        il.Emit(OpCodes.Box, ctx.Types.Double);
    }

    /// <summary>
    /// Emits IL for process.memoryUsage().
    /// Returns an object with memory usage information.
    /// </summary>
    private static void EmitMemoryUsage(IEmitterContext emitter)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Call runtime helper
        il.Emit(OpCodes.Call, ctx.Runtime!.ProcessMemoryUsage);
    }

    /// <summary>
    /// Emits IL for process.nextTick(callback, ...args).
    /// Schedules callback to run via SetTimeout with delay 0.
    /// </summary>
    private static void EmitNextTick(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Emit callback - first argument
        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        // Delay is always 0 for nextTick
        il.Emit(OpCodes.Ldc_R8, 0.0);

        // Emit args array - remaining arguments (starting from index 1)
        EmitArgsArray(emitter, arguments, 1);

        // Call $Runtime.SetTimeout(callback, 0, args)
        il.Emit(OpCodes.Call, ctx.Runtime!.SetTimeout);

        // nextTick returns undefined, so pop the result and push null
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldnull);
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

    /// <summary>
    /// Emits IL for EventEmitter method calls on process (on, once, off, emit, etc.).
    /// Uses the compiled $EventEmitter singleton for process events.
    /// </summary>
    private static void EmitProcessEventEmitterCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        var runtime = ctx.Runtime!;

        switch (methodName)
        {
            case "on":
            case "addListener":
                // On(string eventName, object listener) -> $EventEmitter
                il.Emit(OpCodes.Call, runtime.GetProcessEventEmitter);
                EmitStringArg(emitter, arguments, 0);
                EmitBoxedArg(emitter, arguments, 1);
                il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterOn);
                break;

            case "once":
                il.Emit(OpCodes.Call, runtime.GetProcessEventEmitter);
                EmitStringArg(emitter, arguments, 0);
                EmitBoxedArg(emitter, arguments, 1);
                il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterOnce);
                break;

            case "off":
            case "removeListener":
                il.Emit(OpCodes.Call, runtime.GetProcessEventEmitter);
                EmitStringArg(emitter, arguments, 0);
                EmitBoxedArg(emitter, arguments, 1);
                il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterOff);
                break;

            case "emit":
                // Emit(string eventName, object[] args) -> bool
                il.Emit(OpCodes.Call, runtime.GetProcessEventEmitter);
                EmitStringArg(emitter, arguments, 0);
                // Pack remaining args into object[]
                var extraCount = Math.Max(0, arguments.Count - 1);
                il.Emit(OpCodes.Ldc_I4, extraCount);
                il.Emit(OpCodes.Newarr, ctx.Types.Object);
                for (int i = 1; i < arguments.Count; i++)
                {
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, i - 1);
                    emitter.EmitExpression(arguments[i]);
                    emitter.EmitBoxIfNeeded(arguments[i]);
                    il.Emit(OpCodes.Stelem_Ref);
                }
                il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterEmit);
                il.Emit(OpCodes.Box, ctx.Types.Boolean);
                break;

            case "removeAllListeners":
                // RemoveAllListeners(string eventName) -> $EventEmitter
                il.Emit(OpCodes.Call, runtime.GetProcessEventEmitter);
                EmitStringArg(emitter, arguments, 0);
                il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterRemoveAllListeners);
                break;

            case "listenerCount":
                // ListenerCount(string eventName) -> double
                il.Emit(OpCodes.Call, runtime.GetProcessEventEmitter);
                EmitStringArg(emitter, arguments, 0);
                il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterListenerCount);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                break;

            case "listeners":
                // Listeners(string eventName) -> TSArray
                il.Emit(OpCodes.Call, runtime.GetProcessEventEmitter);
                EmitStringArg(emitter, arguments, 0);
                il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterListeners);
                break;

            case "eventNames":
                // EventNames() -> TSArray
                il.Emit(OpCodes.Call, runtime.GetProcessEventEmitter);
                il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterEventNames);
                break;

            case "prependListener":
                il.Emit(OpCodes.Call, runtime.GetProcessEventEmitter);
                EmitStringArg(emitter, arguments, 0);
                EmitBoxedArg(emitter, arguments, 1);
                il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterPrependListener);
                break;

            case "prependOnceListener":
                il.Emit(OpCodes.Call, runtime.GetProcessEventEmitter);
                EmitStringArg(emitter, arguments, 0);
                EmitBoxedArg(emitter, arguments, 1);
                il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterPrependOnceListener);
                break;

            case "setMaxListeners":
                il.Emit(OpCodes.Call, runtime.GetProcessEventEmitter);
                if (arguments.Count > 0)
                    emitter.EmitExpressionAsDouble(arguments[0]);
                else
                    il.Emit(OpCodes.Ldc_R8, 10.0);
                il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterSetMaxListeners);
                break;

            case "getMaxListeners":
                il.Emit(OpCodes.Call, runtime.GetProcessEventEmitter);
                il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterGetMaxListeners);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                break;

            default:
                il.Emit(OpCodes.Ldnull);
                break;
        }
    }

    private static void EmitStringArg(IEmitterContext emitter, List<Expr> arguments, int index)
    {
        var il = emitter.Context.IL;
        if (index < arguments.Count)
        {
            emitter.EmitExpression(arguments[index]);
            emitter.EmitBoxIfNeeded(arguments[index]);
            il.Emit(OpCodes.Callvirt, emitter.Context.Types.GetMethodNoParams(emitter.Context.Types.Object, "ToString"));
        }
        else
        {
            il.Emit(OpCodes.Ldstr, "");
        }
    }

    private static void EmitBoxedArg(IEmitterContext emitter, List<Expr> arguments, int index)
    {
        var il = emitter.Context.IL;
        if (index < arguments.Count)
        {
            emitter.EmitExpression(arguments[index]);
            emitter.EmitBoxIfNeeded(arguments[index]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }
    }

    public bool HasStaticProperty(string memberName) => memberName is
        "platform" or "arch" or "pid" or "version" or "env" or "argv" or
        "exitCode" or "stdin" or "stdout" or "stderr";
}
