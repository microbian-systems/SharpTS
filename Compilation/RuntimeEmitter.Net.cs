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
    /// Creates a SharpTSNetServer via reflection for standalone DLL support.
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

        var il = method.GetILGenerator();

        // Use reflection to create SharpTSNetServer
        // Type.GetType("SharpTS.Runtime.Types.SharpTSNetServer, SharpTS")
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.SharpTSNetServer, SharpTS");
        il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetType", [_types.String])!);

        var typeLocal = il.DeclareLocal(typeof(Type));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Check if type was found
        var typeFoundLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Brtrue, typeFoundLabel);

        // Type not found (standalone mode without SharpTS) - return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(typeFoundLabel);

        // Activator.CreateInstance(type, new object[] { callback })
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Call, typeof(Activator).GetMethod("CreateInstance", [typeof(Type), typeof(object[])])!);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object NetCreateConnection(object? options, object? callback)
    /// Creates a socket and connects via reflection.
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

        var il = method.GetILGenerator();

        // Use reflection to create SharpTSSocket
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.SharpTSSocket, SharpTS");
        il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetType", [_types.String])!);

        var typeLocal = il.DeclareLocal(typeof(Type));
        il.Emit(OpCodes.Stloc, typeLocal);

        var typeFoundLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Brtrue, typeFoundLabel);

        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(typeFoundLabel);

        // Create socket instance (parameterless ctor)
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Call, typeof(Activator).GetMethod("CreateInstance", [typeof(Type)])!);
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
