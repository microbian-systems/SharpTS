using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SharpTS.Compilation;

/// <summary>
/// Emits the TLS server accept loop infrastructure:
/// - $TlsAcceptClosure: Carries TcpClient + handshake results, dispatches 'secureConnection' on EventLoop
/// - _TlsAcceptWorker body: Blocking accept loop with SslStream handshake
/// </summary>
public partial class RuntimeEmitter
{
    private TypeBuilder _tlsAcceptClosureType = null!;
    private ConstructorBuilder _tlsAcceptClosureCtor = null!;
    private MethodBuilder _tlsAcceptClosureRun = null!;

    // TLS client connect closure
    private TypeBuilder _tlsConnectClosureType = null!;
    private ConstructorBuilder _tlsConnectClosureCtor = null!;
    private MethodBuilder _tlsConnectClosureConnect = null!;
    private MethodBuilder _tlsAcceptAllCertsMethod = null!;

    /// <summary>
    /// Emits TLS handshake helper methods on the $Runtime class.
    /// These use late-binding to call RuntimeTypes.TlsAcceptAndHandshake/TlsConnectAndHandshake.
    /// </summary>
    private void EmitTlsHandshakeHelpers(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.TlsAcceptAndHandshake = EmitReflectionHelper(typeBuilder, "TlsAcceptAndHandshake", 4);
        runtime.TlsConnectAndHandshake = EmitReflectionHelper(typeBuilder, "TlsConnectAndHandshake", 4);
    }

    /// <summary>
    /// Emits the $TlsAcceptClosure class.
    /// Fields: _server ($TlsServer), _socket ($TlsSocket)
    /// Run(): calls _server._callback.Invoke([_socket]), emits "secureConnection" event
    /// </summary>
    private void EmitTlsAcceptClosureClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$TlsAcceptClosure",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );
        _tlsAcceptClosureType = typeBuilder;

        var serverField = typeBuilder.DefineField("_server", _tlsServerTypeBuilder, FieldAttributes.Private);
        var socketField = typeBuilder.DefineField("_socket", _tlsSocketTypeBuilder, FieldAttributes.Private);

        // Constructor: (TlsServer server, TlsSocket socket)
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_tlsServerTypeBuilder, _tlsSocketTypeBuilder]
        );
        _tlsAcceptClosureCtor = ctor;

        var ctorIL = ctor.GetILGenerator();
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.Object));
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Stfld, serverField);
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_2);
        ctorIL.Emit(OpCodes.Stfld, socketField);
        ctorIL.Emit(OpCodes.Ret);

        // Run(): void
        var run = typeBuilder.DefineMethod(
            "Run",
            MethodAttributes.Public,
            typeof(void),
            Type.EmptyTypes
        );
        _tlsAcceptClosureRun = run;

        var il = run.GetILGenerator();

        // Call _server._callback.Invoke([_socket]) if callback exists
        var noCallback = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, serverField);
        il.Emit(OpCodes.Ldfld, _tlsServerCallbackField);
        il.Emit(OpCodes.Brfalse, noCallback);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, serverField);
        il.Emit(OpCodes.Ldfld, _tlsServerCallbackField);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, noCallback);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, serverField);
        il.Emit(OpCodes.Ldfld, _tlsServerCallbackField);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, socketField);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(noCallback);

        // Emit "secureConnection" event: _server.Emit("secureConnection", [_socket])
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, serverField);
        il.Emit(OpCodes.Ldstr, "secureConnection");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, socketField);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterEmit);
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ret);

        typeBuilder.CreateType();
    }

    /// <summary>
    /// Emits the _TlsAcceptWorker body on $TlsServer.
    /// Blocking loop: AcceptTcpClient → SslStream → AuthenticateAsServer → schedule closure.
    /// Uses RuntimeTypes.TlsAcceptAndHandshake helper to avoid complex IL for SslStream/cert handling.
    /// </summary>
    private void EmitTlsServerAcceptWorkerBody(EmittedRuntime runtime)
    {
        var il = _tlsServerAcceptWorkerMethod.GetILGenerator();

        var loopTop = il.DefineLabel();
        var loopExit = il.DefineLabel();

        il.MarkLabel(loopTop);

        // if (!_isListening || _listener == null) break
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerIsListeningField);
        il.Emit(OpCodes.Brfalse, loopExit);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerListenerField);
        il.Emit(OpCodes.Brfalse, loopExit);

        il.BeginExceptionBlock();

        // Call RuntimeTypes.TlsAcceptAndHandshake(_listener, _cert, _key, _alpn)
        // Returns: object[] { authorized(bool), encrypted(bool), alpnProtocol(string) }
        // or null on failure
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerListenerField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerCertField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerKeyField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerAlpnField);
        il.Emit(OpCodes.Call, runtime.TlsAcceptAndHandshake);

        // result == null → skip (handshake failed)
        // Cast from object to object[] (the reflection helper returns object)
        var resultLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Castclass, _types.ObjectArray);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Ldloc, resultLocal);
        var noResult = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, noResult);

        // Create $TlsSocket
        var tlsSocketLocal = il.DeclareLocal(_tlsSocketTypeBuilder);
        il.Emit(OpCodes.Newobj, runtime.TlsSocketCtor);
        il.Emit(OpCodes.Stloc, tlsSocketLocal);

        // socket._authorized = (bool)result[0]
        il.Emit(OpCodes.Ldloc, tlsSocketLocal);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Stfld, _tlsSocketAuthorizedField);

        // socket._encrypted = (bool)result[1]
        il.Emit(OpCodes.Ldloc, tlsSocketLocal);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Stfld, _tlsSocketEncryptedField);

        // socket._alpnProtocol = (string)result[2]
        il.Emit(OpCodes.Ldloc, tlsSocketLocal);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stfld, _tlsSocketAlpnProtocolField);

        // Schedule on EventLoop
        il.Emit(OpCodes.Call, runtime.EventLoopGetInstance);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, tlsSocketLocal);
        il.Emit(OpCodes.Newobj, _tlsAcceptClosureCtor);
        il.Emit(OpCodes.Ldftn, _tlsAcceptClosureRun);
        il.Emit(OpCodes.Newobj, typeof(Action).GetConstructor([_types.Object, typeof(IntPtr)])!);
        il.Emit(OpCodes.Call, runtime.EventLoopSchedule);

        il.MarkLabel(noResult);

        var afterAccept = il.DefineLabel();
        il.Emit(OpCodes.Leave, afterAccept);

        // catch { break }
        il.BeginCatchBlock(typeof(Exception));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Leave, loopExit);
        il.EndExceptionBlock();

        il.MarkLabel(afterAccept);
        il.Emit(OpCodes.Br, loopTop);

        il.MarkLabel(loopExit);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits $TlsConnectClosure: calls TlsConnectAndHandshake via late-binding on ThreadPool,
    /// then populates socket and invokes callback.
    /// Fields: _socket ($TlsSocket), _port (boxed double), _host, _reject (boxed bool), _alpn, _callback
    /// </summary>
    private void EmitTlsConnectClosureClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$TlsConnectClosure",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );
        _tlsConnectClosureType = typeBuilder;

        var socketField = typeBuilder.DefineField("_socket", _tlsSocketTypeBuilder, FieldAttributes.Private);
        var portField = typeBuilder.DefineField("_port", _types.Int32, FieldAttributes.Private);
        var hostField = typeBuilder.DefineField("_host", _types.String, FieldAttributes.Private);
        var rejectField = typeBuilder.DefineField("_reject", _types.Boolean, FieldAttributes.Private);
        var alpnField = typeBuilder.DefineField("_alpn", typeof(string[]), FieldAttributes.Private);
        var callbackField = typeBuilder.DefineField("_callback", _types.Object, FieldAttributes.Private);

        // Constructor
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_tlsSocketTypeBuilder, _types.Int32, _types.String, _types.Boolean, typeof(string[]), _types.Object]
        );
        _tlsConnectClosureCtor = ctor;

        var ctorIL = ctor.GetILGenerator();
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.Object));
        ctorIL.Emit(OpCodes.Ldarg_0); ctorIL.Emit(OpCodes.Ldarg_1); ctorIL.Emit(OpCodes.Stfld, socketField);
        ctorIL.Emit(OpCodes.Ldarg_0); ctorIL.Emit(OpCodes.Ldarg_2); ctorIL.Emit(OpCodes.Stfld, portField);
        ctorIL.Emit(OpCodes.Ldarg_0); ctorIL.Emit(OpCodes.Ldarg_3); ctorIL.Emit(OpCodes.Stfld, hostField);
        ctorIL.Emit(OpCodes.Ldarg_0); ctorIL.Emit(OpCodes.Ldarg, 4); ctorIL.Emit(OpCodes.Stfld, rejectField);
        ctorIL.Emit(OpCodes.Ldarg_0); ctorIL.Emit(OpCodes.Ldarg, 5); ctorIL.Emit(OpCodes.Stfld, alpnField);
        ctorIL.Emit(OpCodes.Ldarg_0); ctorIL.Emit(OpCodes.Ldarg, 6); ctorIL.Emit(OpCodes.Stfld, callbackField);
        ctorIL.Emit(OpCodes.Ret);

        // Connect(object state): void — runs on ThreadPool
        // Calls TlsConnectAndHandshake(port, host, reject, alpn) via $Runtime helper,
        // populates socket properties, invokes callback.
        var connect = typeBuilder.DefineMethod(
            "Connect",
            MethodAttributes.Public,
            typeof(void),
            [_types.Object]
        );
        _tlsConnectClosureConnect = connect;

        var il = connect.GetILGenerator();

        il.BeginExceptionBlock();

        // Call $Runtime.TlsConnectAndHandshake(port, host, reject, alpn)
        // All args must be object for the reflection helper
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, portField);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, hostField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, rejectField);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, alpnField);
        il.Emit(OpCodes.Call, runtime.TlsConnectAndHandshake);

        // result == null → handshake failed
        var resultLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Castclass, _types.ObjectArray);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Ldloc, resultLocal);
        var noResult = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, noResult);

        // Populate socket properties from result
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, socketField);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Stfld, _tlsSocketAuthorizedField);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, socketField);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Stfld, _tlsSocketEncryptedField);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, socketField);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stfld, _tlsSocketAlpnProtocolField);

        // EventLoop.Unref() (we ref'd in TlsConnect)
        il.Emit(OpCodes.Call, runtime.EventLoopGetInstance);
        il.Emit(OpCodes.Call, runtime.EventLoopUnref);

        // Invoke callback if present
        var noCallbackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, callbackField);
        il.Emit(OpCodes.Brfalse, noCallbackLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, callbackField);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, noCallbackLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, callbackField);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);
        il.MarkLabel(noCallbackLabel);

        var afterOk = il.DefineLabel();
        il.Emit(OpCodes.Br, afterOk);

        il.MarkLabel(noResult);
        // Handshake failed: EventLoop.Unref()
        il.Emit(OpCodes.Call, runtime.EventLoopGetInstance);
        il.Emit(OpCodes.Call, runtime.EventLoopUnref);

        il.MarkLabel(afterOk);
        var afterConnect = il.DefineLabel();
        il.Emit(OpCodes.Leave, afterConnect);

        // catch (Exception) { EventLoop.Unref(); }
        il.BeginCatchBlock(typeof(Exception));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Call, runtime.EventLoopGetInstance);
        il.Emit(OpCodes.Call, runtime.EventLoopUnref);
        il.Emit(OpCodes.Leave, afterConnect);
        il.EndExceptionBlock();

        il.MarkLabel(afterConnect);
        il.Emit(OpCodes.Ret);

        typeBuilder.CreateType();
    }

    /// <summary>
    /// Finalizes the $TlsServer type after the accept worker body is emitted.
    /// </summary>
    private void EmitTlsServerFinalize()
    {
        _tlsServerTypeBuilder.CreateType();
    }
}
