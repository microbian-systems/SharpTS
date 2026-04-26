using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits a tiny dedicated type <c>$ArgumentsContext</c> holding the thread-static
    /// <c>_currentArguments</c> slot used to surface JS <c>arguments</c> in compiled
    /// mode (#64). See call-site comment in <c>EmitAll</c> for why this isolation
    /// matters — placing the field on <c>$TSFunction</c> or <c>$Runtime</c> alongside
    /// other ThreadStatic slots regressed an Intl test in layout-dependent ways.
    /// </summary>
    private void EmitArgumentsContextClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$ArgumentsContext",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );
        var field = typeBuilder.DefineField(
            "_currentArguments",
            _types.ObjectArray,
            FieldAttributes.Public | FieldAttributes.Static);
        var threadStaticCtor = typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes)!;
        field.SetCustomAttribute(new CustomAttributeBuilder(threadStaticCtor, []));
        runtime.CurrentArgumentsField = field;
        typeBuilder.CreateType();
    }

    private void EmitTSFunctionClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        // Define class: public sealed class $TSFunction
        var typeBuilder = moduleBuilder.DefineType(
            "$TSFunction",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );
        runtime.TSFunctionType = typeBuilder;

        // Fields
        var targetField = typeBuilder.DefineField("_target", _types.Object, FieldAttributes.Private);
        var methodField = typeBuilder.DefineField("_method", _types.MethodInfo, FieldAttributes.Private);
        runtime.TSFunctionMethodField = methodField;
        // Cached name and length for functions where reflection doesn't work (e.g., MethodBuilder tokens)
        var cachedNameField = typeBuilder.DefineField("_cachedName", _types.String, FieldAttributes.Private);
        var cachedLengthField = typeBuilder.DefineField("_cachedLength", _types.Int32, FieldAttributes.Private);

        // Static cache for "this" fields: ConcurrentDictionary<Type, FieldInfo>
        // used to avoid reflection overhead in BindThis
        var fieldCacheType = _types.MakeGenericType(_types.ConcurrentDictionaryOpen, _types.Type, _types.FieldInfo);
        var fieldCacheField = typeBuilder.DefineField("_thisFieldCache", fieldCacheType, FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);

        // Static instance cache keyed by MethodInfo. Every reference to a
        // function declaration (`function f(){}` followed by `f` used as a value)
        // previously created a fresh $TSFunction wrapper — breaking
        // identity-keyed property storage in PropertyDescriptorStore. Result:
        // `fn.x = 42` wrote under wrapper A's identity, `fn.x` read under a new
        // wrapper B → returned null. Most visibly, the test262 harness does
        // `function assert(...){}; assert.sameValue = function(...){}` and
        // then silently failed to dispatch `assert.sameValue(a, b)` (typeof
        // returned "object"), turning real spec failures into false-positive
        // Passes. Caching by MethodInfo makes every reference to the same
        // function declaration return the same $TSFunction instance so PDS
        // read/write use the same key. Only applies to target=null wrappers
        // (static function declarations) — bound/closure wrappers carry state
        // and must stay fresh.
        var instanceCacheType = _types.MakeGenericType(_types.ConcurrentDictionaryOpen, _types.MethodInfo, typeBuilder);
        var instanceCacheField = typeBuilder.DefineField("_instanceCache", instanceCacheType, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
        runtime.TSFunctionInstanceCacheField = instanceCacheField;

        // Static prototype cache keyed by MethodInfo. Per JS spec every
        // function has a `.prototype` object whose identity persists across
        // references: `fn.prototype === fn.prototype` holds. Keying by
        // MethodInfo (rather than $TSFunction instance) means two wrappers
        // for the same declaration return the SAME prototype — even when
        // the instance-level cache isn't in use. This unblocks `fn.prototype.x = v`
        // and `this instanceof fn` correctness without requiring the wider
        // identity cache to be wired up at both emit sites. Value-type is
        // object rather than $TSObject so there's no TypeBuilder generic-arg
        // issue at cctor time — the runtime value is always a $TSObject.
        var prototypeCacheType = _types.MakeGenericType(_types.ConcurrentDictionaryOpen, _types.MethodInfo, _types.Object);
        var prototypeCacheField = typeBuilder.DefineField("_prototypeCache", prototypeCacheType, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
        runtime.TSFunctionPrototypeCacheField = prototypeCacheField;

        // Thread-static "current function this" slot. Reads in compiled function bodies
        // (LocalVariableResolver.LoadThis's final fallback path) pick this up when the
        // method has no `__this` parameter. Set + restored around InvokeWithThis below
        // (so `Fn.call(target, ...)` routes properties to target) and around
        // $Runtime.NewOnFunction's body (so `new Fn(...)` routes to the fresh instance).
        // Lives on $TSFunction rather than $Runtime so InvokeWithThis can reference it
        // at TSFunction-emit time — $Runtime hasn't been built yet at this point.
        var currentThisField = typeBuilder.DefineField(
            "_currentFunctionThis",
            _types.Object,
            FieldAttributes.Public | FieldAttributes.Static);
        var threadStaticCtor = typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes)!;
        currentThisField.SetCustomAttribute(new CustomAttributeBuilder(threadStaticCtor, []));
        runtime.CurrentFunctionThisField = currentThisField;

        // Note: the thread-static `_currentArguments` slot used for JS `arguments` is
        // defined on $ArgumentsContext (emitted before this type) — see
        // EmitArgumentsContextClass.

        // Static Constructor
        var cctorBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            Type.EmptyTypes
        );
        var cctorIL = cctorBuilder.GetILGenerator();
        cctorIL.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(fieldCacheType));
        cctorIL.Emit(OpCodes.Stsfld, fieldCacheField);
        // Generic ConcurrentDictionary whose value-type is the enclosing TypeBuilder —
        // member resolution on such a constructed type must go through
        // TypeBuilder.GetConstructor/GetMethod rather than .GetConstructor directly.
        var concurrentDictOpenCtor = typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).GetConstructor(Type.EmptyTypes)!;
        cctorIL.Emit(OpCodes.Newobj, TypeBuilder.GetConstructor(instanceCacheType, concurrentDictOpenCtor));
        cctorIL.Emit(OpCodes.Stsfld, instanceCacheField);
        // Prototype cache: ConcurrentDictionary<MethodInfo, object> — generic args
        // are both concrete CLR types, so the constructor resolves directly.
        cctorIL.Emit(OpCodes.Newobj, prototypeCacheType.GetConstructor(Type.EmptyTypes)!);
        cctorIL.Emit(OpCodes.Stsfld, prototypeCacheField);
        cctorIL.Emit(OpCodes.Ret);

        // Constructor: public $TSFunction(object target, MethodInfo method)
        var ctorBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.Object, _types.MethodInfo]
        );
        runtime.TSFunctionCtor = ctorBuilder;

        var ctorIL = ctorBuilder.GetILGenerator();
        // Call base constructor
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.Object));
        // this._target = target
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Stfld, targetField);
        // this._method = method
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_2);
        ctorIL.Emit(OpCodes.Stfld, methodField);
        // this._cachedLength = -1 (sentinel for "not cached")
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldc_I4_M1);
        ctorIL.Emit(OpCodes.Stfld, cachedLengthField);
        // this._cachedName = null (will use reflection)
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldnull);
        ctorIL.Emit(OpCodes.Stfld, cachedNameField);
        ctorIL.Emit(OpCodes.Ret);

        // Alternative constructor with cached name/length: public $TSFunction(object target, MethodInfo method, string name, int length)
        // Use this constructor when the MethodInfo might not support GetParameters() (e.g., MethodBuilder tokens in persisted assemblies)
        var ctorWithCacheBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.Object, _types.MethodInfo, _types.String, _types.Int32]
        );
        runtime.TSFunctionCtorWithCache = ctorWithCacheBuilder;

        var ctorCacheIL = ctorWithCacheBuilder.GetILGenerator();
        // Call base constructor
        ctorCacheIL.Emit(OpCodes.Ldarg_0);
        ctorCacheIL.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.Object));
        // this._target = target
        ctorCacheIL.Emit(OpCodes.Ldarg_0);
        ctorCacheIL.Emit(OpCodes.Ldarg_1);
        ctorCacheIL.Emit(OpCodes.Stfld, targetField);
        // this._method = method
        ctorCacheIL.Emit(OpCodes.Ldarg_0);
        ctorCacheIL.Emit(OpCodes.Ldarg_2);
        ctorCacheIL.Emit(OpCodes.Stfld, methodField);
        // this._cachedName = name
        ctorCacheIL.Emit(OpCodes.Ldarg_0);
        ctorCacheIL.Emit(OpCodes.Ldarg_3);
        ctorCacheIL.Emit(OpCodes.Stfld, cachedNameField);
        // this._cachedLength = length
        ctorCacheIL.Emit(OpCodes.Ldarg_0);
        ctorCacheIL.Emit(OpCodes.Ldarg, 4);  // 4th argument (0-indexed: 0=this, 1=target, 2=method, 3=name, 4=length)
        ctorCacheIL.Emit(OpCodes.Stfld, cachedLengthField);
        ctorCacheIL.Emit(OpCodes.Ret);

        // Static factory: public static $TSFunction GetOrCreate(MethodInfo method, string name, int length).
        // Returns a cached $TSFunction for the given MethodInfo, creating one if
        // not present. Used only for function DECLARATIONS (target=null), where
        // identity must be stable across references for PropertyDescriptorStore
        // to work (`fn.x = v` → `fn.x` roundtrips). name/length are used only
        // on first create (subsequent calls return the cached instance whose
        // name/length were fixed at that first-create moment). Cached values
        // are required because MethodBuilder tokens don't reflect at runtime.
        var getOrCreateBuilder = typeBuilder.DefineMethod(
            "GetOrCreate",
            MethodAttributes.Public | MethodAttributes.Static,
            typeBuilder,
            [_types.MethodInfo, _types.String, _types.Int32]
        );
        runtime.TSFunctionGetOrCreate = getOrCreateBuilder;
        var gocIL = getOrCreateBuilder.GetILGenerator();

        var existingLocal = gocIL.DeclareLocal(typeBuilder);
        var newInstLocal = gocIL.DeclareLocal(typeBuilder);

        // Resolve methods via TypeBuilder.GetMethod because the value-type is
        // still an open TypeBuilder at emit time.
        var concurrentDictOpen = typeof(System.Collections.Concurrent.ConcurrentDictionary<,>);
        var openTryGet = concurrentDictOpen.GetMethod("TryGetValue")!;
        var openGetOrAdd = concurrentDictOpen.GetMethods()
            .First(m => m.Name == "GetOrAdd" && m.GetParameters().Length == 2
                 && m.GetParameters()[1].ParameterType == m.DeclaringType!.GetGenericArguments()[1]);
        var tryGetValueM = TypeBuilder.GetMethod(instanceCacheType, openTryGet);
        var getOrAddM = TypeBuilder.GetMethod(instanceCacheType, openGetOrAdd);

        // if (_instanceCache.TryGetValue(method, out existing)) return existing;
        gocIL.Emit(OpCodes.Ldsfld, instanceCacheField);
        gocIL.Emit(OpCodes.Ldarg_0);
        gocIL.Emit(OpCodes.Ldloca, existingLocal);
        gocIL.Emit(OpCodes.Callvirt, tryGetValueM);
        var notFound = gocIL.DefineLabel();
        gocIL.Emit(OpCodes.Brfalse, notFound);
        gocIL.Emit(OpCodes.Ldloc, existingLocal);
        gocIL.Emit(OpCodes.Ret);

        // Not in cache: create new via the cached-name/length constructor
        // (MethodBuilder tokens don't reflect at runtime), try to add, return
        // cache value (handles races).
        gocIL.MarkLabel(notFound);
        gocIL.Emit(OpCodes.Ldnull);            // target = null (static method)
        gocIL.Emit(OpCodes.Ldarg_0);           // method
        gocIL.Emit(OpCodes.Ldarg_1);           // name
        gocIL.Emit(OpCodes.Ldarg_2);           // length
        gocIL.Emit(OpCodes.Newobj, ctorWithCacheBuilder);
        gocIL.Emit(OpCodes.Stloc, newInstLocal);

        // return _instanceCache.GetOrAdd(method, newInst)
        gocIL.Emit(OpCodes.Ldsfld, instanceCacheField);
        gocIL.Emit(OpCodes.Ldarg_0);
        gocIL.Emit(OpCodes.Ldloc, newInstLocal);
        gocIL.Emit(OpCodes.Callvirt, getOrAddM);
        gocIL.Emit(OpCodes.Ret);

        // Public getter for the internal _method field so sibling classes
        // (e.g., $Runtime.GetFunctionMethod's prototype cache) can read the
        // MethodInfo key without a FieldAccessException. A method is used
        // instead of making the field Public so SharpTSProxy.InvokeTrap's
        // reflection-based `_method` lookup (BindingFlags.NonPublic) keeps
        // working unchanged.
        var getMethodInfoBuilder = typeBuilder.DefineMethod(
            "GetMethodInfo",
            MethodAttributes.Public,
            _types.MethodInfo,
            Type.EmptyTypes
        );
        runtime.TSFunctionGetMethodInfo = getMethodInfoBuilder;
        var gmIL = getMethodInfoBuilder.GetILGenerator();
        gmIL.Emit(OpCodes.Ldarg_0);
        gmIL.Emit(OpCodes.Ldfld, methodField);
        gmIL.Emit(OpCodes.Ret);

        // Helper method: private static object[] AdjustArgs(MethodInfo method, object[] args)
        var adjustArgsMethod = EmitTSFunctionAdjustArgsHelper(typeBuilder, runtime);

        // Helper method: private static void ConvertArgsForUnionTypes(MethodInfo method, object[] args)
        var convertArgsMethod = EmitTSFunctionConvertArgsHelper(typeBuilder, runtime);

        // Invoke method: public object Invoke(object[] args)
        var invokeBuilder = typeBuilder.DefineMethod(
            "Invoke",
            MethodAttributes.Public,
            _types.Object,
            [_types.ObjectArray]
        );
        runtime.TSFunctionInvoke = invokeBuilder;

        var invokeIL = invokeBuilder.GetILGenerator();

        // Local variables
        var effectiveArgsLocal = invokeIL.DeclareLocal(_types.ObjectArray);
        var invokeTargetLocal = invokeIL.DeclareLocal(_types.Object);

        // Check if this is a static method with a bound target
        // if (_method.IsStatic && _target != null)
        var notStaticWithTarget = invokeIL.DefineLabel();
        var afterArgPrep = invokeIL.DefineLabel();

        invokeIL.Emit(OpCodes.Ldarg_0);
        invokeIL.Emit(OpCodes.Ldfld, methodField);
        invokeIL.Emit(OpCodes.Callvirt, _types.MethodInfo.GetProperty("IsStatic")!.GetGetMethod()!);
        invokeIL.Emit(OpCodes.Brfalse, notStaticWithTarget);

        invokeIL.Emit(OpCodes.Ldarg_0);
        invokeIL.Emit(OpCodes.Ldfld, targetField);
        invokeIL.Emit(OpCodes.Brfalse, notStaticWithTarget);

        // Static method with bound target: prepend target to args
        // effectiveArgs = new object[args.Length + 1];
        // effectiveArgs[0] = _target;
        // Array.Copy(args, 0, effectiveArgs, 1, args.Length);
        // invokeTarget = null;
        invokeIL.Emit(OpCodes.Ldarg_1);
        invokeIL.Emit(OpCodes.Ldlen);
        invokeIL.Emit(OpCodes.Conv_I4);
        invokeIL.Emit(OpCodes.Ldc_I4_1);
        invokeIL.Emit(OpCodes.Add);
        invokeIL.Emit(OpCodes.Newarr, _types.Object);
        invokeIL.Emit(OpCodes.Stloc, effectiveArgsLocal);

        invokeIL.Emit(OpCodes.Ldloc, effectiveArgsLocal);
        invokeIL.Emit(OpCodes.Ldc_I4_0);
        invokeIL.Emit(OpCodes.Ldarg_0);
        invokeIL.Emit(OpCodes.Ldfld, targetField);
        invokeIL.Emit(OpCodes.Stelem_Ref);

        invokeIL.Emit(OpCodes.Ldarg_1);  // source
        invokeIL.Emit(OpCodes.Ldc_I4_0); // sourceIndex
        invokeIL.Emit(OpCodes.Ldloc, effectiveArgsLocal); // dest
        invokeIL.Emit(OpCodes.Ldc_I4_1); // destIndex
        invokeIL.Emit(OpCodes.Ldarg_1);
        invokeIL.Emit(OpCodes.Ldlen);
        invokeIL.Emit(OpCodes.Conv_I4);  // length
        invokeIL.Emit(OpCodes.Call, _types.ArrayType.GetMethod("Copy", [_types.ArrayType, _types.Int32, _types.ArrayType, _types.Int32, _types.Int32])!);

        invokeIL.Emit(OpCodes.Ldnull);
        invokeIL.Emit(OpCodes.Stloc, invokeTargetLocal);
        invokeIL.Emit(OpCodes.Br, afterArgPrep);

        // Not a static with target: use args directly, target is _target
        invokeIL.MarkLabel(notStaticWithTarget);
        invokeIL.Emit(OpCodes.Ldarg_1);
        invokeIL.Emit(OpCodes.Stloc, effectiveArgsLocal);
        invokeIL.Emit(OpCodes.Ldarg_0);
        invokeIL.Emit(OpCodes.Ldfld, targetField);
        invokeIL.Emit(OpCodes.Stloc, invokeTargetLocal);

        invokeIL.MarkLabel(afterArgPrep);

        // Adjust args for rest parameters and padding/trimming
        // adjustedArgs = AdjustArgs(_method, effectiveArgs)
        var adjustedArgsLocal = invokeIL.DeclareLocal(_types.ObjectArray);
        invokeIL.Emit(OpCodes.Ldarg_0);
        invokeIL.Emit(OpCodes.Ldfld, methodField);
        invokeIL.Emit(OpCodes.Ldloc, effectiveArgsLocal);
        invokeIL.Emit(OpCodes.Call, adjustArgsMethod);
        invokeIL.Emit(OpCodes.Stloc, adjustedArgsLocal);

        // Convert args for union types before invoking
        // ConvertArgsForUnionTypes(this._method, adjustedArgs)
        invokeIL.Emit(OpCodes.Ldarg_0);
        invokeIL.Emit(OpCodes.Ldfld, methodField);
        invokeIL.Emit(OpCodes.Ldloc, adjustedArgsLocal);
        invokeIL.Emit(OpCodes.Call, convertArgsMethod);

        // Publish the un-adjusted caller args to the thread-static $Runtime._currentArguments
        // slot so flagged function bodies (those that reference JS `arguments`) can see
        // values beyond their declared arity — lodash overRest pattern from #64. No
        // try/finally restore: the prologue on the callee side reads once at entry and
        // clears immediately, and each new Invoke re-sets for its own callee, so the
        // value never needs to be restored to a prior state here.
        if (runtime.CurrentArgumentsField != null)
        {
            invokeIL.Emit(OpCodes.Ldarg_1);
            invokeIL.Emit(OpCodes.Stsfld, runtime.CurrentArgumentsField);
        }

        invokeIL.Emit(OpCodes.Ldarg_0);
        invokeIL.Emit(OpCodes.Ldfld, methodField);
        invokeIL.Emit(OpCodes.Ldloc, invokeTargetLocal);
        invokeIL.Emit(OpCodes.Ldloc, adjustedArgsLocal);
        invokeIL.Emit(OpCodes.Callvirt, _types.MethodInfo.GetMethod("Invoke", [_types.Object, _types.ObjectArray])!);
        invokeIL.Emit(OpCodes.Ret);

        // InvokeWithThis method: public object InvokeWithThis(object thisArg, object[] args)
        // This checks if the first parameter is named "__this" and prepends thisArg if so
        var invokeWithThisBuilder = typeBuilder.DefineMethod(
            "InvokeWithThis",
            MethodAttributes.Public,
            _types.Object,
            [_types.Object, _types.ObjectArray]
        );
        runtime.TSFunctionInvokeWithThis = invokeWithThisBuilder;

        var iwt = invokeWithThisBuilder.GetILGenerator();

        // Local variables
        var paramsLocal = iwt.DeclareLocal(_types.MakeArrayType(_types.ParameterInfo));
        var paramCountLocalIWT = iwt.DeclareLocal(_types.Int32);
        var expectsThisLocal = iwt.DeclareLocal(_types.Boolean);
        var effectiveArgsIWT = iwt.DeclareLocal(_types.ObjectArray);

        // params = _method.GetParameters()
        iwt.Emit(OpCodes.Ldarg_0);
        iwt.Emit(OpCodes.Ldfld, methodField);
        iwt.Emit(OpCodes.Callvirt, _types.MethodInfo.GetMethod("GetParameters")!);
        iwt.Emit(OpCodes.Stloc, paramsLocal);

        // paramCount = params.Length
        iwt.Emit(OpCodes.Ldloc, paramsLocal);
        iwt.Emit(OpCodes.Ldlen);
        iwt.Emit(OpCodes.Conv_I4);
        iwt.Emit(OpCodes.Stloc, paramCountLocalIWT);

        // expectsThis = paramCount > 0 && params[0].Name == "__this"
        var checkDoneLabel = iwt.DefineLabel();

        iwt.Emit(OpCodes.Ldc_I4_0);
        iwt.Emit(OpCodes.Stloc, expectsThisLocal);

        iwt.Emit(OpCodes.Ldloc, paramCountLocalIWT);
        iwt.Emit(OpCodes.Ldc_I4_0);
        iwt.Emit(OpCodes.Ble, checkDoneLabel);

        iwt.Emit(OpCodes.Ldloc, paramsLocal);
        iwt.Emit(OpCodes.Ldc_I4_0);
        iwt.Emit(OpCodes.Ldelem_Ref);
        iwt.Emit(OpCodes.Callvirt, _types.ParameterInfo.GetProperty("Name")!.GetGetMethod()!);
        iwt.Emit(OpCodes.Ldstr, "__this");
        iwt.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        iwt.Emit(OpCodes.Stloc, expectsThisLocal);

        iwt.MarkLabel(checkDoneLabel);

        // if (!expectsThis) { route thisArg through the thread-local; call Invoke(args) }
        // The function body has no __this parameter (e.g. `function Fn() { this.x = ... }`),
        // so the only way the user's `this` reaches it is via LocalVariableResolver.LoadThis's
        // thread-local fallback. Set + restore around the Invoke so `Fn.call(target, ...)`
        // routes property writes to target. NewOnFunction does the same set/restore around
        // its own InvokeWithThis call — both layers of save/restore are no-ops at the inner
        // layer (same value), so they nest correctly.
        var expectsThisLabel = iwt.DefineLabel();
        iwt.Emit(OpCodes.Ldloc, expectsThisLocal);
        iwt.Emit(OpCodes.Brtrue, expectsThisLabel);

        // Save current thread-local this, set to thisArg, call Invoke, restore in finally.
        var prevThisIWT = iwt.DeclareLocal(_types.Object);
        var resultIWT = iwt.DeclareLocal(_types.Object);

        iwt.Emit(OpCodes.Ldsfld, currentThisField);
        iwt.Emit(OpCodes.Stloc, prevThisIWT);

        iwt.Emit(OpCodes.Ldarg_1);
        iwt.Emit(OpCodes.Stsfld, currentThisField);

        iwt.BeginExceptionBlock();
        iwt.Emit(OpCodes.Ldarg_0);
        iwt.Emit(OpCodes.Ldarg_2);
        iwt.Emit(OpCodes.Callvirt, invokeBuilder);
        iwt.Emit(OpCodes.Stloc, resultIWT);
        iwt.BeginFinallyBlock();
        iwt.Emit(OpCodes.Ldloc, prevThisIWT);
        iwt.Emit(OpCodes.Stsfld, currentThisField);
        iwt.EndExceptionBlock();

        iwt.Emit(OpCodes.Ldloc, resultIWT);
        iwt.Emit(OpCodes.Ret);

        // expectsThis is true - prepend thisArg to args
        iwt.MarkLabel(expectsThisLabel);

        // effectiveArgs = new object[args.Length + 1]
        iwt.Emit(OpCodes.Ldarg_2);
        iwt.Emit(OpCodes.Ldlen);
        iwt.Emit(OpCodes.Conv_I4);
        iwt.Emit(OpCodes.Ldc_I4_1);
        iwt.Emit(OpCodes.Add);
        iwt.Emit(OpCodes.Newarr, _types.Object);
        iwt.Emit(OpCodes.Stloc, effectiveArgsIWT);

        // effectiveArgs[0] = thisArg
        iwt.Emit(OpCodes.Ldloc, effectiveArgsIWT);
        iwt.Emit(OpCodes.Ldc_I4_0);
        iwt.Emit(OpCodes.Ldarg_1);
        iwt.Emit(OpCodes.Stelem_Ref);

        // Array.Copy(args, 0, effectiveArgs, 1, args.Length)
        iwt.Emit(OpCodes.Ldarg_2);  // source
        iwt.Emit(OpCodes.Ldc_I4_0); // sourceIndex
        iwt.Emit(OpCodes.Ldloc, effectiveArgsIWT); // dest
        iwt.Emit(OpCodes.Ldc_I4_1); // destIndex
        iwt.Emit(OpCodes.Ldarg_2);
        iwt.Emit(OpCodes.Ldlen);
        iwt.Emit(OpCodes.Conv_I4);  // length
        iwt.Emit(OpCodes.Call, _types.ArrayType.GetMethod("Copy", [_types.ArrayType, _types.Int32, _types.ArrayType, _types.Int32, _types.Int32])!);

        // Call Invoke(effectiveArgs) and return
        iwt.Emit(OpCodes.Ldarg_0);
        iwt.Emit(OpCodes.Ldloc, effectiveArgsIWT);
        iwt.Emit(OpCodes.Callvirt, invokeBuilder);
        iwt.Emit(OpCodes.Ret);

        // ToString method
        var toStringBuilder = typeBuilder.DefineMethod(
            "ToString",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            _types.String,
            Type.EmptyTypes
        );
        var toStringIL = toStringBuilder.GetILGenerator();
        toStringIL.Emit(OpCodes.Ldstr, "[Function]");
        toStringIL.Emit(OpCodes.Ret);

        // BindThis method: public void BindThis(object thisValue)
        // Sets the 'this' field in the display class to the given value
        var bindThisBuilder = typeBuilder.DefineMethod(
            "BindThis",
            MethodAttributes.Public,
            _types.Void,
            [_types.Object]
        );
        runtime.TSFunctionBindThis = bindThisBuilder;

        var bindThisIL = bindThisBuilder.GetILGenerator();
        var noTargetLabel = bindThisIL.DefineLabel();
        var endLabel = bindThisIL.DefineLabel();
        var thisFieldLocal = bindThisIL.DeclareLocal(_types.FieldInfo);
        var targetTypeLocal = bindThisIL.DeclareLocal(_types.Type);

        // if (_target == null) return;
        bindThisIL.Emit(OpCodes.Ldarg_0);
        bindThisIL.Emit(OpCodes.Ldfld, targetField);
        bindThisIL.Emit(OpCodes.Brfalse, noTargetLabel);

        // targetType = _target.GetType();
        bindThisIL.Emit(OpCodes.Ldarg_0);
        bindThisIL.Emit(OpCodes.Ldfld, targetField);
        bindThisIL.Emit(OpCodes.Callvirt, _types.Object.GetMethod("GetType")!);
        bindThisIL.Emit(OpCodes.Stloc, targetTypeLocal);

        // Try get from cache
        // if (!_thisFieldCache.TryGetValue(targetType, out thisField))
        bindThisIL.Emit(OpCodes.Ldsfld, fieldCacheField);
        bindThisIL.Emit(OpCodes.Ldloc, targetTypeLocal);
        bindThisIL.Emit(OpCodes.Ldloca, thisFieldLocal);
        bindThisIL.Emit(OpCodes.Callvirt, fieldCacheType.GetMethod("TryGetValue", [_types.Type, _types.FieldInfo.MakeByRefType()])!);
        var cacheHitLabel = bindThisIL.DefineLabel();
        bindThisIL.Emit(OpCodes.Brtrue, cacheHitLabel);

        // Cache miss: lookup field
        // thisField = targetType.GetField("this", BindingFlags.Public | BindingFlags.Instance);
        bindThisIL.Emit(OpCodes.Ldloc, targetTypeLocal);
        bindThisIL.Emit(OpCodes.Ldstr, "this");
        bindThisIL.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.Instance));
        bindThisIL.Emit(OpCodes.Callvirt, _types.Type.GetMethod("GetField", [_types.String, _types.BindingFlags])!);
        bindThisIL.Emit(OpCodes.Stloc, thisFieldLocal);

        // Store in cache (even if null, but ConcurrentDictionary doesn't allow null values if we used that, but here we can just skip if null)
        // Actually, if null, we shouldn't cache null if we use TryGetValue. 
        // Let's simplify: if field is found, cache it. If not found, we don't cache (or cache a dummy? no need to overcomplicate).
        
        var fieldNullLabel = bindThisIL.DefineLabel();
        bindThisIL.Emit(OpCodes.Ldloc, thisFieldLocal);
        bindThisIL.Emit(OpCodes.Brfalse, fieldNullLabel);

        // Cache it
        bindThisIL.Emit(OpCodes.Ldsfld, fieldCacheField);
        bindThisIL.Emit(OpCodes.Ldloc, targetTypeLocal);
        bindThisIL.Emit(OpCodes.Ldloc, thisFieldLocal);
        bindThisIL.Emit(OpCodes.Callvirt, fieldCacheType.GetMethod("TryAdd", [_types.Type, _types.FieldInfo])!);
        bindThisIL.Emit(OpCodes.Pop); // discard bool result

        bindThisIL.MarkLabel(fieldNullLabel);
        bindThisIL.MarkLabel(cacheHitLabel);

        // if (thisField == null) return;
        bindThisIL.Emit(OpCodes.Ldloc, thisFieldLocal);
        bindThisIL.Emit(OpCodes.Brfalse, noTargetLabel);

        // thisField.SetValue(_target, thisValue);
        bindThisIL.Emit(OpCodes.Ldloc, thisFieldLocal);
        bindThisIL.Emit(OpCodes.Ldarg_0);
        bindThisIL.Emit(OpCodes.Ldfld, targetField);
        bindThisIL.Emit(OpCodes.Ldarg_1);
        bindThisIL.Emit(OpCodes.Callvirt, _types.FieldInfo.GetMethod("SetValue", [_types.Object, _types.Object])!);
        bindThisIL.Emit(OpCodes.Br, endLabel);

        bindThisIL.MarkLabel(noTargetLabel);
        bindThisIL.MarkLabel(endLabel);
        bindThisIL.Emit(OpCodes.Ret);

        // Length property: returns the number of required parameters (excluding rest, optional, and those with defaults)
        // public int get_Length()
        var lengthGetterBuilder = typeBuilder.DefineMethod(
            "get_Length",
            MethodAttributes.Public,
            _types.Int32,
            Type.EmptyTypes
        );
        runtime.TSFunctionLengthGetter = lengthGetterBuilder;

        var lengthIL = lengthGetterBuilder.GetILGenerator();
        var paramsLocalLength = lengthIL.DeclareLocal(_types.MakeArrayType(_types.ParameterInfo));
        var countLocal = lengthIL.DeclareLocal(_types.Int32);
        var indexLocalLength = lengthIL.DeclareLocal(_types.Int32);
        var paramLocalLength = lengthIL.DeclareLocal(_types.ParameterInfo);
        var lengthLoopStart = lengthIL.DefineLabel();
        var lengthLoopEnd = lengthIL.DefineLabel();
        var incrementCount = lengthIL.DefineLabel();
        var skipParam = lengthIL.DefineLabel();
        var returnZero = lengthIL.DefineLabel();
        var useCachedLength = lengthIL.DefineLabel();
        var computeLength = lengthIL.DefineLabel();

        // Check if _cachedLength >= 0 (cached value available)
        lengthIL.Emit(OpCodes.Ldarg_0);
        lengthIL.Emit(OpCodes.Ldfld, cachedLengthField);
        lengthIL.Emit(OpCodes.Ldc_I4_0);
        lengthIL.Emit(OpCodes.Bge, useCachedLength);
        lengthIL.Emit(OpCodes.Br, computeLength);

        // Return cached length
        lengthIL.MarkLabel(useCachedLength);
        lengthIL.Emit(OpCodes.Ldarg_0);
        lengthIL.Emit(OpCodes.Ldfld, cachedLengthField);
        lengthIL.Emit(OpCodes.Ret);

        // Compute length via reflection
        lengthIL.MarkLabel(computeLength);

        // Check if _method is null - if so, return 0
        lengthIL.Emit(OpCodes.Ldarg_0);
        lengthIL.Emit(OpCodes.Ldfld, methodField);
        lengthIL.Emit(OpCodes.Brfalse, returnZero);

        // params = _method.GetParameters()
        lengthIL.Emit(OpCodes.Ldarg_0);
        lengthIL.Emit(OpCodes.Ldfld, methodField);
        lengthIL.Emit(OpCodes.Callvirt, _types.MethodInfo.GetMethod("GetParameters")!);
        lengthIL.Emit(OpCodes.Stloc, paramsLocalLength);

        // Check if params is null - if so, return 0
        lengthIL.Emit(OpCodes.Ldloc, paramsLocalLength);
        lengthIL.Emit(OpCodes.Brfalse, returnZero);

        // count = 0
        lengthIL.Emit(OpCodes.Ldc_I4_0);
        lengthIL.Emit(OpCodes.Stloc, countLocal);

        // index = 0
        lengthIL.Emit(OpCodes.Ldc_I4_0);
        lengthIL.Emit(OpCodes.Stloc, indexLocalLength);

        // Loop through parameters
        lengthIL.MarkLabel(lengthLoopStart);
        // if (index >= params.Length) goto end
        lengthIL.Emit(OpCodes.Ldloc, indexLocalLength);
        lengthIL.Emit(OpCodes.Ldloc, paramsLocalLength);
        lengthIL.Emit(OpCodes.Ldlen);
        lengthIL.Emit(OpCodes.Conv_I4);
        lengthIL.Emit(OpCodes.Bge, lengthLoopEnd);

        // param = params[index]
        lengthIL.Emit(OpCodes.Ldloc, paramsLocalLength);
        lengthIL.Emit(OpCodes.Ldloc, indexLocalLength);
        lengthIL.Emit(OpCodes.Ldelem_Ref);
        lengthIL.Emit(OpCodes.Stloc, paramLocalLength);

        // Skip if param.IsOptional (has default value or is optional)
        lengthIL.Emit(OpCodes.Ldloc, paramLocalLength);
        lengthIL.Emit(OpCodes.Callvirt, _types.ParameterInfo.GetProperty("IsOptional")!.GetGetMethod()!);
        lengthIL.Emit(OpCodes.Brtrue, skipParam);

        // Skip if param type is List<object> (rest parameter)
        lengthIL.Emit(OpCodes.Ldloc, paramLocalLength);
        lengthIL.Emit(OpCodes.Callvirt, _types.ParameterInfo.GetProperty("ParameterType")!.GetGetMethod()!);
        lengthIL.Emit(OpCodes.Ldtoken, _types.ListOfObject);
        lengthIL.Emit(OpCodes.Call, _types.Type.GetMethod("GetTypeFromHandle")!);
        lengthIL.Emit(OpCodes.Call, _types.Type.GetMethod("op_Equality", [_types.Type, _types.Type])!);
        lengthIL.Emit(OpCodes.Brtrue, skipParam);

        // Skip if param name starts with "__" (internal parameters like __this).
        // ParameterInfo.Name can be null on emitted MethodBuilder methods that
        // didn't supply parameter names (e.g. MathFloorAdapter takes (object)
        // unnamed). Guard the StartsWith with a null check — null name means
        // "regular parameter, count it".
        var nameLocal = lengthIL.DeclareLocal(_types.String);
        lengthIL.Emit(OpCodes.Ldloc, paramLocalLength);
        lengthIL.Emit(OpCodes.Callvirt, _types.ParameterInfo.GetProperty("Name")!.GetGetMethod()!);
        lengthIL.Emit(OpCodes.Stloc, nameLocal);
        lengthIL.Emit(OpCodes.Ldloc, nameLocal);
        var nameNotNullLabel = lengthIL.DefineLabel();
        lengthIL.Emit(OpCodes.Brtrue, nameNotNullLabel);
        lengthIL.Emit(OpCodes.Br, incrementCount);
        lengthIL.MarkLabel(nameNotNullLabel);
        lengthIL.Emit(OpCodes.Ldloc, nameLocal);
        lengthIL.Emit(OpCodes.Ldstr, "__");
        lengthIL.Emit(OpCodes.Callvirt, _types.String.GetMethod("StartsWith", [_types.String])!);
        lengthIL.Emit(OpCodes.Brtrue, skipParam);
        lengthIL.MarkLabel(incrementCount);

        // count++
        lengthIL.Emit(OpCodes.Ldloc, countLocal);
        lengthIL.Emit(OpCodes.Ldc_I4_1);
        lengthIL.Emit(OpCodes.Add);
        lengthIL.Emit(OpCodes.Stloc, countLocal);

        lengthIL.MarkLabel(skipParam);
        // index++
        lengthIL.Emit(OpCodes.Ldloc, indexLocalLength);
        lengthIL.Emit(OpCodes.Ldc_I4_1);
        lengthIL.Emit(OpCodes.Add);
        lengthIL.Emit(OpCodes.Stloc, indexLocalLength);
        lengthIL.Emit(OpCodes.Br, lengthLoopStart);

        lengthIL.MarkLabel(lengthLoopEnd);
        lengthIL.Emit(OpCodes.Ldloc, countLocal);
        lengthIL.Emit(OpCodes.Ret);

        // Return 0 if _method or params was null
        lengthIL.MarkLabel(returnZero);
        lengthIL.Emit(OpCodes.Ldc_I4_0);
        lengthIL.Emit(OpCodes.Ret);

        // Define Length property
        var lengthProperty = typeBuilder.DefineProperty(
            "Length",
            PropertyAttributes.None,
            _types.Int32,
            Type.EmptyTypes
        );
        lengthProperty.SetGetMethod(lengthGetterBuilder);

        // Name property: returns the method name
        // public string get_Name()
        var nameGetterBuilder = typeBuilder.DefineMethod(
            "get_Name",
            MethodAttributes.Public,
            _types.String,
            Type.EmptyTypes
        );
        runtime.TSFunctionNameGetter = nameGetterBuilder;

        var nameIL = nameGetterBuilder.GetILGenerator();
        var nameReturnEmpty = nameIL.DefineLabel();
        var useCachedName = nameIL.DefineLabel();
        var computeName = nameIL.DefineLabel();

        // Check if _cachedName is not null (cached value available)
        nameIL.Emit(OpCodes.Ldarg_0);
        nameIL.Emit(OpCodes.Ldfld, cachedNameField);
        nameIL.Emit(OpCodes.Brtrue, useCachedName);
        nameIL.Emit(OpCodes.Br, computeName);

        // Return cached name
        nameIL.MarkLabel(useCachedName);
        nameIL.Emit(OpCodes.Ldarg_0);
        nameIL.Emit(OpCodes.Ldfld, cachedNameField);
        nameIL.Emit(OpCodes.Ret);

        // Compute name via reflection
        nameIL.MarkLabel(computeName);

        // Check if _method is null - if so, return ""
        nameIL.Emit(OpCodes.Ldarg_0);
        nameIL.Emit(OpCodes.Ldfld, methodField);
        nameIL.Emit(OpCodes.Brfalse, nameReturnEmpty);

        // return _method.Name
        nameIL.Emit(OpCodes.Ldarg_0);
        nameIL.Emit(OpCodes.Ldfld, methodField);
        nameIL.Emit(OpCodes.Callvirt, _types.MethodInfo.GetProperty("Name")!.GetGetMethod()!);
        nameIL.Emit(OpCodes.Ret);

        // Return "" if _method was null
        nameIL.MarkLabel(nameReturnEmpty);
        nameIL.Emit(OpCodes.Ldstr, "");
        nameIL.Emit(OpCodes.Ret);

        // Define Name property
        var nameProperty = typeBuilder.DefineProperty(
            "Name",
            PropertyAttributes.None,
            _types.String,
            Type.EmptyTypes
        );
        nameProperty.SetGetMethod(nameGetterBuilder);

        typeBuilder.CreateType();
    }

    /// <summary>
    /// Emits a private static helper method on $TSFunction to adjust arguments for rest parameters and padding/trimming.
    /// </summary>
    private MethodBuilder EmitTSFunctionAdjustArgsHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // private static object[] AdjustArgs(MethodInfo method, object[] args)
        var method = typeBuilder.DefineMethod(
            "AdjustArgs",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.ObjectArray,
            [_types.MethodInfo, _types.ObjectArray]
        );

        var il = method.GetILGenerator();

        // Local variables
        var paramsLocal = il.DeclareLocal(_types.MakeArrayType(_types.ParameterInfo));
        var paramCountLocal = il.DeclareLocal(_types.Int32);
        var argsLengthLocal = il.DeclareLocal(_types.Int32);
        var resultLocal = il.DeclareLocal(_types.ObjectArray);
        var lastParamTypeLocal = il.DeclareLocal(_types.Type);
        var restListLocal = il.DeclareLocal(_types.ListOfObject);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var copyCountLocal = il.DeclareLocal(_types.Int32);

        // params = method.GetParameters()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.MethodInfo.GetMethod("GetParameters")!);
        il.Emit(OpCodes.Stloc, paramsLocal);

        // paramCount = params.Length
        il.Emit(OpCodes.Ldloc, paramsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, paramCountLocal);

        // argsLength = args.Length
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, argsLengthLocal);

        // Labels
        var notRestParam = il.DefineLabel();
        var doReturn = il.DefineLabel();
        var exactMatch = il.DefineLabel();
        var needsPadding = il.DefineLabel();
        var needsTrimming = il.DefineLabel();
        var restLoopStart = il.DefineLabel();
        var restLoopEnd = il.DefineLabel();

        // Check if paramCount > 0
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, notRestParam);

        // lastParamType = params[paramCount - 1].ParameterType
        il.Emit(OpCodes.Ldloc, paramsLocal);
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.ParameterInfo.GetProperty("ParameterType")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, lastParamTypeLocal);

        // Check if lastParamType == typeof(List<object>) (TypeScript rest parameter)
        var notListRestParam = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, lastParamTypeLocal);
        il.Emit(OpCodes.Ldtoken, _types.ListOfObject);
        il.Emit(OpCodes.Call, _types.Type.GetMethod("GetTypeFromHandle")!);
        il.Emit(OpCodes.Call, _types.Type.GetMethod("op_Inequality", [_types.Type, _types.Type])!);
        il.Emit(OpCodes.Brtrue, notListRestParam);

        // === REST PARAMETER HANDLING (List<object>) ===
        // result = new object[paramCount]
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, resultLocal);

        // regularParamCount = paramCount - 1
        // copyCount = min(argsLength, regularParamCount)
        il.Emit(OpCodes.Ldloc, argsLengthLocal);
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Call, _types.Math.GetMethod("Min", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, copyCountLocal);

        // if (copyCount > 0) Array.Copy(args, result, copyCount)
        var skipCopy = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, copyCountLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, skipCopy);
        il.Emit(OpCodes.Ldarg_1); // source
        il.Emit(OpCodes.Ldloc, resultLocal); // dest
        il.Emit(OpCodes.Ldloc, copyCountLocal); // length
        il.Emit(OpCodes.Call, _types.ArrayType.GetMethod("Copy", [_types.ArrayType, _types.ArrayType, _types.Int32])!);
        il.MarkLabel(skipCopy);

        // restList = new List<object>()
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.ListOfObject));
        il.Emit(OpCodes.Stloc, restListLocal);

        // for (i = paramCount - 1; i < argsLength; i++) restList.Add(args[i])
        // i = paramCount - 1 (start of rest args)
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, indexLocal);

        il.MarkLabel(restLoopStart);
        // if (i >= argsLength) goto restLoopEnd
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, argsLengthLocal);
        il.Emit(OpCodes.Bge, restLoopEnd);

        // restList.Add(args[i])
        il.Emit(OpCodes.Ldloc, restListLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);

        // i++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, restLoopStart);

        il.MarkLabel(restLoopEnd);

        // result[paramCount - 1] = restList
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ldloc, restListLocal);
        il.Emit(OpCodes.Stelem_Ref);

        // return result
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // === REST PARAMETER HANDLING (object[]) ===
        // Handles methods like $EventEmitter.Emit(string, object[]) called via runtime dispatch
        il.MarkLabel(notListRestParam);
        il.Emit(OpCodes.Ldloc, lastParamTypeLocal);
        il.Emit(OpCodes.Ldtoken, _types.ObjectArray);
        il.Emit(OpCodes.Call, _types.Type.GetMethod("GetTypeFromHandle")!);
        il.Emit(OpCodes.Call, _types.Type.GetMethod("op_Inequality", [_types.Type, _types.Type])!);
        il.Emit(OpCodes.Brtrue, notRestParam);

        // Collect trailing args into object[] for the last parameter
        // result = new object[paramCount]
        var arrayRestResult = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, arrayRestResult);

        // Copy regular params: Array.Copy(args, result, min(argsLength, paramCount - 1))
        var regularCount = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, regularCount);

        var skipRegularCopy = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, regularCount);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, skipRegularCopy);
        il.Emit(OpCodes.Ldloc, argsLengthLocal);
        il.Emit(OpCodes.Ldloc, regularCount);
        il.Emit(OpCodes.Call, _types.Math.GetMethod("Min", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, copyCountLocal);
        il.Emit(OpCodes.Ldloc, copyCountLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, skipRegularCopy);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, arrayRestResult);
        il.Emit(OpCodes.Ldloc, copyCountLocal);
        il.Emit(OpCodes.Call, _types.ArrayType.GetMethod("Copy", [_types.ArrayType, _types.ArrayType, _types.Int32])!);
        il.MarkLabel(skipRegularCopy);

        // restCount = max(0, argsLength - regularCount)
        var restCountLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldloc, argsLengthLocal);
        il.Emit(OpCodes.Ldloc, regularCount);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, _types.Math.GetMethod("Max", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, restCountLocal);

        // restArray = new object[restCount]
        var restArrayLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Ldloc, restCountLocal);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, restArrayLocal);

        // if (restCount > 0) Array.Copy(args, regularCount, restArray, 0, restCount)
        var skipRestCopy = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, restCountLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, skipRestCopy);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, regularCount);
        il.Emit(OpCodes.Ldloc, restArrayLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, restCountLocal);
        il.Emit(OpCodes.Call, _types.ArrayType.GetMethod("Copy", [_types.ArrayType, _types.Int32, _types.ArrayType, _types.Int32, _types.Int32])!);
        il.MarkLabel(skipRestCopy);

        // result[paramCount - 1] = restArray
        il.Emit(OpCodes.Ldloc, arrayRestResult);
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ldloc, restArrayLocal);
        il.Emit(OpCodes.Stelem_Ref);

        // return result
        il.Emit(OpCodes.Ldloc, arrayRestResult);
        il.Emit(OpCodes.Ret);

        // === NOT A REST PARAMETER - STANDARD PADDING/TRIMMING ===
        il.MarkLabel(notRestParam);

        // if (argsLength == paramCount) return args
        il.Emit(OpCodes.Ldloc, argsLengthLocal);
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Bne_Un, needsPadding);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(needsPadding);
        // if (argsLength >= paramCount) goto needsTrimming
        il.Emit(OpCodes.Ldloc, argsLengthLocal);
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Bge, needsTrimming);

        // Pad with nulls: result = new object[paramCount]; Array.Copy(args, result, argsLength)
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, argsLengthLocal);
        il.Emit(OpCodes.Call, _types.ArrayType.GetMethod("Copy", [_types.ArrayType, _types.ArrayType, _types.Int32])!);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // Trim: result = new object[paramCount]; Array.Copy(args, result, paramCount)
        il.MarkLabel(needsTrimming);
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, paramCountLocal);
        il.Emit(OpCodes.Call, _types.ArrayType.GetMethod("Copy", [_types.ArrayType, _types.ArrayType, _types.Int32])!);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// Emits a private static helper method on $TSFunction to convert arguments for union type parameters.
    /// </summary>
    private MethodBuilder EmitTSFunctionConvertArgsHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // private static void ConvertArgsForUnionTypes(MethodInfo method, object[] args)
        // Wraps raw primitive values into union types using op_Implicit operators.
        var method = typeBuilder.DefineMethod(
            "ConvertArgsForUnionTypes",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.Void,
            [_types.MethodInfo, _types.ObjectArray]
        );

        var il = method.GetILGenerator();

        // Locals: 0=ParameterInfo[], 1=int count, 2=int i, 3=Type paramType, 4=Type argType, 5=MethodInfo implicitOp
        var paramsLocal = il.DeclareLocal(typeof(ParameterInfo[]));
        var countLocal = il.DeclareLocal(typeof(int));
        var iLocal = il.DeclareLocal(typeof(int));
        var paramTypeLocal = il.DeclareLocal(_types.Type);
        var argTypeLocal = il.DeclareLocal(_types.Type);
        var implicitOpLocal = il.DeclareLocal(_types.MethodInfo);

        // params = method.GetParameters()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, typeof(MethodBase).GetMethod("GetParameters")!);
        il.Emit(OpCodes.Stloc, paramsLocal);

        // count = Math.Min(args.Length, params.Length)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldloc, paramsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Call, typeof(Math).GetMethod("Min", [typeof(int), typeof(int)])!);
        il.Emit(OpCodes.Stloc, countLocal);

        // i = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        var loopConditionLabel = il.DefineLabel();
        var loopBodyLabel = il.DefineLabel();
        var continueLabel = il.DefineLabel();

        il.Emit(OpCodes.Br, loopConditionLabel);

        il.MarkLabel(loopBodyLabel);

        // paramType = params[i].ParameterType
        il.Emit(OpCodes.Ldloc, paramsLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, typeof(ParameterInfo).GetProperty("ParameterType")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, paramTypeLocal);

        // if (!paramType.IsValueType) continue
        il.Emit(OpCodes.Ldloc, paramTypeLocal);
        il.Emit(OpCodes.Callvirt, _types.Type.GetProperty("IsValueType")!.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, continueLabel);

        // if (!paramType.Name.StartsWith("Union_")) continue
        il.Emit(OpCodes.Ldloc, paramTypeLocal);
        il.Emit(OpCodes.Callvirt, _types.Type.GetProperty("Name")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, "Union_");
        il.Emit(OpCodes.Callvirt, typeof(string).GetMethod("StartsWith", [typeof(string)])!);
        il.Emit(OpCodes.Brfalse, continueLabel);

        // if (args[i] == null) continue
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Brfalse, continueLabel);

        // argType = args[i].GetType()
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("GetType")!);
        il.Emit(OpCodes.Stloc, argTypeLocal);

        // if (argType == paramType) continue
        il.Emit(OpCodes.Ldloc, argTypeLocal);
        il.Emit(OpCodes.Ldloc, paramTypeLocal);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Brtrue, continueLabel);

        // implicitOp = paramType.GetMethod("op_Implicit", new Type[] { argType })
        il.Emit(OpCodes.Ldloc, paramTypeLocal);
        il.Emit(OpCodes.Ldstr, "op_Implicit");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, argTypeLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.Type.GetMethod("GetMethod", [typeof(string), typeof(Type[])])!);
        il.Emit(OpCodes.Stloc, implicitOpLocal);

        // if (implicitOp == null) continue
        il.Emit(OpCodes.Ldloc, implicitOpLocal);
        il.Emit(OpCodes.Brfalse, continueLabel);

        // args[i] = implicitOp.Invoke(null, new object[] { args[i] })
        il.Emit(OpCodes.Ldarg_1);   // args array (for stelem later)
        il.Emit(OpCodes.Ldloc, iLocal);  // index (for stelem later)
        il.Emit(OpCodes.Ldloc, implicitOpLocal);
        il.Emit(OpCodes.Ldnull);     // target (null for static)
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, typeof(MethodBase).GetMethod("Invoke", [typeof(object), typeof(object[])])!);
        il.Emit(OpCodes.Stelem_Ref); // args[i] = result

        il.MarkLabel(continueLabel);

        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(loopConditionLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, countLocal);
        il.Emit(OpCodes.Blt, loopBodyLabel);

        il.Emit(OpCodes.Ret);

        return method;
    }
}
