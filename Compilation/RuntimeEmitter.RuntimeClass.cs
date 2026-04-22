using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Defines util inspect method signatures early so ConsoleDir can reference them.
    /// Method bodies are emitted later in EmitUtilStandaloneMethods.
    /// </summary>
    private void DefineUtilInspectSignatures(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // InspectValue(object value, int depth, int currentDepth) -> string
        runtime.UtilInspectValue = typeBuilder.DefineMethod(
            "UtilInspectValue",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object, _types.Int32, _types.Int32]);

        // InspectArray(object arr, int depth, int currentDepth) -> string
        runtime.UtilInspectArray = typeBuilder.DefineMethod(
            "UtilInspectArray",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object, _types.Int32, _types.Int32]);

        // InspectObject(object obj, int depth, int currentDepth) -> string
        runtime.UtilInspectObject = typeBuilder.DefineMethod(
            "UtilInspectObject",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object, _types.Int32, _types.Int32]);
    }

    private void EmitRuntimeClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        // Define class: public static class $Runtime
        var typeBuilder = moduleBuilder.DefineType(
            "$Runtime",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );
        runtime.RuntimeType = typeBuilder;
        _runtimeTypeBuilder = typeBuilder;

        // Static field for Random
        var randomField = typeBuilder.DefineField("_random", _types.Random, FieldAttributes.Private | FieldAttributes.Static);

        // Static field for symbol storage: ConditionalWeakTable<object, Dictionary<object, object?>>
        var symbolDictType = _types.DictionaryObjectObject;
        var symbolStorageType = _types.MakeGenericType(_types.ConditionalWeakTableOpen, _types.Object, symbolDictType);
        var symbolStorageField = typeBuilder.DefineField(
            "_symbolStorage",
            symbolStorageType,
            FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly
        );
        runtime.SymbolStorageField = symbolStorageField;

        // Static fields for Object.freeze/seal tracking: ConditionalWeakTable<object, object>
        // Made public so other types (like class constructors) can access for freeze checks
        var frozenObjectsField = typeBuilder.DefineField(
            "_frozenObjects",
            _types.ConditionalWeakTable,
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly
        );
        runtime.FrozenObjectsField = frozenObjectsField;
        var sealedObjectsField = typeBuilder.DefineField(
            "_sealedObjects",
            _types.ConditionalWeakTable,
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly
        );
        runtime.SealedObjectsField = sealedObjectsField;

        // Static field for non-extensible objects tracking: ConditionalWeakTable<object, object>
        var nonExtensibleObjectsField = typeBuilder.DefineField(
            "_nonExtensibleObjects",
            _types.ConditionalWeakTable,
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly
        );
        runtime.NonExtensibleObjectsField = nonExtensibleObjectsField;

        // Static field for prototype tracking: ConditionalWeakTable<object, object>
        var prototypeStoreField = typeBuilder.DefineField(
            "_prototypeStore",
            _types.ConditionalWeakTable,
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly
        );
        runtime.PrototypeStoreField = prototypeStoreField;

        // Static sentinel field for null/undefined Map keys
        var mapNullSentinelField = typeBuilder.DefineField(
            "_mapNullSentinel",
            _types.Object,
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly
        );
        runtime.MapNullSentinel = mapNullSentinelField;

        // Static field for FinalizationRegistry poke table
        runtime.FinRegPokeTableField = typeBuilder.DefineField(
            "_finRegPokeTable",
            _types.ConditionalWeakTableObjectObject,
            FieldAttributes.Private | FieldAttributes.Static
        );

        // Static field for console group indentation level (needed early for ConsoleLog)
        var consoleGroupLevelField = typeBuilder.DefineField(
            "_consoleGroupLevel",
            _types.Int32,
            FieldAttributes.Private | FieldAttributes.Static
        );
        runtime.ConsoleGroupLevelField = consoleGroupLevelField;

        // Static constructor to initialize Random and symbol storage
        var cctorBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            Type.EmptyTypes
        );
        var cctorIL = cctorBuilder.GetILGenerator();

        // Initialize _random = new Random()
        cctorIL.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.Random));
        cctorIL.Emit(OpCodes.Stsfld, randomField);

        // Initialize _symbolStorage = new ConditionalWeakTable<object, Dictionary<object, object?>>()
        cctorIL.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(symbolStorageType));
        cctorIL.Emit(OpCodes.Stsfld, symbolStorageField);

        // Initialize _frozenObjects = new ConditionalWeakTable<object, object>()
        cctorIL.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.ConditionalWeakTable));
        cctorIL.Emit(OpCodes.Stsfld, frozenObjectsField);

        // Initialize _sealedObjects = new ConditionalWeakTable<object, object>()
        cctorIL.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.ConditionalWeakTable));
        cctorIL.Emit(OpCodes.Stsfld, sealedObjectsField);

        // Initialize _nonExtensibleObjects = new ConditionalWeakTable<object, object>()
        cctorIL.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.ConditionalWeakTable));
        cctorIL.Emit(OpCodes.Stsfld, nonExtensibleObjectsField);

        // Initialize _prototypeStore = new ConditionalWeakTable<object, object>()
        cctorIL.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.ConditionalWeakTable));
        cctorIL.Emit(OpCodes.Stsfld, prototypeStoreField);

        // Initialize _mapNullSentinel = new object()
        cctorIL.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.Object));
        cctorIL.Emit(OpCodes.Stsfld, mapNullSentinelField);

        // Initialize perf_hooks timing fields (must be called after fields are defined)
        // Note: Fields will be defined by EmitPerfHooksMethods, so we defer this initialization
        // The initialization is done inline in EmitPerfHooksMethods instead

        // Initialize _finRegPokeTable = new ConditionalWeakTable<object, object>()
        EmitFinRegPokeTableInit(cctorIL, runtime);

        // Define the event-subscription registry (field + two helper methods). Must be
        // emitted while we still hold the cctor IL generator so the field gets initialized.
        EmitEventSubscriptionHelpers(typeBuilder, runtime, cctorIL);

        cctorIL.Emit(OpCodes.Ret);

        // Emit all methods - these are now in partial class files
        // Core utilities
        EmitStringify(typeBuilder, runtime);
        EmitStringRaw(typeBuilder, runtime);
        // Format specifier helpers (must be emitted before ConsoleLog/ConsoleLogMultiple which call them)
        EmitHasFormatSpecifiers(typeBuilder, runtime);
        EmitFormatSingleArg(typeBuilder, runtime);
        EmitFormatAsInteger(typeBuilder, runtime);
        EmitFormatAsFloat(typeBuilder, runtime);
        EmitFormatAsJson(typeBuilder, runtime);
        EmitFormatConsoleArgs(typeBuilder, runtime);
        // GetConsoleIndent must be emitted before ConsoleLog/ConsoleLogMultiple which call it
        EmitGetConsoleIndent(typeBuilder, runtime);
        EmitConsoleLog(typeBuilder, runtime);
        // JoinWithStringify must be emitted before ConsoleLogMultiple which uses it
        EmitJoinWithStringify(typeBuilder, runtime);
        EmitConsoleLogMultiple(typeBuilder, runtime);
        EmitToNumber(typeBuilder, runtime);
        EmitConvertToNumber(typeBuilder, runtime);
        EmitJsToInt32(typeBuilder, runtime);
        EmitIsTruthy(typeBuilder, runtime);
        EmitTypeOf(typeBuilder, runtime);
        EmitInstanceOf(typeBuilder, runtime);
        EmitAdd(typeBuilder, runtime);
        EmitEquals(typeBuilder, runtime);
        // Object methods - must come BEFORE iterator methods since GetProperty, InvokeMethodValue are needed
        EmitCreateObject(typeBuilder, runtime);
        EmitGetArrayMethod(typeBuilder, runtime);
        EmitGetFunctionMethod(typeBuilder, runtime);  // For bind/call/apply on functions
        EmitToPascalCase(typeBuilder, runtime);  // Must be emitted before GetFieldsProperty/SetFieldsProperty
        EmitSafeGetMethod(typeBuilder, runtime); // Must be emitted before GetFieldsProperty/SetFieldsProperty
        // Promise callback types must be created before InvokeValue (which dispatches to them)
        EmitPromiseCallbackTypes(moduleBuilder, runtime);
        // ArrayConstructor (#61) must come before InvokeValue since InvokeValue's
        // Type-callee dispatch branch emits a direct call to it for `Array(n)`
        // patterns where Array was stored as a value.
        EmitArrayConstructor(typeBuilder, runtime);
        // LookupBuiltInStaticMember (#63): MethodBuilder defined here so
        // GetProperty's Type branch can reference it; body emitted later
        // after all backing static methods are in place.
        DefineLookupBuiltInStaticMember(typeBuilder, runtime);
        // InvokeValue/InvokeMethodValue must come before GetFieldsProperty (needs InvokeMethodValue for getters)
        // and before Promise methods (needed by InvokeCallback)
        EmitInvokeValue(typeBuilder, runtime);
        EmitInvokeMethodValue(typeBuilder, runtime);
        EmitGetFieldsProperty(typeBuilder, runtime);
        EmitGetListProperty(typeBuilder, runtime);
        EmitGetMapProperty(typeBuilder, runtime);
        EmitGetSetProperty(typeBuilder, runtime);
        EmitSetFieldsProperty(typeBuilder, runtime);
        EmitSetFieldsPropertyStrict(typeBuilder, runtime);
        // Exception helpers must come before Promise methods (Promise.any uses CreateException)
        EmitCreateException(typeBuilder, runtime);
        EmitWrapException(typeBuilder, runtime);
        // Promise methods must come before GetProperty (which needs PromiseThen for typeof p.then)
        EmitPromiseMethods(typeBuilder, runtime);
        // TypedArray detection helpers must come before GetProperty (which uses IsTypedArrayMethod)
        EmitTypedArrayDetectionHelpers(typeBuilder, runtime);
        EmitGetProperty(typeBuilder, runtime);
        EmitSetProperty(typeBuilder, runtime);
        EmitSetPropertyStrict(typeBuilder, runtime);
        EmitDeleteProperty(typeBuilder, runtime);
        EmitDeletePropertyStrict(typeBuilder, runtime);
        EmitMergeIntoObject(typeBuilder, runtime);
        EmitMergeIntoTSObject(typeBuilder, runtime);
        // Symbol support helpers - must come before iterator methods which depend on GetSymbolDict
        EmitGetSymbolDict(typeBuilder, runtime, symbolStorageField);
        EmitIsSymbol(typeBuilder, runtime);
        // DisposeResource depends on GetSymbolDict and InvokeMethodValue
        EmitDisposeResource(typeBuilder, runtime);
        // HasIn operator depends on IsSymbol and GetSymbolDict
        EmitHasIn(typeBuilder, runtime);
        // Array SetElement helpers - must come BEFORE GetIndex/SetIndex which reference them.
        // Previously only Typed variants were emitted here; the Object variant was deferred to
        // the Arrays section below. That left SetIndex's object-list branch unable to call it,
        // so we emit all of them up front now (including Object) for JS-spec auto-extend semantics.
        foreach (var desc in ArrayElements.All)
            EmitSetArrayElementFor(typeBuilder, runtime, desc);
        // Note: TypedArray detection helpers are emitted earlier (before GetProperty)
        EmitGetIndex(typeBuilder, runtime);
        EmitSetIndex(typeBuilder, runtime);
        EmitSetIndexStrict(typeBuilder, runtime);
        EmitDeleteIndex(typeBuilder, runtime);
        EmitDeleteIndexStrict(typeBuilder, runtime);
        EmitStrictModeHelpers(typeBuilder, runtime);
        // Basic iterator protocol methods - must come AFTER object methods (need GetProperty, InvokeMethodValue)
        EmitIteratorMethodsBasic(typeBuilder, runtime);
        // Emit $IteratorWrapper AFTER basic iterator methods (needs InvokeIteratorNext etc.)
        // but BEFORE IterateToList (which needs IteratorWrapperCtor)
        EmitIteratorWrapperType(moduleBuilder, runtime);
        // Advanced iterator methods (IterateToList) - needs IteratorWrapperCtor
        EmitIteratorMethodsAdvanced(typeBuilder, runtime);
        // ES2025 Iterator Helper methods and lazy wrapper types
        EmitIteratorHelperMethods(typeBuilder, moduleBuilder, runtime);
        // Arrays - must come AFTER iterator methods since ConcatArrays/ExpandCallArgs use IterateToList.
        // SetArrayElement* helpers (including Object variant) are emitted earlier, BEFORE SetIndex,
        // since SetIndex's object-list branch also calls SetArrayElement for auto-extend semantics.
        EmitCreateArray(typeBuilder, runtime);
        EmitGetLength(typeBuilder, runtime);
        EmitGetElement(typeBuilder, runtime);
        EmitGetKeys(typeBuilder, runtime);
        EmitGetOwnPropertyNames(typeBuilder, runtime);
        EmitGetValues(typeBuilder, runtime);
        EmitGetEntries(typeBuilder, runtime);
        EmitObjectFromEntries(typeBuilder, runtime);
        EmitObjectHasOwn(typeBuilder, runtime);
        EmitObjectIs(typeBuilder, runtime);
        EmitObjectAssign(typeBuilder, runtime);
        EmitObjectFreeze(typeBuilder, runtime, frozenObjectsField, sealedObjectsField);
        EmitObjectSeal(typeBuilder, runtime, sealedObjectsField);
        EmitObjectIsFrozen(typeBuilder, runtime, frozenObjectsField);
        EmitObjectIsSealed(typeBuilder, runtime, sealedObjectsField);
        EmitObjectDefineProperty(typeBuilder, runtime);
        EmitObjectGetOwnPropertyDescriptor(typeBuilder, runtime);
        EmitObjectDefineProperties(typeBuilder, runtime);
        EmitObjectGetOwnPropertyDescriptors(typeBuilder, runtime);
        EmitObjectCreate(typeBuilder, runtime, prototypeStoreField);
        EmitObjectPreventExtensions(typeBuilder, runtime, nonExtensibleObjectsField, frozenObjectsField, sealedObjectsField);
        EmitObjectIsExtensible(typeBuilder, runtime, nonExtensibleObjectsField, frozenObjectsField, sealedObjectsField);
        EmitGetOwnPropertySymbols(typeBuilder, runtime);
        EmitObjectGetPrototypeOf(typeBuilder, runtime, prototypeStoreField);
        EmitObjectSetPrototypeOf(typeBuilder, runtime, prototypeStoreField, nonExtensibleObjectsField);
        EmitObjectGroupBy(typeBuilder, runtime);
        EmitReflectSet(typeBuilder, runtime);
        EmitReflectSetPrototypeOf(typeBuilder, runtime, prototypeStoreField, nonExtensibleObjectsField);
        EmitReflectDefineProperty(typeBuilder, runtime);
        EmitReflectOwnKeys(typeBuilder, runtime);
        EmitReflectApply(typeBuilder, runtime);
        EmitReflectConstruct(typeBuilder, runtime);
        EmitIsArray(typeBuilder, runtime);
        EmitSpreadArray(typeBuilder, runtime);
        EmitConcatArrays(typeBuilder, runtime);
        EmitExpandCallArgs(typeBuilder, runtime);
        EmitArrayPop(typeBuilder, runtime);
        EmitArrayShift(typeBuilder, runtime);
        EmitArrayUnshift(typeBuilder, runtime);
        EmitArraySlice(typeBuilder, runtime);
        // Array callback methods must come after InvokeValue and IsTruthy
        EmitArrayMap(typeBuilder, runtime);
        EmitArrayFilter(typeBuilder, runtime);
        EmitArrayForEach(typeBuilder, runtime);
        EmitArrayPush(typeBuilder, runtime);
        EmitArrayFind(typeBuilder, runtime);
        EmitArrayFindIndex(typeBuilder, runtime);
        EmitArrayFindLast(typeBuilder, runtime);
        EmitArrayFindLastIndex(typeBuilder, runtime);
        EmitArraySome(typeBuilder, runtime);
        EmitArrayEvery(typeBuilder, runtime);
        EmitArrayReduce(typeBuilder, runtime);
        EmitArrayReduceRight(typeBuilder, runtime);
        EmitArrayIncludes(typeBuilder, runtime);
        EmitArrayIndexOf(typeBuilder, runtime);
        EmitArrayJoin(typeBuilder, runtime);
        EmitArrayConcat(typeBuilder, runtime);
        EmitArrayReverse(typeBuilder, runtime);
        EmitArrayFlatHelper(typeBuilder, runtime); // Must be before EmitArrayFlat
        EmitArrayFlat(typeBuilder, runtime);
        EmitArrayFlatMap(typeBuilder, runtime);
        EmitArrayFrom(typeBuilder, runtime);
        EmitArrayOf(typeBuilder, runtime);
        // EmitArrayConstructor is emitted earlier (before InvokeValue) so its
        // MethodBuilder is available to InvokeValue's Type-callee dispatch.
        EmitArraySort(typeBuilder, runtime);
        EmitArrayToSorted(typeBuilder, runtime);
        EmitToIntegerOrInfinityHelper(typeBuilder, runtime); // Must be before EmitArraySplice/EmitArrayWith
        EmitArraySplice(typeBuilder, runtime);
        EmitArrayToSpliced(typeBuilder, runtime);
        EmitArrayToReversed(typeBuilder, runtime);
        EmitArrayWith(typeBuilder, runtime);
        EmitArrayAt(typeBuilder, runtime);
        EmitArrayFill(typeBuilder, runtime);
        EmitArrayCopyWithin(typeBuilder, runtime);
        EmitArrayEntries(typeBuilder, runtime);
        EmitArrayKeys(typeBuilder, runtime);
        EmitArrayValues(typeBuilder, runtime);
        // String methods
        EmitStringCharAt(typeBuilder, runtime);
        EmitStringSubstring(typeBuilder, runtime);
        EmitStringSubstr(typeBuilder, runtime);
        EmitStringIndexOf(typeBuilder, runtime);
        EmitStringIndexOfFrom(typeBuilder, runtime);
        EmitStringReplace(typeBuilder, runtime);
        EmitStringSplit(typeBuilder, runtime);
        EmitStringIncludes(typeBuilder, runtime);
        EmitStringStartsWith(typeBuilder, runtime);
        EmitStringEndsWith(typeBuilder, runtime);
        EmitStringSlice(typeBuilder, runtime);
        EmitStringRepeat(typeBuilder, runtime);
        EmitStringPadStart(typeBuilder, runtime);
        EmitStringPadEnd(typeBuilder, runtime);
        EmitStringCharCodeAt(typeBuilder, runtime);
        EmitStringConcat(typeBuilder, runtime);
        EmitStringLastIndexOf(typeBuilder, runtime);
        EmitStringReplaceAll(typeBuilder, runtime);
        EmitStringAt(typeBuilder, runtime);
        EmitStringFromCharCode(typeBuilder, runtime);
        EmitStringCodePointAt(typeBuilder, runtime);
        EmitStringFromCodePoint(typeBuilder, runtime);
        EmitStringNormalize(typeBuilder, runtime);
        EmitStringLocaleCompare(typeBuilder, runtime);
        // Object utilities
        EmitGetSuperMethod(typeBuilder, runtime);
        // EmitCreateException and EmitWrapException moved earlier (before Promise methods)
        EmitThrowUndefinedVariable(typeBuilder, runtime);
        EmitRandom(typeBuilder, runtime, randomField);
        // Math.* adapters for value-form access (issue #60). Depends on
        // runtime.ToNumber which is emitted before this call.
        EmitMathAdapters(typeBuilder, runtime);
        EmitGetEnumMemberName(typeBuilder, runtime);
        EmitConcatTemplate(typeBuilder, runtime);
        EmitInvokeTaggedTemplate(typeBuilder, runtime);
        EmitInvokeTaggedTemplateWithThis(typeBuilder, runtime);
        EmitObjectRest(typeBuilder, runtime);
        // JSON methods
        EmitJsonParse(typeBuilder, runtime);
        EmitJsonParseWithReviver(typeBuilder, runtime);
        EmitJsonStringify(typeBuilder, runtime);
        EmitJsonStringifyFull(typeBuilder, runtime);
        // BigInt methods
        EmitCreateBigInt(typeBuilder, runtime);
        EmitBigIntArithmetic(typeBuilder, runtime);
        EmitBigIntComparison(typeBuilder, runtime);
        EmitBigIntBitwise(typeBuilder, runtime);
        // Promise methods moved earlier (before GetProperty, which needs PromiseThen for typeof p.then)
        // Number methods
        EmitNumberMethods(typeBuilder, runtime);
        // Fill in LookupBuiltInStaticMember's body now that IsArray, NumberIs*,
        // StringFrom*, and TSFunctionCtor are all in place (#63).
        EmitLookupBuiltInStaticMemberBody(runtime);
        // Microtask method (queueMicrotask) - must come before timer infrastructure so ProcessMicrotasks is available
        EmitQueueMicrotaskMethod(typeBuilder, runtime);
        // Virtual timer infrastructure (must come before DateMethods which calls ProcessPendingTimers)
        EmitTimerQueueInfrastructure(typeBuilder, runtime);
        // Date methods
        EmitDateMethods(typeBuilder, runtime);
        // RegExp methods
        EmitRegExpMethods(typeBuilder, runtime);
        // Error methods
        EmitErrorMethods(typeBuilder, runtime);
        // Map methods
        EmitMapMethods(typeBuilder, runtime);
        EmitMapGroupBy(typeBuilder, runtime);
        // Set methods
        EmitSetMethods(typeBuilder, runtime);
        // WeakMap methods
        EmitWeakMapMethods(typeBuilder, runtime);
        // WeakSet methods
        EmitWeakSetMethods(typeBuilder, runtime);
        // WeakRef methods
        EmitWeakRefMethods(typeBuilder, runtime);
        // FinalizationRegistry methods
        EmitFinalizationRegistryMethods(typeBuilder, runtime);
        // Proxy methods
        EmitProxyMethods(typeBuilder, runtime);
        // AbortController/AbortSignal methods (FireAbortEvent must be emitted before AbortController methods)
        EmitFireAbortEvent(typeBuilder, runtime);
        EmitAbortControllerMethods(typeBuilder, runtime);
        // Dynamic import methods
        EmitDynamicImportMethods(typeBuilder, runtime);
        // Async generator await continuation helper
        EmitAsyncGeneratorAwaitContinueMethods(typeBuilder, moduleBuilder, runtime);
        // NodeError conversion helpers (must be before fs methods which use them)
        EmitNodeErrorHelpers(typeBuilder, runtime);
        // Built-in module methods (fs, os, dns) — path migrated to stdlib/node/path.ts.
        EmitFsModuleMethods(typeBuilder, runtime);
        EmitOsModuleMethods(typeBuilder, runtime);
        EmitDnsModuleMethods(typeBuilder, runtime);
        EmitDnsPromisesMethods(typeBuilder, runtime);
        // Emit wrapper methods for named imports
        EmitFsModuleMethodWrappers(typeBuilder, runtime);
        // Querystring module methods migrated to stdlib/node/querystring.ts.
        // Path module methods migrated to stdlib/node/path.ts.
        // Assert module methods migrated to stdlib/node/assert.ts.
        // TTY module methods
        // primitive:tty — just isatty; user-facing tty is stdlib/node/tty.ts.
        EmitTtyPrimitiveMethods(typeBuilder, runtime);
        // URL module — migrated to stdlib/node/url.ts; no runtime helpers emitted.
        // HTTP module methods (fetch, http.createServer, etc.) - must be before globalThis
        EmitHttpModuleMethods(typeBuilder, runtime);
        // Net module methods (net.createServer, net.connect, etc.)
        EmitNetModuleMethods(typeBuilder, runtime);
        // TLS module methods (tls.createServer, tls.connect, etc.)
        EmitTlsModuleMethods(typeBuilder, runtime);
        // Dgram module methods (dgram.createSocket)
        EmitDgramModuleMethods(typeBuilder, runtime);
        // globalThis methods (ES2020) - must be after HTTP for fetch reference
        EmitGlobalThisMethods(typeBuilder, runtime);
        // Define util inspect method signatures before ConsoleExtensions (ConsoleDir uses UtilInspectValue)
        DefineUtilInspectSignatures(typeBuilder, runtime);
        // Console extensions (error, warn, clear, time, timeEnd, timeLog)
        EmitConsoleExtensions(typeBuilder, runtime);
        // Crypto module methods
        EmitCryptoMethods(typeBuilder, runtime);
        // Util module methods
        EmitUtilMethods(typeBuilder, runtime);
        // Readline module methods
        EmitReadlineMethods(typeBuilder, runtime);
        // Child process module methods
        EmitChildProcessMethods(typeBuilder, runtime);
        // Reflect metadata API
        EmitReflectMetadataMethods(typeBuilder, runtime);
        // fs.watch / fs.watchFile / fs.unwatchFile
        EmitFsWatchFactories(typeBuilder, runtime);
        // Timer methods (setTimeout, clearTimeout, setInterval, clearInterval)
        EmitSetTimeoutMethod(typeBuilder, runtime);
        EmitClearTimeoutMethod(typeBuilder, runtime);
        EmitSetIntervalMethod(typeBuilder, runtime);
        EmitClearIntervalMethod(typeBuilder, runtime);
        // Timer promise methods (timers/promises module)
        EmitTimerPromisesMethods(typeBuilder, runtime);
        // Timer module wrappers for namespace imports (import * as timers from 'timers')
        EmitTimerModuleWrappers(typeBuilder, runtime);
        // Timer promises module wrappers for named/namespace imports (import { setTimeout } from 'timers/promises')
        EmitTimerPromisesModuleWrappers(typeBuilder, runtime);
        // Process global methods (env, argv, nextTick) - must be after timer methods for nextTick
        EmitProcessMethods(typeBuilder, runtime);
        // Zlib module methods
        EmitZlibMethods(typeBuilder, runtime);
        // DNS module methods
        EmitDnsModuleMethods(typeBuilder, runtime);
        // primitive:perf — only the host-tied now() method; the rest of perf_hooks
        // is pure TypeScript in stdlib/node/perf_hooks.ts.
        EmitPerfPrimitiveMethods(typeBuilder, runtime);
        // string_decoder module migrated to stdlib/node/string_decoder.ts.

        // Intl support (Intl.NumberFormat)
        EmitIntlMethods(typeBuilder, runtime);

        // TLS handshake helpers (called via late-binding from emitted TLS types)
        EmitTlsHandshakeHelpers(typeBuilder, runtime);

        // Worker Threads support (SharedArrayBuffer, TypedArrays, Atomics, MessagePort, Worker)
        EmitWorkerHelpers(typeBuilder, runtime);

        // Cluster module support
        EmitClusterHelpers(typeBuilder, runtime);

        // Vm module support
        EmitVmMethods(typeBuilder, runtime);

        // Web Streams API (stream/web) is now fully pure-IL emitted via
        // RuntimeEmitter.QueuingStrategy.cs / WritableStream.cs /
        // ReadableStream.cs / TransformStream.cs. No late-binding helper
        // methods needed on $Runtime.

        // Private member helpers are no longer emitted; async/generator emitters
        // now bind directly to class-private storage and method tokens.

        // NOTE: CreateType() deferred to EmitRuntimeClassFinalize to allow
        // Phase 2 method bodies (e.g., TlsConnect) to be emitted after closure types.
    }

    private TypeBuilder? _runtimeTypeBuilder;

    /// <summary>
    /// Phase 2: Finalizes the $Runtime class after all deferred method bodies are emitted.
    /// </summary>
    internal void EmitRuntimeClassFinalize()
    {
        _runtimeTypeBuilder?.CreateType();
    }
}
