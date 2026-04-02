using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits the runtime support types into the generated assembly.
/// This makes compiled DLLs standalone without requiring SharpTS.dll.
/// </summary>
public partial class RuntimeEmitter
{
    private readonly TypeProvider _types;

    public RuntimeEmitter(TypeProvider types)
    {
        _types = types;
    }

    public EmittedRuntime EmitAll(ModuleBuilder moduleBuilder)
    {
        var runtime = new EmittedRuntime();

        // Emit $Undefined singleton class first (other methods need this type)
        EmitUndefinedClass(moduleBuilder, runtime);

        // Emit IUnionType marker interface first (union types need to implement this)
        EmitIUnionTypeInterface(moduleBuilder, runtime);

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

        // Emit $Array class for standalone array support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSArray
        EmitTSArrayClass(moduleBuilder, runtime);

        // Emit $IHasFields interface for unified property access
        // Must come before $Object which implements it
        EmitHasFieldsInterface(moduleBuilder, runtime);

        // Emit $Object class for standalone object support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSObject
        EmitTSObjectClass(moduleBuilder, runtime);

        // Emit $RegExp class for standalone regex support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSRegExp
        EmitTSRegExpClass(moduleBuilder, runtime);

        // Emit $AssertionError class for standalone assert module support
        // NOTE: Must stay in sync with AssertionError in AssertModuleInterpreter.cs
        EmitTSAssertionErrorClass(moduleBuilder, runtime);

        // Emit $NodeError class for standalone fs module support
        // NOTE: Must stay in sync with NodeError in Runtime/BuiltIns/Modules/NodeError.cs
        EmitNodeErrorClass(moduleBuilder, runtime);

        // Emit $Buffer class for standalone buffer support
        // NOTE: Must come before $Hash and $Hmac since they return Buffer
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSBuffer
        EmitTSBufferClass(moduleBuilder, runtime);

        // Emit $Hash class for standalone crypto hash support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSHash
        EmitTSHashClass(moduleBuilder, runtime);

        // Emit $Hmac class for standalone crypto HMAC support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSHmac
        EmitTSHmacClass(moduleBuilder, runtime);

        // Emit $Cipher class for standalone crypto cipher support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSCipher
        EmitTSCipherClass(moduleBuilder, runtime);

        // Emit $Decipher class for standalone crypto decipher support
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSDecipher
        EmitTSDecipherClass(moduleBuilder, runtime);

        // Emit $Sign type definition (Phase 1)
        // Sign method added in Phase 2 after EmitRuntimeClass
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSSign
        EmitTSSignTypeDefinition(moduleBuilder, runtime);

        // Emit $Verify type definition (Phase 1)
        // Verify method added in Phase 2 after EmitRuntimeClass
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSVerify
        EmitTSVerifyTypeDefinition(moduleBuilder, runtime);

        // Emit $TSKeyObject class for standalone crypto key object support
        // Must come after $Buffer (export returns Buffer for secret keys)
        EmitTSKeyObjectClass(moduleBuilder, runtime);

        // Emit $ECDH type definition (Phase 1)
        // ECDH methods added in Phase 2 after EmitRuntimeClass
        // Must come after $Buffer (encoding returns Buffer)
        EmitTSECDHTypeDefinition(moduleBuilder, runtime);

        // Emit $BoundECDHMethod type definition (Phase 1)
        // Invoke method added in Phase 2 after ECDH methods
        // Must come after $ECDH (uses TSECDHType for field)
        EmitBoundECDHMethodTypeDefinition(moduleBuilder, runtime);

        // Emit $DiffieHellman type definition (Phase 1)
        // DH methods added in Phase 2 after EmitRuntimeClass
        // Must come after $Buffer (encoding returns Buffer)
        EmitTSDHTypeDefinition(moduleBuilder, runtime);

        // Emit $BoundDHMethod type definition (Phase 1)
        // Invoke method added in Phase 2 after DH methods
        // Must come after $DiffieHellman (uses TSDiffieHellmanType for field)
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
        EmitAsyncLocalStorageClass(moduleBuilder, runtime);

        // Emit $EventEmitter class for standalone event emitter support
        // NOTE: Must come after BoundTSFunction (uses TSFunctionType, BoundTSFunctionType)
        // NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSEventEmitter
        EmitTSEventEmitterClass(moduleBuilder, runtime);

        // Emit HTTP types for standalone HTTP server support
        // NOTE: Must come after EventEmitter ($HttpServer extends $EventEmitter)
        EmitHttpTypes(moduleBuilder, runtime);

        // Emit TLS types for standalone TLS support
        // NOTE: Must come after EventEmitter ($TlsSocket and $TlsServer extend $EventEmitter)
        EmitTlsTypes(moduleBuilder, runtime);

        // Emit cluster types for standalone cluster support
        // NOTE: Must come after EventEmitter ($ClusterWorker and $ClusterManager extend it)
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
        EmitTSWritableClass(moduleBuilder, runtime);
        EmitTSReadableTypeDefinition(moduleBuilder, runtime);  // Phase 1: type, fields, most methods
        EmitTSDuplexTypeDefinition(moduleBuilder, runtime);    // Phase 1: type, fields, all methods
        EmitTSReadableMethods(runtime);                        // Phase 2: Pipe method + CreateType
        EmitTSDuplexFinalize(runtime);                         // Phase 2: CreateType
        EmitTSTransformClass(moduleBuilder, runtime);
        EmitTSPassThroughClass(moduleBuilder, runtime);
        EmitTSZlibTransformClass(moduleBuilder, runtime);
        EmitTSStreamUtilsClass(moduleBuilder, runtime);

        // Emit function method wrapper classes for bind/call/apply
        // Must come after TSFunction and BoundTSFunction
        EmitFunctionBindWrapperClass(moduleBuilder, runtime);
        EmitFunctionCallWrapperClass(moduleBuilder, runtime);
        EmitFunctionApplyWrapperClass(moduleBuilder, runtime);

        // Emit util module types for standalone execution
        // Must come after $Buffer (TextEncoder returns $Buffer)
        EmitTSDeprecatedFunctionClass(moduleBuilder, runtime);
        EmitTSCallbackifiedFunctionClass(moduleBuilder, runtime);
        EmitPromisifyCallbackClass(moduleBuilder, runtime);  // Must come before PromisifiedFunction
        EmitTSPromisifiedFunctionClass(moduleBuilder, runtime);
        EmitTSTextEncoderClass(moduleBuilder, runtime);
        EmitTSTextDecoderClass(moduleBuilder, runtime);
        EmitTSTextDecoderDecodeMethodClass(moduleBuilder, runtime);

        // Emit $StringDecoder class for string_decoder module
        // Must come after $Buffer (StringDecoder works with Buffer)
        EmitTSStringDecoderClass(moduleBuilder, runtime);

        // Emit $Stats class for fs.stat() and related methods
        // Must come before fs module methods which use it
        EmitStatsClass(moduleBuilder, runtime);

        // Emit $BoundArrayMethod type and constructor (Phase 1)
        // Must come before EmitRuntimeClass so GetListProperty can use the constructor
        EmitBoundArrayMethodTypeDefinition(moduleBuilder, runtime);

        // Emit $MethodCallable type and constructor (Phase 1)
        // Must come before EmitRuntimeClass so GetFieldsProperty can wrap GetMember results
        EmitMethodCallableTypeDefinition(moduleBuilder, runtime);

        // Emit $TemplateStringsList class for tagged template literals
        // Must come before EmitRuntimeClass so InvokeTaggedTemplate can use the constructor
        EmitTemplateStringsListClass(moduleBuilder, runtime);

        // Emit $PropertyDescriptorStore and supporting types for standalone object semantics
        // Must come before EmitRuntimeClass so Object.freeze/seal/etc. can use it
        EmitPropertyDescriptorTypes(moduleBuilder, runtime);

        // Emit $NetSocket type definition (Phase 1a)
        // Must come after EventEmitter ($NetSocket extends $EventEmitter)
        // Must come before EmitRuntimeClass so NetCreateConnection can use the constructor
        EmitTSNetSocketPhase1(moduleBuilder, runtime);

        // Emit $NetServer type definition (Phase 1a)
        // Must come after $NetSocket ($NetServer creates $NetSocket instances)
        // Must come before EmitRuntimeClass so NetCreateServer can use the constructor
        EmitTSNetServerPhase1(moduleBuilder, runtime);

        // Emit $DatagramSocket type definition (Phase 1)
        // Must come after EventEmitter ($DatagramSocket extends $EventEmitter)
        // Must come before EmitRuntimeClass so DgramCreateSocket can use the constructor
        EmitDatagramSocketTypeDefinition(moduleBuilder, runtime);

        // Emit $ReadlineInterface type definition (Phase 1)
        // Must come before EmitRuntimeClass so ReadlineCreateInterface can use the constructor
        EmitReadlineInterfaceTypeDefinition(moduleBuilder, runtime);

        // Emit $FinRegEntry type (finalizer helper for FinalizationRegistry)
        // Must come before EmitRuntimeClass so Register can use the constructor
        EmitFinRegEntryTypeDefinition(moduleBuilder, runtime);

        // Emit $FsReadStream and $FsWriteStream types
        // Must come after TSFunction (uses TSFunctionInvoke) and before EmitRuntimeClass
        EmitFsStreamTypeDefinitions(moduleBuilder, runtime);

        // Emit $FsWatcher and $StatWatcher types
        // Must come after $EventEmitter and $EventLoop, before EmitRuntimeClass
        EmitFsWatcherClass(moduleBuilder, runtime);
        EmitStatWatcherClass(moduleBuilder, runtime);

        // Emit $Runtime class with all helper methods
        EmitRuntimeClass(moduleBuilder, runtime);

        // Emit $ReflectMetadataDecorator closure class
        // Must come after EmitRuntimeClass (calls ReflectDefineMetadata)
        EmitReflectMetadataDecoratorClass(moduleBuilder, runtime);

        // Finalize $BoundArrayMethod with Invoke method (Phase 2)
        // Must come after EmitRuntimeClass (needs array methods defined)
        EmitBoundArrayMethodFinalize(runtime);

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

        // Finalize $DatagramSocket class (Phase 2)
        // Must come after EmitRuntimeClass
        EmitDatagramSocketFinalize(runtime);

        // Finalize $ReadlineInterface class (Phase 2)
        // Must come after EmitRuntimeClass (Question uses InvokeValue)
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
