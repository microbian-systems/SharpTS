using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters.Modules;

/// <summary>
/// Emits IL code for the Node.js 'net' module.
/// </summary>
/// <remarks>
/// Provides TCP networking functionality:
/// - createServer() - creates a TCP server
/// - createConnection() / connect() - creates a TCP client socket
/// - isIP(), isIPv4(), isIPv6() - IP address utility functions
/// </remarks>
public sealed class NetModuleEmitter : IBuiltInModuleEmitter
{
    public string ModuleName => "net";

    private static readonly string[] _exportedMembers =
    [
        "createServer",
        "createConnection",
        "connect",
        "isIP",
        "isIPv4",
        "isIPv6",
        "Server",
        "Socket"
    ];

    public IReadOnlyList<string> GetExportedMembers() => _exportedMembers;

    public bool TryEmitMethodCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        return methodName switch
        {
            "createServer" => EmitCreateServer(emitter, arguments),
            "createConnection" or "connect" => EmitCreateConnection(emitter, arguments),
            "isIP" => EmitIsIP(emitter, arguments),
            "isIPv4" => EmitIsIPv4(emitter, arguments),
            "isIPv6" => EmitIsIPv6(emitter, arguments),
            _ => false
        };
    }

    public bool TryEmitPropertyGet(IEmitterContext emitter, string propertyName)
    {
        // Server and Socket are constructors, not simple properties
        return false;
    }

    private static bool EmitCreateServer(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Emit callback argument (optional)
        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        // Call $Runtime.NetCreateServer(callback)
        il.Emit(OpCodes.Call, ctx.Runtime!.NetCreateServer);
        emitter.SetStackUnknown();
        return true;
    }

    private static bool EmitCreateConnection(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Emit options/port argument
        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        // Emit callback argument (optional)
        if (arguments.Count > 1)
        {
            emitter.EmitExpression(arguments[1]);
            emitter.EmitBoxIfNeeded(arguments[1]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        // Call $Runtime.NetCreateConnection(options, callback)
        il.Emit(OpCodes.Call, ctx.Runtime!.NetCreateConnection);
        emitter.SetStackUnknown();
        return true;
    }

    private static bool EmitIsIP(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        il.Emit(OpCodes.Call, ctx.Runtime!.NetIsIP);
        emitter.SetStackUnknown();
        return true;
    }

    private static bool EmitIsIPv4(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        il.Emit(OpCodes.Call, ctx.Runtime!.NetIsIPv4);
        emitter.SetStackUnknown();
        return true;
    }

    private static bool EmitIsIPv6(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        il.Emit(OpCodes.Call, ctx.Runtime!.NetIsIPv6);
        emitter.SetStackUnknown();
        return true;
    }

    public bool IsExportedProperty(string memberName) => false;
}
