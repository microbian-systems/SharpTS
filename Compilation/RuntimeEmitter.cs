using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits the runtime support types into the generated assembly.
/// This makes compiled DLLs standalone without requiring SharpTS.dll.
/// </summary>
public partial class RuntimeEmitter
{
    private readonly TypeProvider _types;

    /// <summary>
    /// Feature gating set — populated by <see cref="EmitAll(ModuleBuilder, RuntimeFeatureSet)"/>
    /// and consulted by individual <c>Emit*</c> methods to skip emission of helper types
    /// (and any <c>$Runtime</c> methods that depend on those helper types) the program
    /// doesn't need. Defaults to "emit everything" when an older overload is used.
    /// </summary>
    private RuntimeFeatureSet _features = RuntimeFeatureSet.EmitEverything();

    public RuntimeEmitter(TypeProvider types)
    {
        _types = types;
    }

    /// <summary>
    /// Backward-compatible overload: emit every helper type unconditionally.
    /// New callers should pass a <see cref="RuntimeFeatureSet"/> derived from
    /// <see cref="RuntimeFeatureDetector"/> so unused machinery can be skipped.
    /// </summary>
    public EmittedRuntime EmitAll(ModuleBuilder moduleBuilder)
        => EmitAll(moduleBuilder, RuntimeFeatureSet.EmitEverything());

    public EmittedRuntime EmitAll(ModuleBuilder moduleBuilder, RuntimeFeatureSet features)
    {
        _features = features;
        var runtime = new EmittedRuntime();

        // Emit $Undefined singleton class first (other methods need this type)
        EmitUndefinedClass(moduleBuilder, runtime);

        // Emit IUnionType marker interface first (union types need to implement this)
        EmitIUnionTypeInterface(moduleBuilder, runtime);

        // Emit a tiny dedicated type holding the thread-static `_currentArguments` slot
        // that $TSFunction.Invoke publishes so JS `arguments` capture can see caller
        // values beyond declared arity. Lives on its own type — adding it to
        // $TSFunction regressed Intl's formatRangeToParts test in opaque ways tied to
        // that type's field layout; isolating keeps $TSFunction's layout unchanged.
        EmitArgumentsContextClass(moduleBuilder, runtime);

        // Emit TSFunction class first (other methods depend on it)
        EmitTSFunctionClass(moduleBuilder, runtime);

        // Emit TSNamespace class for namespace support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSNamespace
        EmitTSNamespaceClass(moduleBuilder, runtime);

        // Emit TSSymbol class for symbol support
        EmitTSSymbolClass(moduleBuilder, runtime);

        // Emit ReferenceEqualityComparer for Map/Set key equality
        EmitReferenceEqualityComparerClass(moduleBuilder, runtime);

        // Emit $IGenerator interface for generator return/throw support
        EmitGeneratorInterface(moduleBuilder, runtime);

        // Emit $IAsyncGenerator interface for async generator return/throw support
        EmitAsyncGeneratorInterface(moduleBuilder, runtime);

        // NOTE: $IteratorWrapper is emitted later, after iterator methods are defined

        // Emit $TSDate class for standalone Date support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSDate
        EmitTSDateClass(moduleBuilder, runtime);

        // Emit $Error class hierarchy for standalone error support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSError and subclasses
        EmitTSErrorClasses(moduleBuilder, runtime);

        // Emit $Promise class for standalone Promise support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSPromise
        EmitTSPromiseClass(moduleBuilder, runtime);

        // Emit $ArrayHole singleton first — $Array methods reference
        // $ArrayHole.Instance for padding intermediate positions on sparse writes
        // and `a.length = N` extensions.
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.ArrayHole
        EmitArrayHoleClass(moduleBuilder, runtime);

        // Per-thread args[] pool used by method-call dispatch to skip
        // newarr per `obj.method(a, b)` invocation. Lives on a separate
        // class because $Runtime can't host more than one [ThreadStatic]
        // field without tripping a .NET 10 tier-0 QuickJit miscompilation.
        EmitCallArgsPool(moduleBuilder, runtime);

        // Emit $Array class for standalone array support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSArray
        EmitTSArrayClass(moduleBuilder, runtime);

        // Emit $IHasFields interface for unified property access
        // Must come before $Object which implements it
        EmitHasFieldsInterface(moduleBuilder, runtime);

        // Emit $Object class for standalone object support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSObject
        EmitTSObjectClass(moduleBuilder, runtime);

        // Emit $PropertyDescriptorStore and $CompiledPropertyDescriptor early
        // so types that build property descriptors during their own emission
        // ($RegExp.Exec attaches `index`/`input`/`groups` to its Array result
        // via PDS so the result remains a List<object?> for `instanceof Array`)
        // can reference CompiledPropertyDescriptorType / PDSDefineProperty.
        EmitPropertyDescriptorTypes(moduleBuilder, runtime);

        // Emit $RegExp class for standalone regex support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSRegExp
        EmitTSRegExpClass(moduleBuilder, runtime);

        // AssertionError now lives in stdlib/node/assert.ts (embedded stdlib migration).
        // Emit $NodeError class for standalone fs module support
        // NOTE: Must stay in sync with NodeError in Runtime/BuiltIns/Modules/NodeError.cs
        EmitNodeErrorClass(moduleBuilder, runtime);

        // Emit $Buffer class for standalone buffer support
        // NOTE: Must come before $Hash and $Hmac since they return Buffer
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSBuffer
        EmitTSBufferClass(moduleBuilder, runtime);

        // Crypto helper types — always emit. EmitGlobalThisMethods inside
        // $Runtime references the crypto namespace getter unconditionally;
        // gating these would require additional dispatch-site work. Phase 2.
        EmitTSHashClass(moduleBuilder, runtime);
        EmitTSHmacClass(moduleBuilder, runtime);
        EmitTSCipherClass(moduleBuilder, runtime);
        EmitTSDecipherClass(moduleBuilder, runtime);
        EmitTSSignTypeDefinition(moduleBuilder, runtime);
        EmitTSVerifyTypeDefinition(moduleBuilder, runtime);
        EmitTSKeyObjectClass(moduleBuilder, runtime);
        EmitTSECDHTypeDefinition(moduleBuilder, runtime);
        EmitBoundECDHMethodTypeDefinition(moduleBuilder, runtime);
        EmitTSDHTypeDefinition(moduleBuilder, runtime);
        EmitBoundDHMethodTypeDefinition(moduleBuilder, runtime);

        // Emit $EventLoop singleton (must come before timer types and net/http types that call Ref/Unref/Schedule)
        EmitTSEventLoopClass(moduleBuilder, runtime);

        // Emit $VirtualTimer class for virtual timer support (single-threaded semantics)
        // Must come after TSFunction (uses TSFunctionType)
        // Must come BEFORE TSTimeoutClass (TSTimeout references VirtualTimer)
        EmitVirtualTimerClass(moduleBuilder, runtime);

        // Emit $TSTimeout class for timer support
        // Must come after $EventLoop (Cancel/Ref/Unref call EventLoop.Ref/Unref)
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSTimeout
        EmitTSTimeoutClass(moduleBuilder, runtime);

        // Emit $TimeoutClosure class for setTimeout callback execution
        // Must come after TSFunction (uses TSFunctionType, TSFunctionInvoke)
        EmitTimeoutClosureClass(moduleBuilder, runtime);

        // Emit $IntervalClosure class for setInterval callback execution
        // Must come after TSFunction (uses TSFunctionType, TSFunctionInvoke)
        EmitIntervalClosureClass(moduleBuilder, runtime);

        // Emit $BoundTSFunction class for bound functions
        // Must come after TSFunction (uses TSFunctionType, TSFunctionInvokeWithThis)
        EmitBoundTSFunctionClass(moduleBuilder, runtime);

        // Emit $AsyncLocalStorage class for async context propagation
        // Must come after TSFunction (Run/Exit invoke callbacks via TSFunctionInvoke)
        if (features.UsesAsyncLocalStorage)
            EmitAsyncLocalStorageClass(moduleBuilder, runtime);

        // Emit $EventEmitter class for standalone event emitter support
        // NOTE: Must come after BoundTSFunction (uses TSFunctionType, BoundTSFunctionType)
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSEventEmitter
        EmitTSEventEmitterClass(moduleBuilder, runtime);

        // HTTP / TLS types — always emit. EmitGlobalThisMethods references the
        // fetch/Headers/Request/Response getters unconditionally; gating these
        // would require conditional dispatch emission. Phase 2.
        EmitHttpTypes(moduleBuilder, runtime);
        EmitTlsTypes(moduleBuilder, runtime);

        // Emit cluster types for standalone cluster support
        // NOTE: Must come after EventEmitter ($ClusterWorker and $ClusterManager extend it)
        if (features.UsesCluster)
            EmitClusterTypes(moduleBuilder, runtime);

        // Emit $FileDescriptorTable for standalone fs fd-based operations (Phase 21)
        // NOTE: Must come after $NodeError (uses NodeErrorCtor for EBADF errors)
        EmitFileDescriptorTableType(moduleBuilder, runtime);

        // Emit $Dirent and $Dir for standalone fs.opendirSync support (Phase 21)
        // NOTE: Must emit Dirent first since Dir's ReadSync creates Dirent instances
        EmitDirentType(moduleBuilder, runtime);
        EmitDirType(moduleBuilder, runtime);

        // Emit $ArrayBuffer, $SharedArrayBuffer, and $DataView for standalone TypedArray support (Phase 20)
        EmitArrayBufferType(moduleBuilder, runtime);
        EmitSharedArrayBufferType(moduleBuilder, runtime);
        EmitDataViewType(moduleBuilder, runtime);
        EmitTypedArrayTypes(moduleBuilder, runtime);

        // Emit stream classes for standalone stream support
        // NOTE: Must come after EventEmitter (stream types extend $EventEmitter)
        // Order matters due to inheritance and cross-references:
        // - Writable is standalone
        // - Readable's Pipe() method needs to reference Duplex (for piping to Duplex streams)
        // - Duplex extends Readable
        // - Transform extends Duplex
        // - PassThrough extends Transform
        //
        // Two-phase approach to resolve circular reference:
        // Phase 1: Define types, fields, and most methods (no CreateType)
        // Phase 2: Add methods that need cross-references, then CreateType
        // Node-stream types ($Readable / $Writable / $Duplex / $Transform / etc.)
        // are referenced from EmitInvokeValue's central dispatch in $Runtime, so
        // we always emit them in Phase 1. Defer node-stream gating to Phase 2.
        EmitTSWritableClass(moduleBuilder, runtime);
        EmitTSReadableTypeDefinition(moduleBuilder, runtime);  // Phase 1: type, fields, most methods
        EmitTSDuplexTypeDefinition(moduleBuilder, runtime);    // Phase 1: type, fields, all methods
        EmitTSReadablePhaseTwoMethods(runtime);                  // Phase 2a: Push, Pipe (need Duplex)
        EmitTSDuplexFinalize(runtime);                         // Phase 2: CreateType
        EmitTSTransformClass(moduleBuilder, runtime);
        EmitMapFilterTransformCallbackClasses(moduleBuilder, runtime); // Helper classes for map/filter
        EmitTSReadableMapFilterMethods(runtime);               // Phase 2b: Map, Filter (need Transform) + CreateType
        EmitTSPassThroughClass(moduleBuilder, runtime);
        EmitTSStreamUtilsClass(moduleBuilder, runtime);
        EmitTSZlibTransformClass(moduleBuilder, runtime);

        // Function wrapper emission is deferred below until AFTER $BoundArrayMethod /
        // $BoundMapMethod / $BoundSetMethod Phase 1 so their Invoke MethodBuilders
        // are available to the wrapper bodies (for dispatching .call/.apply/.bind on
        // bound methods).

        // Emit util module types for standalone execution
        // Must come after $Buffer (TextEncoder returns $Buffer).
        // $DeprecatedFunction / $CallbackifiedFunction / $PromisifiedFunction and
        // $TextDecoderDecodeMethod are referenced from EmitInvokeValue's central
        // dispatch — always emit in Phase 1. Phase 2 will gate by also gating the
        // dispatch arms.
        EmitTSDeprecatedFunctionClass(moduleBuilder, runtime);
        EmitTSCallbackifiedFunctionClass(moduleBuilder, runtime);
        EmitPromisifyCallbackClass(moduleBuilder, runtime);  // Must come before PromisifiedFunction
        EmitTSPromisifiedFunctionClass(moduleBuilder, runtime);
        EmitTSTextEncoderClass(moduleBuilder, runtime);
        EmitTSTextDecoderClass(moduleBuilder, runtime);
        EmitTSTextDecoderDecodeMethodClass(moduleBuilder, runtime);

        // $StringDecoder class removed — StringDecoder migrated to
        // stdlib/node/string_decoder.ts (pure-TS over the Buffer JS API).

        // Emit $Stats class for fs.stat() and related methods
        // Must come before fs module methods which use it
        EmitStatsClass(moduleBuilder, runtime);

        // Emit $CJSModule — backs the `module` local bound in every CJS module init.
        // No ordering constraint relative to EmitRuntimeClass; placed here alongside
        // other small emitted wrapper types.
        EmitCjsModuleClass(moduleBuilder, runtime);

        // Emit $Arguments : List<object> marker subclass. Must come before
        // any IL that constructs `arguments` (ILCompiler.Functions.cs uses
        // runtime.ArgumentsDefaultCtor / ArgumentsEnumerableCtor).
        EmitArgumentsTypeDefinition(moduleBuilder, runtime);

        // Emit $BoundArrayMethod type and constructor (Phase 1)
        // Must come before EmitRuntimeClass so GetListProperty can use the constructor
        EmitBoundArrayMethodTypeDefinition(moduleBuilder, runtime);

        // Emit $BoundMapMethod / $BoundSetMethod types and constructors (Phase 1)
        // Must come before EmitRuntimeClass so GetMapProperty/GetSetProperty can use them
        EmitBoundMapMethodTypeDefinition(moduleBuilder, runtime);
        EmitBoundSetMethodTypeDefinition(moduleBuilder, runtime);

        // Emit $BoundAnyFunction (the partial-apply wrapper for .bind on non-$TSFunction
        // callables) and the function bind/call/apply wrappers. All reference the
        // Bound*Method TypeBuilders above, so they MUST come after Phase 1 of those.
        // They come before EmitRuntimeClass so GetFunctionMethod (inside EmitRuntimeClass)
        // can use their constructors.
        EmitBoundAnyFunctionClass(moduleBuilder, runtime);
        EmitFunctionBindWrapperClass(moduleBuilder, runtime);
        EmitFunctionCallWrapperClass(moduleBuilder, runtime);
        EmitFunctionApplyWrapperClass(moduleBuilder, runtime);

        // Emit $MethodCallable type and constructor (Phase 1)
        // Must come before EmitRuntimeClass so GetFieldsProperty can wrap GetMember results
        EmitMethodCallableTypeDefinition(moduleBuilder, runtime);

        // Emit $TemplateStringsList class for tagged template literals
        // Must come before EmitRuntimeClass so InvokeTaggedTemplate can use the constructor
        EmitTemplateStringsListClass(moduleBuilder, runtime);

        // $PropertyDescriptorStore is now emitted earlier (just before $RegExp)
        // so types that need CompiledPropertyDescriptorType during their own
        // emission can reference it. This used to live here.

        // Net / Dgram types — always emit. Same central-dispatch concern as HTTP/TLS.
        EmitTSNetSocketPhase1(moduleBuilder, runtime);
        EmitTSNetServerPhase1(moduleBuilder, runtime);
        EmitDatagramSocketTypeDefinition(moduleBuilder, runtime);

        // Emit $ReadlineInterface type definition (Phase 1)
        // Must come before EmitRuntimeClass so ReadlineCreateInterface can use the constructor
        if (features.UsesReadline)
            EmitReadlineInterfaceTypeDefinition(moduleBuilder, runtime);

        // Emit $FinRegEntry type (finalizer helper for FinalizationRegistry)
        // Must come before EmitRuntimeClass so Register can use the constructor
        if (features.UsesFinalizationRegistry)
            EmitFinRegEntryTypeDefinition(moduleBuilder, runtime);

        // FS stream/watcher types — always emit (FS module methods reference them
        // unconditionally inside $Runtime). Phase 2.
        EmitFsStreamTypeDefinitions(moduleBuilder, runtime);
        EmitFsWatcherClass(moduleBuilder, runtime);
        EmitStatWatcherClass(moduleBuilder, runtime);

        // Emit $Runtime class with all helper methods
        EmitRuntimeClass(moduleBuilder, runtime);

        // Emit $Runtime.NewOnFunction — the JS `new` protocol for runtime-valued
        // function callees. Depends on $Object, $TSFunction, $BoundTSFunction, and
        // the $Runtime type itself all being defined.
        EmitNewOnFunction(_runtimeTypeBuilder!, runtime);

        // Emit $BroadcastChannel — extends $EventEmitter, dispatches via $EventLoop,
        // and clones messages via $Runtime.StructuredClone (populated during EmitRuntimeClass
        // → EmitWorkerHelpers → EmitStructuredCloneHelper).
        // NOTE: Must come after EmitRuntimeClass so runtime.StructuredCloneClone is set.
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSBroadcastChannel
        if (features.UsesBroadcastChannel)
            EmitBroadcastChannelClass(moduleBuilder, runtime);

        // Web Streams — always emit. EmitGlobalThisMethods references the
        // ReadableStream/WritableStream/TransformStream getters unconditionally.
        // Phase 2 will gate by also gating those globalThis arms.
        EmitQueuingStrategyClasses(moduleBuilder, runtime);
        EmitWritableStreamClasses(moduleBuilder, runtime);
        EmitReadableStreamClasses(moduleBuilder, runtime);
        EmitTransformStreamClasses(moduleBuilder, runtime);

        // Emit $ReflectMetadataDecorator closure class
        // Must come after EmitRuntimeClass (calls ReflectDefineMetadata)
        // External usage in ReflectStaticEmitter has a null-check fallback, so
        // skipping this is safe even if some path slips past the detector.
        if (features.UsesReflectMetadata)
            EmitReflectMetadataDecoratorClass(moduleBuilder, runtime);

        // Finalize $BoundArrayMethod with Invoke method (Phase 2)
        // Must come after EmitRuntimeClass (needs array methods defined)
        EmitBoundArrayMethodFinalize(runtime);

        // Finalize $BoundMapMethod / $BoundSetMethod with Invoke method (Phase 2)
        // Must come after EmitRuntimeClass (needs Map*/Set* runtime methods defined)
        EmitBoundMapMethodFinalize(runtime);
        EmitBoundSetMethodFinalize(runtime);

        // Finalize $MethodCallable with Invoke method (Phase 2)
        EmitMethodCallableFinalize(runtime);

        // Emit net/http closure types (Phase 1b)
        // Must come after Phase 1a (references $NetSocket/$NetServer/$HttpServer TypeBuilders/fields)
        // Must come before Phase 2 (methods Newobj the closure constructors)
        EmitNetClosureTypes(moduleBuilder, runtime);

        // Emit HTTP accept worker body (Phase 2)
        // Must come after EmitNetClosureTypes (uses $HttpAcceptClosure)
        EmitHttpServerAcceptWorkerBody(runtime);

        // Finalize $NetSocket class (Phase 2)
        EmitTSNetSocketPhase2(runtime);

        // Finalize $NetServer class (Phase 2)
        EmitTSNetServerPhase2(runtime);

        // Emit dgram message closure class (needed by receive worker body)
        EmitDgramMessageClosureClass(moduleBuilder, runtime);

        // Finalize $DatagramSocket class (Phase 2)
        // Must come after EmitRuntimeClass and DgramMessageClosure
        EmitDgramReceiveWorkerBody(runtime);
        EmitDatagramSocketFinalize(runtime);

        // Emit TLS closure classes, finalize $Runtime and $TlsServer (Phase 2)
        // Must come after EmitRuntimeClass (TlsConnectAndHandshake helper is on $Runtime)
        EmitTlsAcceptClosureClass(moduleBuilder, runtime);
        EmitTlsConnectClosureClass(moduleBuilder, runtime);
        EmitTlsConnectBody(runtime);   // Deferred TlsConnect body (needs $TlsConnectClosure)
        EmitRuntimeClassFinalize();     // Finalize $Runtime after all method bodies
        EmitTlsServerAcceptWorkerBody(runtime);
        EmitTlsServerFinalize();

        // Finalize $ReadlineInterface class (Phase 2)
        // Must come after EmitRuntimeClass (Question uses InvokeValue)
        if (features.UsesReadline)
            EmitReadlineInterfaceFinalize(runtime);

        // Finalize $Sign class (Phase 2)
        // Must come after EmitRuntimeClass (Sign uses SignDataBytes)
        EmitTSSignFinalize(runtime);

        // Finalize $Verify class (Phase 2)
        // Must come after EmitRuntimeClass (Verify uses VerifyDataBytes)
        EmitTSVerifyFinalize(runtime);

        // Finalize $ECDH class (Phase 2)
        // Must come after EmitRuntimeClass (methods use EncodeResult/DecodeInput)
        EmitTSECDHFinalize(runtime);

        // Finalize $BoundECDHMethod class (Phase 2)
        // Must come after ECDH methods (Invoke calls them)
        EmitBoundECDHMethodFinalize(runtime);

        // Finalize $DiffieHellman class (Phase 2)
        // Must come after EmitRuntimeClass (methods use EncodeResult/DecodeInput)
        EmitTSDHFinalize(runtime);

        // Finalize $BoundDHMethod class (Phase 2)
        // Must come after DH methods (Invoke calls them)
        EmitBoundDHMethodFinalize(runtime);

        return runtime;
    }
}
