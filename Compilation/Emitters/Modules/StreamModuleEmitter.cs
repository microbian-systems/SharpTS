using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters.Modules;

/// <summary>
/// Emits IL code for the Node.js 'stream' module.
/// Exports Readable, Writable, Duplex, Transform, PassThrough stream constructors,
/// plus utility functions finished(), pipeline(), and addAbortSignal().
/// </summary>
public sealed class StreamModuleEmitter : IBuiltInModuleEmitter
{
    public string ModuleName => "stream";

    private static readonly string[] _exportedMembers =
    [
        "Readable",
        "Writable",
        "Duplex",
        "Transform",
        "PassThrough",
        "finished",
        "pipeline",
        "addAbortSignal",
        "promises"
    ];

    public IReadOnlyList<string> GetExportedMembers() => _exportedMembers;

    public bool TryEmitMethodCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        switch (methodName)
        {
            case "finished":
                EmitFinishedCall(emitter, arguments);
                return true;
            case "pipeline":
                EmitPipelineCall(emitter, arguments);
                return true;
            case "addAbortSignal":
                EmitAddAbortSignalCall(emitter, arguments);
                return true;
            case "Readable.from":
                EmitReadableFromCall(emitter, arguments);
                return true;
            default:
                return false;
        }
    }

    public bool TryEmitPropertyGet(IEmitterContext emitter, string propertyName)
    {
        if (!_exportedMembers.Contains(propertyName))
            return false;

        var ctx = emitter.Context;
        var il = ctx.IL;

        if (propertyName == "promises")
        {
            il.Emit(OpCodes.Ldstr, "[stream/promises]");
            return true;
        }

        // Emit a placeholder value for the stream constructor or function.
        il.Emit(OpCodes.Ldstr, $"[{propertyName}]");
        return true;
    }

    private static void EmitFinishedCall(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Pack args into object[]
        il.Emit(OpCodes.Ldc_I4, arguments.Count);
        il.Emit(OpCodes.Newarr, typeof(object));

        for (int i = 0; i < arguments.Count; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            emitter.EmitExpression(arguments[i]);
            emitter.EmitBoxIfNeeded(arguments[i]);
            il.Emit(OpCodes.Stelem_Ref);
        }

        il.Emit(OpCodes.Call, ctx.Runtime!.StreamFinished);
    }

    private static void EmitPipelineCall(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        il.Emit(OpCodes.Ldc_I4, arguments.Count);
        il.Emit(OpCodes.Newarr, typeof(object));

        for (int i = 0; i < arguments.Count; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            emitter.EmitExpression(arguments[i]);
            emitter.EmitBoxIfNeeded(arguments[i]);
            il.Emit(OpCodes.Stelem_Ref);
        }

        il.Emit(OpCodes.Call, ctx.Runtime!.StreamPipeline);
    }

    private static void EmitAddAbortSignalCall(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Simply return the stream (second argument)
        if (arguments.Count >= 2)
        {
            emitter.EmitExpression(arguments[1]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }
    }

    private static void EmitReadableFromCall(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Pack args into object[]
        il.Emit(OpCodes.Ldc_I4, arguments.Count);
        il.Emit(OpCodes.Newarr, typeof(object));

        for (int i = 0; i < arguments.Count; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            emitter.EmitExpression(arguments[i]);
            emitter.EmitBoxIfNeeded(arguments[i]);
            il.Emit(OpCodes.Stelem_Ref);
        }

        il.Emit(OpCodes.Call, ctx.Runtime!.StreamReadableFrom);
    }
}
