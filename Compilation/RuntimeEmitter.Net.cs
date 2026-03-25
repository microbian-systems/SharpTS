using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Net module support for compiled TypeScript: net.createServer, net.createConnection, etc.
/// </summary>
public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits all net module methods.
    /// </summary>
    private void EmitNetModuleMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitNetCreateServer(typeBuilder, runtime);
        EmitNetCreateConnection(typeBuilder, runtime);
        EmitNetIsIP(typeBuilder, runtime);
        EmitNetIsIPv4(typeBuilder, runtime);
        EmitNetIsIPv6(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits: public static object NetCreateServer(object? callback)
    /// Creates a $NetServer directly (no reflection needed — standalone DLL support).
    /// </summary>
    private void EmitNetCreateServer(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "NetCreateServer",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.NetCreateServer = method;
        runtime.RegisterBuiltInModuleMethod("net", "createServer", method);
        runtime.RegisterBuiltInModuleMethod("net", "Server", method); // alias

        var il = method.GetILGenerator();

        // return new $NetServer(callback)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, runtime.NetServerCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object NetCreateConnection(object? options, object? callback)
    /// Creates a $NetSocket directly and calls Connect (no reflection needed).
    /// </summary>
    private void EmitNetCreateConnection(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "NetCreateConnection",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.NetCreateConnection = method;
        runtime.RegisterBuiltInModuleMethod("net", "createConnection", method);
        runtime.RegisterBuiltInModuleMethod("net", "connect", method); // alias

        var il = method.GetILGenerator();

        // var socket = new $NetSocket()
        var socketLocal = il.DeclareLocal(runtime.NetSocketType);
        il.Emit(OpCodes.Newobj, runtime.NetSocketCtor);
        il.Emit(OpCodes.Stloc, socketLocal);

        // socket.Connect(options, callback, null)
        var noOptions = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, noOptions);

        il.Emit(OpCodes.Ldloc, socketLocal);
        il.Emit(OpCodes.Ldarg_0); // options
        il.Emit(OpCodes.Ldarg_1); // callback
        il.Emit(OpCodes.Ldnull);  // third arg
        il.Emit(OpCodes.Callvirt, runtime.NetSocketConnect);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(noOptions);

        // return socket
        il.Emit(OpCodes.Ldloc, socketLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object NetIsIP(object? input)
    /// Returns 4 for IPv4, 6 for IPv6, 0 for invalid.
    /// </summary>
    private void EmitNetIsIP(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "NetIsIP",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.NetIsIP = method;
        runtime.RegisterBuiltInModuleMethod("net", "isIP", method);

        var il = method.GetILGenerator();

        // if (input is not string) return 0.0
        var isStringLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, isStringLabel);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(isStringLabel);

        // IPAddress.TryParse(input as string, out addr)
        var addrLocal = il.DeclareLocal(typeof(IPAddress));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ldloca, addrLocal);
        il.Emit(OpCodes.Call, typeof(IPAddress).GetMethod("TryParse", [_types.String, typeof(IPAddress).MakeByRefType()])!);

        var validLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, validLabel);

        // Not valid
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(validLabel);

        // Check address family
        il.Emit(OpCodes.Ldloc, addrLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPAddress).GetProperty("AddressFamily")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4, (int)AddressFamily.InterNetworkV6);

        var isV6Label = il.DefineLabel();
        il.Emit(OpCodes.Beq, isV6Label);

        // IPv4
        il.Emit(OpCodes.Ldc_R8, 4.0);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);

        // IPv6
        il.MarkLabel(isV6Label);
        il.Emit(OpCodes.Ldc_R8, 6.0);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object NetIsIPv4(object? input)
    /// </summary>
    private void EmitNetIsIPv4(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "NetIsIPv4",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.NetIsIPv4 = method;
        runtime.RegisterBuiltInModuleMethod("net", "isIPv4", method);

        var il = method.GetILGenerator();

        // if (input is not string) return false
        var isStringLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, isStringLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(isStringLabel);

        var addrLocal = il.DeclareLocal(typeof(IPAddress));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ldloca, addrLocal);
        il.Emit(OpCodes.Call, typeof(IPAddress).GetMethod("TryParse", [_types.String, typeof(IPAddress).MakeByRefType()])!);

        var validLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, validLabel);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(validLabel);

        il.Emit(OpCodes.Ldloc, addrLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPAddress).GetProperty("AddressFamily")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4, (int)AddressFamily.InterNetwork);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object NetIsIPv6(object? input)
    /// </summary>
    private void EmitNetIsIPv6(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "NetIsIPv6",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.NetIsIPv6 = method;
        runtime.RegisterBuiltInModuleMethod("net", "isIPv6", method);

        var il = method.GetILGenerator();

        var isStringLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, isStringLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(isStringLabel);

        var addrLocal = il.DeclareLocal(typeof(IPAddress));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ldloca, addrLocal);
        il.Emit(OpCodes.Call, typeof(IPAddress).GetMethod("TryParse", [_types.String, typeof(IPAddress).MakeByRefType()])!);

        var validLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, validLabel);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(validLabel);

        il.Emit(OpCodes.Ldloc, addrLocal);
        il.Emit(OpCodes.Callvirt, typeof(IPAddress).GetProperty("AddressFamily")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4, (int)AddressFamily.InterNetworkV6);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);
    }
}
