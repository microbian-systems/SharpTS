using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// TLS module support for compiled TypeScript: tls.createServer, tls.connect, etc.
/// Uses emitted $TlsSocket and $TlsServer types for standalone DLL support (no SharpTS.dll dependency).
/// </summary>
public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits all tls module methods.
    /// </summary>
    private void EmitTlsModuleMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitTlsCreateServer(typeBuilder, runtime);
        EmitTlsConnect(typeBuilder, runtime);
        EmitTlsCreateSocket(typeBuilder, runtime);
        EmitTlsCreateSecureContext(typeBuilder, runtime);
        EmitTlsGetDefaultMinVersion(typeBuilder, runtime);
        EmitTlsGetDefaultMaxVersion(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits: public static object TlsCreateServer(object? options, object? callback)
    /// Creates a new $TlsServer instance (pure IL, no SharpTS.dll dependency).
    /// </summary>
    private void EmitTlsCreateServer(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "TlsCreateServer",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.TlsCreateServer = method;

        var il = method.GetILGenerator();

        // new $TlsServer(options, callback)
        il.Emit(OpCodes.Ldarg_0); // options
        il.Emit(OpCodes.Ldarg_1); // callback
        il.Emit(OpCodes.Newobj, runtime.TlsServerCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object TlsConnect(object? portOrOptions, object? hostOrCallback, object? optionsOrNull, object? callbackOrNull)
    /// Creates a new $TlsSocket instance (pure IL, no SharpTS.dll dependency).
    /// In standalone mode, the socket is created but not actually connected.
    /// </summary>
    private void EmitTlsConnect(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "TlsConnect",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object, _types.Object]
        );
        runtime.TlsConnect = method;

        var il = method.GetILGenerator();

        // new $TlsSocket()
        il.Emit(OpCodes.Newobj, runtime.TlsSocketCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object TlsCreateSocket()
    /// Creates a new $TlsSocket instance directly (used by tls.TLSSocket() constructor call).
    /// </summary>
    private void EmitTlsCreateSocket(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "TlsCreateSocket",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes
        );
        runtime.TlsCreateSocket = method;

        var il = method.GetILGenerator();

        // new $TlsSocket()
        il.Emit(OpCodes.Newobj, runtime.TlsSocketCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object TlsCreateSecureContext(object? options)
    /// Returns a new empty Dictionary (standalone mode doesn't need real secure context).
    /// </summary>
    private void EmitTlsCreateSecureContext(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "TlsCreateSecureContext",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.TlsCreateSecureContext = method;

        var il = method.GetILGenerator();

        // Return a new Dictionary<string,object?> to indicate success
        il.Emit(OpCodes.Newobj, typeof(Dictionary<string, object?>).GetConstructor([])!);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object TlsGetDefaultMinVersion()
    /// Returns "TLSv1.2" - no reflection needed.
    /// </summary>
    private void EmitTlsGetDefaultMinVersion(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "TlsGetDefaultMinVersion",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            []
        );
        runtime.TlsGetDefaultMinVersion = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldstr, "TLSv1.2");
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object TlsGetDefaultMaxVersion()
    /// Returns "TLSv1.3" - no reflection needed.
    /// </summary>
    private void EmitTlsGetDefaultMaxVersion(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "TlsGetDefaultMaxVersion",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            []
        );
        runtime.TlsGetDefaultMaxVersion = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldstr, "TLSv1.3");
        il.Emit(OpCodes.Ret);
    }
}
