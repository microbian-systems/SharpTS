using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits the $TlsSocket and $TlsServer classes in pure IL for the TLS module.
/// These replace the reflection-based SharpTSTlsSocket/SharpTSTlsServer creation,
/// eliminating the SharpTS.dll dependency for standalone compiled DLLs.
/// </summary>
/// <remarks>
/// NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSTlsSocket
/// and SharpTS.Runtime.Types.SharpTSTlsServer
/// </remarks>
public partial class RuntimeEmitter
{
    // ---- $TlsSocket field builders ----
    private TypeBuilder _tlsSocketTypeBuilder = null!;
    private FieldBuilder _tlsSocketAuthorizedField = null!;
    private FieldBuilder _tlsSocketEncryptedField = null!;
    private FieldBuilder _tlsSocketAlpnProtocolField = null!;
    private FieldBuilder _tlsSocketServernameField = null!;

    // ---- $TlsServer field builders ----
    private TypeBuilder _tlsServerTypeBuilder = null!;
    private FieldBuilder _tlsServerIsListeningField = null!;
    private FieldBuilder _tlsServerCallbackField = null!;
    private FieldBuilder _tlsServerCertField = null!;       // string (PEM cert)
    private FieldBuilder _tlsServerKeyField = null!;        // string (PEM key)
    private FieldBuilder _tlsServerListenerField = null!;   // TcpListener
    private FieldBuilder _tlsServerPortField = null!;       // int
    private FieldBuilder _tlsServerAlpnField = null!;       // string[] (ALPN protocol names)
    private MethodBuilder _tlsServerAcceptWorkerMethod = null!;

    /// <summary>
    /// Emits all TLS types ($TlsSocket and $TlsServer) for standalone operation.
    /// Must be called after $EventEmitter is defined.
    /// </summary>
    private void EmitTlsTypes(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        EmitTlsSocketClass(moduleBuilder, runtime);
        EmitTlsServerClass(moduleBuilder, runtime);
    }

    // ========================================================================
    // $TlsSocket - extends $EventEmitter
    // ========================================================================

    /// <summary>
    /// Emits: public class $TlsSocket : $EventEmitter
    /// A standalone TLS socket with properties and stub methods.
    /// </summary>
    private void EmitTlsSocketClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$TlsSocket",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            runtime.TSEventEmitterType
        );
        _tlsSocketTypeBuilder = typeBuilder;
        runtime.TlsSocketType = typeBuilder;

        // Fields
        _tlsSocketAuthorizedField = typeBuilder.DefineField("_authorized", _types.Boolean, FieldAttributes.Assembly);
        _tlsSocketEncryptedField = typeBuilder.DefineField("_encrypted", _types.Boolean, FieldAttributes.Assembly);
        _tlsSocketAlpnProtocolField = typeBuilder.DefineField("_alpnProtocol", _types.Object, FieldAttributes.Assembly);
        _tlsSocketServernameField = typeBuilder.DefineField("_servername", _types.Object, FieldAttributes.Assembly);

        // Constructor: public $TlsSocket() - calls base $EventEmitter()
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes
        );
        runtime.TlsSocketCtor = ctor;

        var ctorIL = ctor.GetILGenerator();
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Call, runtime.TSEventEmitterCtor);
        // _authorized = false (default)
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldc_I4_0);
        ctorIL.Emit(OpCodes.Stfld, _tlsSocketAuthorizedField);
        // _encrypted = false (default)
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldc_I4_0);
        ctorIL.Emit(OpCodes.Stfld, _tlsSocketEncryptedField);
        // _alpnProtocol = null (returns undefined via property getter)
        // _servername = null (returns undefined via property getter)
        ctorIL.Emit(OpCodes.Ret);

        // Properties (found by GetFieldsProperty PascalCase property lookup)
        EmitTlsSocketProperties(typeBuilder, runtime);

        // Methods (found by GetFieldsProperty case-insensitive method lookup)
        EmitTlsSocketGetCipher(typeBuilder);
        EmitTlsSocketGetPeerCertificate(typeBuilder);
        EmitTlsSocketGetProtocol(typeBuilder);
        EmitTlsSocketWrite(typeBuilder);
        EmitTlsSocketEnd(typeBuilder);
        EmitTlsSocketDestroy(typeBuilder, runtime);
        EmitTlsSocketSetEncoding(typeBuilder);
        EmitTlsSocketGetMember(typeBuilder, runtime);

        typeBuilder.CreateType();
    }

    /// <summary>
    /// Emits properties on $TlsSocket:
    /// - Authorized (bool) - whether the TLS connection is authorized
    /// - Encrypted (bool) - whether the socket is encrypted
    /// - AlpnProtocol (object) - ALPN protocol or undefined
    /// - Servername (object) - server name or undefined
    /// </summary>
    private void EmitTlsSocketProperties(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Authorized property (bool)
        var authorizedProp = typeBuilder.DefineProperty("Authorized", PropertyAttributes.None, _types.Boolean, null);
        var getAuthorized = typeBuilder.DefineMethod(
            "get_Authorized",
            MethodAttributes.Public | MethodAttributes.SpecialName,
            _types.Boolean,
            Type.EmptyTypes
        );
        var il = getAuthorized.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsSocketAuthorizedField);
        il.Emit(OpCodes.Ret);
        authorizedProp.SetGetMethod(getAuthorized);

        // Encrypted property (bool)
        var encryptedProp = typeBuilder.DefineProperty("Encrypted", PropertyAttributes.None, _types.Boolean, null);
        var getEncrypted = typeBuilder.DefineMethod(
            "get_Encrypted",
            MethodAttributes.Public | MethodAttributes.SpecialName,
            _types.Boolean,
            Type.EmptyTypes
        );
        il = getEncrypted.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsSocketEncryptedField);
        il.Emit(OpCodes.Ret);
        encryptedProp.SetGetMethod(getEncrypted);

        // AlpnProtocol property (object) - returns field value or $Undefined._instance
        var alpnProp = typeBuilder.DefineProperty("AlpnProtocol", PropertyAttributes.None, _types.Object, null);
        var getAlpn = typeBuilder.DefineMethod(
            "get_AlpnProtocol",
            MethodAttributes.Public | MethodAttributes.SpecialName,
            _types.Object,
            Type.EmptyTypes
        );
        il = getAlpn.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsSocketAlpnProtocolField);
        il.Emit(OpCodes.Dup);
        var hasAlpnLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, hasAlpnLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.MarkLabel(hasAlpnLabel);
        il.Emit(OpCodes.Ret);
        alpnProp.SetGetMethod(getAlpn);

        // Servername property (object) - returns field value or $Undefined._instance
        var servernameProp = typeBuilder.DefineProperty("Servername", PropertyAttributes.None, _types.Object, null);
        var getServername = typeBuilder.DefineMethod(
            "get_Servername",
            MethodAttributes.Public | MethodAttributes.SpecialName,
            _types.Object,
            Type.EmptyTypes
        );
        il = getServername.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsSocketServernameField);
        il.Emit(OpCodes.Dup);
        var hasServernameLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, hasServernameLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.MarkLabel(hasServernameLabel);
        il.Emit(OpCodes.Ret);
        servernameProp.SetGetMethod(getServername);
    }

    /// <summary>
    /// Emits: public object? GetCipher()
    /// Returns null in standalone mode (no SslStream available).
    /// </summary>
    private void EmitTlsSocketGetCipher(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod(
            "GetCipher",
            MethodAttributes.Public,
            _types.Object,
            Type.EmptyTypes
        );
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object? GetPeerCertificate()
    /// Returns a new empty Dictionary (no peer certificate in standalone mode).
    /// </summary>
    private void EmitTlsSocketGetPeerCertificate(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod(
            "GetPeerCertificate",
            MethodAttributes.Public,
            _types.Object,
            Type.EmptyTypes
        );
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object? GetProtocol()
    /// Returns null in standalone mode (no SslStream available).
    /// </summary>
    private void EmitTlsSocketGetProtocol(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod(
            "GetProtocol",
            MethodAttributes.Public,
            _types.Object,
            Type.EmptyTypes
        );
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object? Write(object data)
    /// Stub - returns null in standalone mode.
    /// </summary>
    private void EmitTlsSocketWrite(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod(
            "Write",
            MethodAttributes.Public,
            _types.Object,
            [_types.Object]
        );
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object? End()
    /// Stub - returns this for chaining.
    /// </summary>
    private void EmitTlsSocketEnd(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod(
            "End",
            MethodAttributes.Public,
            _types.Object,
            Type.EmptyTypes
        );
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object? Destroy()
    /// Emits 'close' event and returns this.
    /// </summary>
    private void EmitTlsSocketDestroy(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Destroy",
            MethodAttributes.Public,
            _types.Object,
            Type.EmptyTypes
        );
        var il = method.GetILGenerator();

        // Emit 'close' event
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "close");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterEmit);
        il.Emit(OpCodes.Pop);

        // return this
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object? SetEncoding(object encoding)
    /// Stub - returns this for chaining.
    /// </summary>
    private void EmitTlsSocketSetEncoding(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod(
            "SetEncoding",
            MethodAttributes.Public,
            _types.Object,
            [_types.Object]
        );
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object? GetMember(string name)
    /// Handles property lookups that can't be resolved via PascalCase reflection
    /// (e.g., "authorized" returns boxed bool, "encrypted" returns boxed bool,
    /// "alpnProtocol" returns value or undefined, "servername" returns value or undefined).
    /// This is used as a fallback by GetFieldsProperty.
    /// </summary>
    private void EmitTlsSocketGetMember(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetMember",
            MethodAttributes.Public,
            _types.Object,
            [_types.String]
        );

        var il = method.GetILGenerator();

        var authorizedLabel = il.DefineLabel();
        var encryptedLabel = il.DefineLabel();
        var alpnLabel = il.DefineLabel();
        var servernameLabel = il.DefineLabel();
        var defaultLabel = il.DefineLabel();

        // Check "authorized"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "authorized");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Equals", [_types.String])!);
        il.Emit(OpCodes.Brtrue, authorizedLabel);

        // Check "encrypted"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "encrypted");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Equals", [_types.String])!);
        il.Emit(OpCodes.Brtrue, encryptedLabel);

        // Check "alpnProtocol"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "alpnProtocol");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Equals", [_types.String])!);
        il.Emit(OpCodes.Brtrue, alpnLabel);

        // Check "servername"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "servername");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Equals", [_types.String])!);
        il.Emit(OpCodes.Brtrue, servernameLabel);

        il.Emit(OpCodes.Br, defaultLabel);

        // "authorized" -> box _authorized bool
        il.MarkLabel(authorizedLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsSocketAuthorizedField);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        // "encrypted" -> box _encrypted bool
        il.MarkLabel(encryptedLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsSocketEncryptedField);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        // "alpnProtocol" -> _alpnProtocol ?? undefined
        il.MarkLabel(alpnLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsSocketAlpnProtocolField);
        il.Emit(OpCodes.Dup);
        var hasAlpn = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, hasAlpn);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.MarkLabel(hasAlpn);
        il.Emit(OpCodes.Ret);

        // "servername" -> _servername ?? undefined
        il.MarkLabel(servernameLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsSocketServernameField);
        il.Emit(OpCodes.Dup);
        var hasSn = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, hasSn);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.MarkLabel(hasSn);
        il.Emit(OpCodes.Ret);

        // default -> return null (lets base EventEmitter methods resolve via reflection)
        il.MarkLabel(defaultLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    // ========================================================================
    // $TlsServer - extends $EventEmitter
    // ========================================================================

    /// <summary>
    /// Emits: public class $TlsServer : $EventEmitter
    /// A standalone TLS server with listening state and stub methods.
    /// </summary>
    private void EmitTlsServerClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$TlsServer",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            runtime.TSEventEmitterType
        );
        _tlsServerTypeBuilder = typeBuilder;
        runtime.TlsServerType = typeBuilder;

        // Fields
        _tlsServerIsListeningField = typeBuilder.DefineField("_isListening", _types.Boolean, FieldAttributes.Private);
        _tlsServerCallbackField = typeBuilder.DefineField("_callback", _types.Object, FieldAttributes.Assembly);
        _tlsServerCertField = typeBuilder.DefineField("_cert", _types.String, FieldAttributes.Private);
        _tlsServerKeyField = typeBuilder.DefineField("_key", _types.String, FieldAttributes.Private);
        _tlsServerListenerField = typeBuilder.DefineField("_listener", typeof(System.Net.Sockets.TcpListener), FieldAttributes.Private);
        _tlsServerPortField = typeBuilder.DefineField("_port", _types.Int32, FieldAttributes.Private);
        _tlsServerAlpnField = typeBuilder.DefineField("_alpn", typeof(string[]), FieldAttributes.Private);

        // Define accept worker stub (body emitted later — needs closure type)
        _tlsServerAcceptWorkerMethod = typeBuilder.DefineMethod(
            "_TlsAcceptWorker",
            MethodAttributes.Private,
            typeof(void),
            [_types.Object]
        );

        // Constructor: public $TlsServer(object? options, object? callback)
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.Object, _types.Object]
        );
        runtime.TlsServerCtor = ctor;

        var ctorIL = ctor.GetILGenerator();
        // Call base $EventEmitter()
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Call, runtime.TSEventEmitterCtor);
        // _isListening = false (default)
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldc_I4_0);
        ctorIL.Emit(OpCodes.Stfld, _tlsServerIsListeningField);
        // _callback = callback (arg2)
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_2);
        ctorIL.Emit(OpCodes.Stfld, _tlsServerCallbackField);

        // Parse options (arg1) for cert, key, ALPNProtocols
        var skipOptions = ctorIL.DefineLabel();
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Brfalse, skipOptions);

        // cert = options["cert"] as string
        var dictType = _types.DictionaryStringObject;
        var dictTryGet = dictType.GetMethod("TryGetValue")!;
        var tempLocal = ctorIL.DeclareLocal(_types.Object);

        // Check if options is Dictionary<string, object?>
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Isinst, dictType);
        ctorIL.Emit(OpCodes.Brfalse, skipOptions);

        var optLocal = ctorIL.DeclareLocal(dictType);
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Castclass, dictType);
        ctorIL.Emit(OpCodes.Stloc, optLocal);

        // Extract "cert"
        var noCert = ctorIL.DefineLabel();
        ctorIL.Emit(OpCodes.Ldloc, optLocal);
        ctorIL.Emit(OpCodes.Ldstr, "cert");
        ctorIL.Emit(OpCodes.Ldloca, tempLocal);
        ctorIL.Emit(OpCodes.Callvirt, dictTryGet);
        ctorIL.Emit(OpCodes.Brfalse, noCert);
        ctorIL.Emit(OpCodes.Ldloc, tempLocal);
        ctorIL.Emit(OpCodes.Isinst, _types.String);
        ctorIL.Emit(OpCodes.Brfalse, noCert);
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldloc, tempLocal);
        ctorIL.Emit(OpCodes.Castclass, _types.String);
        ctorIL.Emit(OpCodes.Stfld, _tlsServerCertField);
        ctorIL.MarkLabel(noCert);

        // Extract "key"
        var noKey = ctorIL.DefineLabel();
        ctorIL.Emit(OpCodes.Ldloc, optLocal);
        ctorIL.Emit(OpCodes.Ldstr, "key");
        ctorIL.Emit(OpCodes.Ldloca, tempLocal);
        ctorIL.Emit(OpCodes.Callvirt, dictTryGet);
        ctorIL.Emit(OpCodes.Brfalse, noKey);
        ctorIL.Emit(OpCodes.Ldloc, tempLocal);
        ctorIL.Emit(OpCodes.Isinst, _types.String);
        ctorIL.Emit(OpCodes.Brfalse, noKey);
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldloc, tempLocal);
        ctorIL.Emit(OpCodes.Castclass, _types.String);
        ctorIL.Emit(OpCodes.Stfld, _tlsServerKeyField);
        ctorIL.MarkLabel(noKey);

        // Extract "ALPNProtocols" → convert List<object?> to string[]
        var noAlpn = ctorIL.DefineLabel();
        ctorIL.Emit(OpCodes.Ldloc, optLocal);
        ctorIL.Emit(OpCodes.Ldstr, "ALPNProtocols");
        ctorIL.Emit(OpCodes.Ldloca, tempLocal);
        ctorIL.Emit(OpCodes.Callvirt, dictTryGet);
        ctorIL.Emit(OpCodes.Brfalse, noAlpn);
        ctorIL.Emit(OpCodes.Ldloc, tempLocal);
        ctorIL.Emit(OpCodes.Isinst, _types.ListOfObject);
        ctorIL.Emit(OpCodes.Brfalse, noAlpn);
        // Convert List<object?> to string[] using LINQ
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldloc, tempLocal);
        ctorIL.Emit(OpCodes.Castclass, _types.ListOfObject);
        // Call Enumerable.OfType<string>().ToArray()
        var ofTypeMethod = typeof(System.Linq.Enumerable).GetMethod("OfType")!.MakeGenericMethod(_types.String);
        var toArrayMethod = typeof(System.Linq.Enumerable).GetMethod("ToArray")!.MakeGenericMethod(_types.String);
        ctorIL.Emit(OpCodes.Call, ofTypeMethod);
        ctorIL.Emit(OpCodes.Call, toArrayMethod);
        ctorIL.Emit(OpCodes.Stfld, _tlsServerAlpnField);
        ctorIL.MarkLabel(noAlpn);

        ctorIL.MarkLabel(skipOptions);
        ctorIL.Emit(OpCodes.Ret);

        // Properties
        EmitTlsServerProperties(typeBuilder);

        // Methods
        EmitTlsServerListen(typeBuilder, runtime);
        EmitTlsServerClose(typeBuilder, runtime);
        EmitTlsServerAddress(typeBuilder, runtime);
        EmitTlsServerGetMember(typeBuilder, runtime);

        // NOTE: CreateType() deferred to EmitTlsServerFinalize (needs accept worker body)
    }

    /// <summary>
    /// Emits the Listening property on $TlsServer.
    /// </summary>
    private void EmitTlsServerProperties(TypeBuilder typeBuilder)
    {
        // Listening property (bool)
        var listeningProp = typeBuilder.DefineProperty("Listening", PropertyAttributes.None, _types.Boolean, null);
        var getListening = typeBuilder.DefineMethod(
            "get_Listening",
            MethodAttributes.Public | MethodAttributes.SpecialName,
            _types.Boolean,
            Type.EmptyTypes
        );
        var il = getListening.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerIsListeningField);
        il.Emit(OpCodes.Ret);
        listeningProp.SetGetMethod(getListening);
    }

    /// <summary>
    /// Emits: public object? Listen(object port, object host, object backlog, object callback)
    /// Sets up a real TcpListener, starts accept worker on ThreadPool.
    /// </summary>
    private void EmitTlsServerListen(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Listen",
            MethodAttributes.Public,
            _types.Object,
            [_types.Object, _types.Object, _types.Object, _types.Object]
        );

        var il = method.GetILGenerator();

        // Parse port from arg1 (double → int)
        var portLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, portLocal);
        var portParsed = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, portParsed);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, portLocal);
        il.MarkLabel(portParsed);

        // Parse host from arg2 (string), default "0.0.0.0"
        var hostLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldstr, "0.0.0.0");
        il.Emit(OpCodes.Stloc, hostLocal);
        var hostParsed = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, hostParsed);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Stloc, hostLocal);
        il.MarkLabel(hostParsed);

        // Find callback in args (scan from right)
        var callbackLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, callbackLocal);
        for (int argIdx = 4; argIdx >= 1; argIdx--)
        {
            var skipCb = il.DefineLabel();
            il.Emit(OpCodes.Ldarg, argIdx);
            il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
            il.Emit(OpCodes.Brfalse, skipCb);
            il.Emit(OpCodes.Ldarg, argIdx);
            il.Emit(OpCodes.Stloc, callbackLocal);
            il.MarkLabel(skipCb);
        }

        // Create TcpListener: new TcpListener(IPAddress.Parse(host), port)
        il.Emit(OpCodes.Ldloc, hostLocal);
        il.Emit(OpCodes.Call, typeof(System.Net.IPAddress).GetMethod("Parse", [_types.String])!);
        il.Emit(OpCodes.Ldloc, portLocal);
        il.Emit(OpCodes.Newobj, typeof(System.Net.Sockets.TcpListener).GetConstructor([typeof(System.Net.IPAddress), _types.Int32])!);
        var listenerLocal = il.DeclareLocal(typeof(System.Net.Sockets.TcpListener));
        il.Emit(OpCodes.Stloc, listenerLocal);

        // listener.Start()
        il.Emit(OpCodes.Ldloc, listenerLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Net.Sockets.TcpListener).GetMethod("Start", Type.EmptyTypes)!);

        // _listener = listener
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, listenerLocal);
        il.Emit(OpCodes.Stfld, _tlsServerListenerField);

        // If port was 0, get the actual port from the listener
        var portNotZero = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, portLocal);
        il.Emit(OpCodes.Brtrue, portNotZero);
        il.Emit(OpCodes.Ldloc, listenerLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Net.Sockets.TcpListener).GetProperty("LocalEndpoint")!.GetGetMethod()!);
        il.Emit(OpCodes.Castclass, typeof(System.Net.IPEndPoint));
        il.Emit(OpCodes.Callvirt, typeof(System.Net.IPEndPoint).GetProperty("Port")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, portLocal);
        il.MarkLabel(portNotZero);

        // _port = port
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, portLocal);
        il.Emit(OpCodes.Stfld, _tlsServerPortField);

        // _isListening = true
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tlsServerIsListeningField);

        // EventLoop.Ref()
        il.Emit(OpCodes.Call, runtime.EventLoopGetInstance);
        il.Emit(OpCodes.Call, runtime.EventLoopRef);

        // Call callback if provided
        var noCallback = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Brfalse, noCallback);
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);
        il.MarkLabel(noCallback);

        // Emit 'listening' event
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "listening");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterEmit);
        il.Emit(OpCodes.Pop);

        // Start accept loop: ThreadPool.QueueUserWorkItem(new WaitCallback(this._TlsAcceptWorker))
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldftn, _tlsServerAcceptWorkerMethod);
        il.Emit(OpCodes.Newobj, typeof(System.Threading.WaitCallback).GetConstructor([_types.Object, typeof(IntPtr)])!);
        il.Emit(OpCodes.Call, typeof(System.Threading.ThreadPool).GetMethod("QueueUserWorkItem", [typeof(System.Threading.WaitCallback)])!);
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object? Close(object? callback)
    /// Sets _isListening = false, calls callback, emits 'close' event.
    /// </summary>
    private void EmitTlsServerClose(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Close",
            MethodAttributes.Public,
            _types.Object,
            [_types.Object]
        );

        var il = method.GetILGenerator();

        // if (!_isListening) return this
        var isListeningLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerIsListeningField);
        il.Emit(OpCodes.Brtrue, isListeningLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(isListeningLabel);

        // _isListening = false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tlsServerIsListeningField);

        // Stop the TcpListener if it exists
        var noListener = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerListenerField);
        il.Emit(OpCodes.Brfalse, noListener);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerListenerField);
        il.Emit(OpCodes.Callvirt, typeof(System.Net.Sockets.TcpListener).GetMethod("Stop")!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stfld, _tlsServerListenerField);
        il.MarkLabel(noListener);

        // EventLoop.Unref()
        il.Emit(OpCodes.Call, runtime.EventLoopGetInstance);
        il.Emit(OpCodes.Call, runtime.EventLoopUnref);

        // Call callback (arg1) if provided
        var noCallbackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, noCallbackLabel);

        // Check TSFunction
        var notTSFunc = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, notTSFunc);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, noCallbackLabel);

        il.MarkLabel(notTSFunc);
        // Check BoundTSFunction
        var notBound = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brfalse, notBound);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, runtime.BoundTSFunctionInvoke);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(notBound);
        il.MarkLabel(noCallbackLabel);

        // Emit 'close' event
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "close");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, runtime.TSEventEmitterEmit);
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object? Address()
    /// Returns a dictionary with { address, family, port } from the bound listener.
    /// </summary>
    private void EmitTlsServerAddress(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Address",
            MethodAttributes.Public,
            _types.Object,
            Type.EmptyTypes
        );

        var il = method.GetILGenerator();

        // if (_listener == null) return null
        var hasListener = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerListenerField);
        il.Emit(OpCodes.Brtrue, hasListener);
        // Return dict with port from _port field
        var dictType = _types.DictionaryStringObject;
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(dictType));
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldstr, "port");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerPortField);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "set_Item", _types.String, _types.Object));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(hasListener);
        // Build { address, family, port } from listener.LocalEndpoint
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(dictType));
        var dictLocal = il.DeclareLocal(dictType);
        il.Emit(OpCodes.Stloc, dictLocal);

        var epLocal = il.DeclareLocal(typeof(System.Net.IPEndPoint));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerListenerField);
        il.Emit(OpCodes.Callvirt, typeof(System.Net.Sockets.TcpListener).GetProperty("LocalEndpoint")!.GetGetMethod()!);
        il.Emit(OpCodes.Castclass, typeof(System.Net.IPEndPoint));
        il.Emit(OpCodes.Stloc, epLocal);

        // ["address"] = ep.Address.ToString()
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "address");
        il.Emit(OpCodes.Ldloc, epLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Net.IPEndPoint).GetProperty("Address")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "set_Item", _types.String, _types.Object));

        // ["port"] = (double)ep.Port
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "port");
        il.Emit(OpCodes.Ldloc, epLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Net.IPEndPoint).GetProperty("Port")!.GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "set_Item", _types.String, _types.Object));

        // ["family"] = "IPv4"
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "family");
        il.Emit(OpCodes.Ldstr, "IPv4");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "set_Item", _types.String, _types.Object));

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object? GetMember(string name)
    /// Handles "listening" property lookup via GetMember fallback.
    /// </summary>
    private void EmitTlsServerGetMember(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetMember",
            MethodAttributes.Public,
            _types.Object,
            [_types.String]
        );

        var il = method.GetILGenerator();

        var listeningLabel = il.DefineLabel();
        var defaultLabel = il.DefineLabel();

        // Check "listening"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "listening");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Equals", [_types.String])!);
        il.Emit(OpCodes.Brtrue, listeningLabel);

        il.Emit(OpCodes.Br, defaultLabel);

        // "listening" -> box _isListening bool
        il.MarkLabel(listeningLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tlsServerIsListeningField);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        // default -> return null (lets base EventEmitter methods resolve via reflection)
        il.MarkLabel(defaultLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Helper: emits callback invocation for TSFunction/BoundTSFunction.
    /// Loads the callback from the specified argument, checks if it's TSFunction or BoundTSFunction,
    /// and invokes with empty args array.
    /// </summary>
    private void EmitCallbackInvoke(ILGenerator il, EmittedRuntime runtime, OpCode loadOpCode, byte argIndex)
    {
        var noCallbackLabel = il.DefineLabel();
        il.Emit(loadOpCode, argIndex);
        il.Emit(OpCodes.Brfalse, noCallbackLabel);

        // Check TSFunction
        var notTSFunc = il.DefineLabel();
        il.Emit(loadOpCode, argIndex);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, notTSFunc);
        il.Emit(loadOpCode, argIndex);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, noCallbackLabel);

        il.MarkLabel(notTSFunc);
        // Check BoundTSFunction
        var notBound = il.DefineLabel();
        il.Emit(loadOpCode, argIndex);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brfalse, notBound);
        il.Emit(loadOpCode, argIndex);
        il.Emit(OpCodes.Castclass, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, runtime.BoundTSFunctionInvoke);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(notBound);
        il.MarkLabel(noCallbackLabel);
    }
}
