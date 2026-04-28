using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits the $Array class for standalone array support.
/// NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSArray.
/// </summary>
/// <remarks>
/// Stage E.2 M1 (infrastructure, no behavior change): the class now carries the
/// sparse/hole-aware fields and long-indexed methods needed by later milestones.
/// Existing callers (IL emitters that invoke <see cref="EmittedRuntime.TSArrayGet"/>,
/// <see cref="EmittedRuntime.TSArraySet"/>, <see cref="EmittedRuntime.TSArrayElementsGetter"/>)
/// continue to observe pre-Stage-E semantics: constructor populates <c>_dense</c>
/// from the input list, <c>_length == _dense.Count</c>, <c>_sparse == null</c>,
/// and legacy int-indexed Get/Set bounds-check against <c>_dense.Count</c>. The
/// long-indexed API is present and correct but unused until M2 wires runtime
/// dispatch through it.
/// </remarks>
public partial class RuntimeEmitter
{
    // Fields. Since $Array now *inherits* from List<object?>, the dense
    // backing IS the instance itself — no separate field. _sparse / _length
    // are new (Stage E.2); frozen/sealed match pre-refactor layout.
    private FieldBuilder _tsArraySparseField = null!;
    private FieldBuilder _tsArrayLengthField = null!;
    private FieldBuilder _tsArrayIsFrozenField = null!;
    private FieldBuilder _tsArrayIsSealedField = null!;

    // Cached generic type for the sparse dictionary backing.
    private Type _tsArraySparseType = null!;

    private MethodInfo? _tsArraySparseTryGetValue;
    private MethodInfo? _tsArraySparseCountGetter;
    private MethodInfo? _tsArraySparseRemove;
    private MethodInfo? _tsArraySparseSetItem;
    private MethodInfo? _tsArrayListCountGetter;
    private MethodInfo? _tsArrayListAdd;
    private MethodInfo? _tsArrayListRemoveAt;
    private MethodInfo? _tsArrayListGetItem;
    private MethodInfo? _tsArrayListSetItem;

    private void InitTSArrayMethodCache()
    {
        _tsArraySparseTryGetValue = _tsArraySparseType.GetMethod("TryGetValue", [_types.UInt32, _types.Object.MakeByRefType()])!;
        _tsArraySparseCountGetter = _types.GetProperty(_tsArraySparseType, "Count").GetGetMethod()!;
        _tsArraySparseRemove = _tsArraySparseType.GetMethod("Remove", [_types.UInt32])!;
        _tsArraySparseSetItem = _tsArraySparseType.GetMethod("set_Item", [_types.UInt32, _types.Object])!;
        _tsArrayListCountGetter = _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!;
        _tsArrayListAdd = _types.ListOfObject.GetMethod("Add", [_types.Object])!;
        _tsArrayListRemoveAt = _types.ListOfObject.GetMethod("RemoveAt", [_types.Int32])!;
        _tsArrayListGetItem = _types.ListOfObject.GetMethod("get_Item", [_types.Int32])!;
        _tsArrayListSetItem = _types.ListOfObject.GetMethod("set_Item", [_types.Int32, _types.Object])!;
    }

    private void EmitTSArrayClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        _tsArraySparseType = _types.MakeGenericType(_types.DictionaryOpen, _types.UInt32, _types.Object);
        InitTSArrayMethodCache();

        // public class $Array : List<object?>
        //
        // Design rationale (Stage E.2 M2): $Array *inherits* from List<object?>
        // rather than owning one as a field. This matters because ~48 IL
        // emission sites throughout Compilation/ still do
        //   Isinst List<object?> → Castclass List<object?>
        // on JS array values. Inheritance makes those sites pass-through
        // automatically — the emitted `$Array` IS a List<object?> at the CLR
        // type-check level. M3's runtime-dispatch cleanup can then prefer the
        // long-indexed API incrementally, without each intermediate commit
        // breaking 100+ tests.
        //
        // The interpreter's SharpTSArray uses composition (owns a Deque<object?>)
        // because it's C# code and C# callers use its public API directly.
        // The emitted class has no such luxury — legacy IL emitters expect
        // List<object?> semantics and will keep expecting them until M3.
        //
        // Dense prefix = the inherited List's own storage (indices [0, Count)).
        // Sparse tail and true _length live in added fields. Past Stage-E,
        // base Count may diverge from _length once sparse writes occur; built-
        // in emitters that care use the explicit getters (Length / LongLength /
        // HasIndex) rather than falling through to base Count.
        var typeBuilder = moduleBuilder.DefineType(
            "$Array",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            _types.ListOfObject
        );
        runtime.TSArrayType = typeBuilder;

        // Sparse-aware fields. The dense backing is the base-List's own storage;
        // `_dense` is kept as an alias field for code clarity but points at `this`.
        _tsArraySparseField = typeBuilder.DefineField("_sparse", _tsArraySparseType, FieldAttributes.Private);
        _tsArrayLengthField = typeBuilder.DefineField("_length", _types.Int64, FieldAttributes.Private);
        _tsArrayIsFrozenField = typeBuilder.DefineField("_isFrozen", _types.Boolean, FieldAttributes.Private);
        _tsArrayIsSealedField = typeBuilder.DefineField("_isSealed", _types.Boolean, FieldAttributes.Private);
        // _tsArrayDenseField retired; every previous read-of-_dense is replaced
        // by a no-op (receiver is already the List via inheritance).

        EmitTSArrayConstructor(typeBuilder, runtime);

        // Elements returns `this` (the inherited List<object?>). In practice
        // callers that cared about the sparse tail have migrated to the
        // long-indexed accessors (Get/Set/HasIndex/LongLength); Elements
        // remains for (a) call sites whose semantics are "get the dense
        // prefix as a List" (e.g. GroupBy, DNS results feed the list into
        // interop paths), and (b) as a semantic marker in emitted IL vs.
        // an opaque Castclass. Kept post-Stage E as a shallow forward-
        // compatible helper; safe because the dense prefix IS the instance.
        EmitTSArrayElementsProperty(typeBuilder, runtime);

        // Private helpers emitted first — all public getters/methods below
        // call SyncLength to absorb mutations that went through inherited
        // List<T>.Add / Insert / RemoveAt (which bump base.Count without
        // touching our _length field). Without this sync, e.g. GroupBy's
        // `new $Array(list).Add(x)` path sees stale _length and reads return
        // undefined.
        var syncLength = EmitTSArraySyncLength(typeBuilder, runtime);
        var materializeDense = EmitTSArrayMaterializeDense(typeBuilder, runtime);
        var tryCollapseSparse = EmitTSArrayTryCollapseSparse(typeBuilder, runtime);
        var getCore = EmitTSArrayGetCore(typeBuilder, runtime);
        var setCore = EmitTSArraySetCore(typeBuilder, runtime);
        var setCoreWithExtend = EmitTSArraySetCoreWithExtend(typeBuilder, runtime, setCore);
        _ = materializeDense;  // reserved for M5 mutator emitters

        EmitTSArrayLongLengthProperty(typeBuilder, runtime, syncLength);
        EmitTSArrayLengthProperty(typeBuilder, runtime, syncLength);
        // No custom Count property — inherited from List<object?>. The public
        // Count + interface ICollection<T>.Count / IReadOnlyCollection<T>.Count
        // all come from the base class. `arr.Count == arr._dense.Count`, which
        // matches pre-refactor semantics; callers wanting the full sparse
        // length use LongLength.

        EmitTSArrayIsFrozenProperty(typeBuilder, runtime);
        EmitTSArrayIsSealedProperty(typeBuilder, runtime);

        EmitTSArrayFreeze(typeBuilder, runtime);
        EmitTSArraySeal(typeBuilder, runtime);

        // Legacy int-indexed methods (pre-Stage-E surface; unchanged semantics).
        EmitTSArrayGet(typeBuilder, runtime);
        EmitTSArraySet(typeBuilder, runtime);
        EmitTSArraySetStrict(typeBuilder, runtime);

        EmitTSArrayHasIndex(typeBuilder, runtime, syncLength);
        EmitTSArrayGetRaw(typeBuilder, runtime, getCore, syncLength);
        EmitTSArrayGetLong(typeBuilder, runtime, getCore, syncLength);
        EmitTSArraySetLong(typeBuilder, runtime, setCoreWithExtend, syncLength);
        EmitTSArraySetStrictLong(typeBuilder, runtime, setCoreWithExtend, syncLength);
        EmitTSArraySetLength(typeBuilder, runtime, tryCollapseSparse, syncLength);
        EmitTSArrayDeleteAt(typeBuilder, runtime, syncLength);

        EmitTSArrayToString(typeBuilder, runtime);
        // IList<object?> impl is inherited from List<object?> — no explicit
        // bridges needed (was the old composition-based approach).

        typeBuilder.CreateType();
    }

    private void EmitTSArrayConstructor(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.ListOfObject]
        );
        runtime.TSArrayCtor = ctor;

        var il = ctor.GetILGenerator();

        // base(IEnumerable<object?>) — copies the input list's items into our
        // own List<object?> storage. Per-element copy is O(N) but callers
        // build a fresh list per $Array allocation, so throughput is unchanged.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.GetConstructor(_types.ListOfObject, _types.IEnumerableOfObject));

        // _length = (long)this.Count  (read AFTER base ctor has populated it).
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Stfld, _tsArrayLengthField);

        il.Emit(OpCodes.Ret);
    }

    private void EmitTSArrayElementsProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var prop = typeBuilder.DefineProperty("Elements", PropertyAttributes.None, _types.ListOfObject, null);
        var getter = typeBuilder.DefineMethod(
            "get_Elements",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            _types.ListOfObject, Type.EmptyTypes);
        runtime.TSArrayElementsGetter = getter;

        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);

        prop.SetGetMethod(getter);
    }

    private void EmitTSArrayLongLengthProperty(TypeBuilder typeBuilder, EmittedRuntime runtime, MethodBuilder syncLength)
    {
        var prop = typeBuilder.DefineProperty("LongLength", PropertyAttributes.None, _types.Int64, null);
        var getter = typeBuilder.DefineMethod(
            "get_LongLength",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            _types.Int64, Type.EmptyTypes);
        runtime.TSArrayLongLengthGetter = getter;

        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, syncLength);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Ret);

        prop.SetGetMethod(getter);
    }

    private void EmitTSArrayLengthProperty(TypeBuilder typeBuilder, EmittedRuntime runtime, MethodBuilder syncLength)
    {
        var prop = typeBuilder.DefineProperty("Length", PropertyAttributes.None, _types.Int32, null);
        var getter = typeBuilder.DefineMethod(
            "get_Length",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            _types.Int32, Type.EmptyTypes);
        runtime.TSArrayLengthGetter = getter;

        var il = getter.GetILGenerator();
        var clampLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, syncLength);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Ldc_I8, (long)int.MaxValue);
        il.Emit(OpCodes.Bgt, clampLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(clampLabel);
        il.Emit(OpCodes.Ldc_I4, int.MaxValue);
        il.Emit(OpCodes.Ret);

        prop.SetGetMethod(getter);
    }

    private void EmitTSArrayIsFrozenProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var prop = typeBuilder.DefineProperty("IsFrozen", PropertyAttributes.None, _types.Boolean, null);
        var getter = typeBuilder.DefineMethod(
            "get_IsFrozen",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            _types.Boolean, Type.EmptyTypes);
        runtime.TSArrayIsFrozenGetter = getter;

        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayIsFrozenField);
        il.Emit(OpCodes.Ret);

        prop.SetGetMethod(getter);
    }

    private void EmitTSArrayIsSealedProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var prop = typeBuilder.DefineProperty("IsSealed", PropertyAttributes.None, _types.Boolean, null);
        var getter = typeBuilder.DefineMethod(
            "get_IsSealed",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            _types.Boolean, Type.EmptyTypes);
        runtime.TSArrayIsSealedGetter = getter;

        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayIsSealedField);
        il.Emit(OpCodes.Ret);

        prop.SetGetMethod(getter);
    }

    private void EmitTSArrayFreeze(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("Freeze", MethodAttributes.Public, _types.Void, Type.EmptyTypes);
        runtime.TSArrayFreeze = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsArrayIsFrozenField);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsArrayIsSealedField);

        il.Emit(OpCodes.Ret);
    }

    private void EmitTSArraySeal(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("Seal", MethodAttributes.Public, _types.Void, Type.EmptyTypes);
        runtime.TSArraySeal = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _tsArrayIsSealedField);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSArrayGet(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("Get", MethodAttributes.Public, _types.Object, [_types.Int32]);
        runtime.TSArrayGet = method;

        var il = method.GetILGenerator();
        var throwLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, throwLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Bge, throwLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _tsArrayListGetItem!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "Index out of bounds.");
        il.Emit(OpCodes.Newobj, _types.Exception.GetConstructor([_types.String])!);
        il.Emit(OpCodes.Throw);
    }

    private void EmitTSArraySet(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("Set", MethodAttributes.Public, _types.Void, [_types.Int32, _types.Object]);
        runtime.TSArraySet = method;

        var il = method.GetILGenerator();
        var frozenLabel = il.DefineLabel();
        var throwLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayIsFrozenField);
        il.Emit(OpCodes.Brtrue, frozenLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, throwLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Bge, throwLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _tsArrayListSetItem!);

        il.MarkLabel(frozenLabel);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "Index out of bounds.");
        il.Emit(OpCodes.Newobj, _types.Exception.GetConstructor([_types.String])!);
        il.Emit(OpCodes.Throw);
    }

    private void EmitTSArraySetStrict(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SetStrict", MethodAttributes.Public, _types.Void,
            [_types.Int32, _types.Object, _types.Boolean]);
        runtime.TSArraySetStrict = method;

        var il = method.GetILGenerator();
        var notFrozenLabel = il.DefineLabel();
        var frozenReturnLabel = il.DefineLabel();
        var throwBoundsLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayIsFrozenField);
        il.Emit(OpCodes.Brfalse, notFrozenLabel);

        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Brfalse, frozenReturnLabel);

        EmitInlineThrowErrorInline(il, "TypeError: Cannot assign to read only property of array", runtime.TSTypeErrorCtor);

        il.MarkLabel(frozenReturnLabel);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notFrozenLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, throwBoundsLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Bge, throwBoundsLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _tsArrayListSetItem!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(throwBoundsLabel);
        il.Emit(OpCodes.Ldstr, "Index out of bounds.");
        il.Emit(OpCodes.Newobj, _types.Exception.GetConstructor([_types.String])!);
        il.Emit(OpCodes.Throw);
    }

    // -----------------------------------------------------------------------
    // Long-indexed / sparse-aware API — mirrors SharpTSArray semantics.
    // -----------------------------------------------------------------------

    /// <summary>
    /// <c>private void SyncLength()</c> — reconciles <c>_length</c> with the
    /// inherited <c>List&lt;T&gt;.Count</c> whenever the array is purely dense.
    /// Needed because external IL emitters push/pop via base-class List methods
    /// (Add, Insert, RemoveAt), which don't touch our <c>_length</c> field.
    /// Every public entry point calls this first so reads see a consistent
    /// view.
    /// </summary>
    private MethodBuilder EmitTSArraySyncLength(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("SyncLength", MethodAttributes.Private, _types.Void, Type.EmptyTypes);
        var il = method.GetILGenerator();
        var doneLabel = il.DefineLabel();

        // if (_sparse != null) return;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Brtrue, doneLabel);

        // _length = (long)base.Count;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Stfld, _tsArrayLengthField);

        il.MarkLabel(doneLabel);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// <c>private void MaterializeDense()</c> — flattens the sparse tail into
    /// <c>_dense</c>. Throws RangeError if length > int.MaxValue.
    /// </summary>
    private MethodBuilder EmitTSArrayMaterializeDense(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("MaterializeDense", MethodAttributes.Private, _types.Void, Type.EmptyTypes);

        var il = method.GetILGenerator();
        var sparseBranch = il.DefineLabel();
        var throwRangeLabel = il.DefineLabel();
        var loopHead = il.DefineLabel();
        var loopExit = il.DefineLabel();
        var padHoleLabel = il.DefineLabel();
        var afterAddLabel = il.DefineLabel();

        // if (_sparse == null) return;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Brtrue, sparseBranch);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(sparseBranch);

        // if (_length > int.MaxValue) throw RangeError
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Ldc_I8, (long)int.MaxValue);
        il.Emit(OpCodes.Bgt, throwRangeLabel);

        var idxLocal = il.DeclareLocal(_types.Int32);
        var sparseValueLocal = il.DeclareLocal(_types.Object);

        il.MarkLabel(loopHead);
        // while (_dense.Count < _length)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Bge, loopExit);

        // int i = _dense.Count;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Stloc, idxLocal);

        // if (_sparse.TryGetValue((uint)i, out v)) _dense.Add(v); else _dense.Add($ArrayHole.Instance);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldloca, sparseValueLocal);
        il.Emit(OpCodes.Callvirt, _tsArraySparseTryGetValue!);
        il.Emit(OpCodes.Brfalse, padHoleLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, sparseValueLocal);
        il.Emit(OpCodes.Callvirt, _tsArrayListAdd!);
        il.Emit(OpCodes.Br, afterAddLabel);

        il.MarkLabel(padHoleLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, runtime.ArrayHoleInstance);
        il.Emit(OpCodes.Callvirt, _tsArrayListAdd!);

        il.MarkLabel(afterAddLabel);
        il.Emit(OpCodes.Br, loopHead);

        il.MarkLabel(loopExit);

        // _sparse = null;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stfld, _tsArraySparseField);
        il.Emit(OpCodes.Ret);

        EmitInlineThrowError(il, runtime, "Array operation requires materializing a sparse array whose length exceeds int.MaxValue.", runtime.TSRangeErrorCtor, throwRangeLabel);

        return method;
    }

    /// <summary>
    /// <c>private void TryCollapseSparse()</c> — drops the sparse dict when
    /// empty OR fully covered by the dense prefix.
    /// </summary>
    private MethodBuilder EmitTSArrayTryCollapseSparse(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("TryCollapseSparse", MethodAttributes.Private, _types.Void, Type.EmptyTypes);

        var il = method.GetILGenerator();
        var collapseLabel = il.DefineLabel();
        var doneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Brfalse, doneLabel);

        // if (_sparse.Count == 0) collapse
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Callvirt, _tsArraySparseCountGetter!);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Beq, collapseLabel);

        // else if (_length <= (long)_dense.Count) collapse
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Bgt, doneLabel);

        il.MarkLabel(collapseLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stfld, _tsArraySparseField);

        il.MarkLabel(doneLabel);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// <c>private object? GetCore(long index)</c> — raw read, returns
    /// <c>$ArrayHole.Instance</c> for holes. Callers ensure index is in range
    /// (<c>0 &lt;= index &lt; _length</c>).
    /// </summary>
    private MethodBuilder EmitTSArrayGetCore(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("GetCore", MethodAttributes.Private, _types.Object, [_types.Int64]);

        var il = method.GetILGenerator();
        var sparsePathLabel = il.DefineLabel();
        var returnHoleLabel = il.DefineLabel();
        var lookupLabel = il.DefineLabel();

        // if (_sparse != null && index >= _dense.Count) goto sparsePath; else goto lookup;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Brfalse, lookupLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Bge, sparsePathLabel);

        il.MarkLabel(lookupLabel);
        // return _dense[(int)index];
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Callvirt, _tsArrayListGetItem!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(sparsePathLabel);
        // if (index > uint.MaxValue) return $ArrayHole.Instance;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I8, (long)uint.MaxValue);
        il.Emit(OpCodes.Bgt, returnHoleLabel);

        var vLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Ldloca, vLocal);
        il.Emit(OpCodes.Callvirt, _tsArraySparseTryGetValue!);
        il.Emit(OpCodes.Brfalse, returnHoleLabel);
        il.Emit(OpCodes.Ldloc, vLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnHoleLabel);
        il.Emit(OpCodes.Ldsfld, runtime.ArrayHoleInstance);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// <c>private void SetCore(long index, object? value)</c> — in-place write,
    /// does NOT extend length. Caller ensures index is in a writable slot.
    /// </summary>
    private MethodBuilder EmitTSArraySetCore(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("SetCore", MethodAttributes.Private, _types.Void,
            [_types.Int64, _types.Object]);

        var il = method.GetILGenerator();
        var sparseLabel = il.DefineLabel();
        var denseLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Brfalse, denseLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Bge, sparseLabel);

        il.MarkLabel(denseLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _tsArrayListSetItem!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(sparseLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _tsArraySparseSetItem!);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// <c>private void SetCoreWithExtend(long index, object? value)</c> —
    /// storage-aware writer: dense fast path, sparse transition past
    /// <see cref="SharpTS.Runtime.Types.SharpTSArray"/>'s SparseThreshold,
    /// and writes within an already-sparse array.
    /// </summary>
    private MethodBuilder EmitTSArraySetCoreWithExtend(
        TypeBuilder typeBuilder, EmittedRuntime runtime, MethodBuilder setCore)
    {
        var method = typeBuilder.DefineMethod("SetCoreWithExtend", MethodAttributes.Private, _types.Void,
            [_types.Int64, _types.Object]);

        var il = method.GetILGenerator();
        var denseEntryLabel = il.DefineLabel();
        var sparseWriteReturn = il.DefineLabel();
        var skipLenUpdate = il.DefineLabel();
        var padDenseLabel = il.DefineLabel();
        var transitionSparseLabel = il.DefineLabel();
        var padLoopHead = il.DefineLabel();
        var padLoopExit = il.DefineLabel();

        // if (_sparse != null) { SetCore(index, value); if (index >= _length) _length = index + 1; return; }
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Brfalse, denseEntryLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, setCore);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Blt, sparseWriteReturn);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stfld, _tsArrayLengthField);

        il.MarkLabel(sparseWriteReturn);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(denseEntryLabel);

        // Pure-dense path.
        // if (index < _length) { _dense[(int)index] = value; return; }
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Bge, padDenseLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _tsArrayListSetItem!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(padDenseLabel);

        // long growth = index + 1 - _length;
        // if (growth <= SparseThreshold && index + 1 <= int.MaxValue) { pad dense; return; }
        // else transition sparse.
        var growthLocal = il.DeclareLocal(_types.Int64);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, growthLocal);

        // growth > SparseThreshold → sparse
        il.Emit(OpCodes.Ldloc, growthLocal);
        il.Emit(OpCodes.Ldc_I8, (long)TSArraySparseThreshold);
        il.Emit(OpCodes.Bgt, transitionSparseLabel);

        // index + 1 > int.MaxValue → sparse
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I8, (long)int.MaxValue);
        il.Emit(OpCodes.Bgt, transitionSparseLabel);

        // Pad with $ArrayHole.Instance, then write value at index.
        // while (_dense.Count <= (int)index) _dense.Add($ArrayHole.Instance);
        il.MarkLabel(padLoopHead);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Bgt, padLoopExit);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, runtime.ArrayHoleInstance);
        il.Emit(OpCodes.Callvirt, _tsArrayListAdd!);
        il.Emit(OpCodes.Br, padLoopHead);

        il.MarkLabel(padLoopExit);

        // _dense[(int)index] = value;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _tsArrayListSetItem!);

        // _length = (long)_dense.Count;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Stfld, _tsArrayLengthField);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(transitionSparseLabel);
        // _sparse = new Dictionary<uint,object?> { [(uint)index] = value };
        // _length = index + 1;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, _tsArraySparseType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stfld, _tsArraySparseField);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _tsArraySparseSetItem!);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stfld, _tsArrayLengthField);
        il.Emit(OpCodes.Ret);

        _ = skipLenUpdate;
        return method;
    }

    /// <summary>
    /// <c>public bool HasIndex(long index)</c> — ECMA-262 HasProperty for
    /// numeric indices. False for holes and out-of-range indices.
    /// </summary>
    private void EmitTSArrayHasIndex(TypeBuilder typeBuilder, EmittedRuntime runtime, MethodBuilder syncLength)
    {
        var method = typeBuilder.DefineMethod("HasIndex", MethodAttributes.Public, _types.Boolean, [_types.Int64]);
        runtime.TSArrayHasIndex = method;

        var il = method.GetILGenerator();
        var returnFalseLabel = il.DefineLabel();
        var sparseBranchLabel = il.DefineLabel();
        var checkDenseHoleLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, syncLength);

        // if ((ulong)index >= (ulong)_length) return false;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Blt, returnFalseLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Bge, returnFalseLabel);

        // if (_sparse == null || index < _dense.Count) -> check dense hole
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Brfalse, checkDenseHoleLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Bge, sparseBranchLabel);

        il.MarkLabel(checkDenseHoleLabel);
        // return !(_dense[(int)index] is $ArrayHole)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Callvirt, _tsArrayListGetItem!);
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ceq);   // 1 if result was null → not a hole → true
        il.Emit(OpCodes.Ret);

        il.MarkLabel(sparseBranchLabel);
        // if (index > uint.MaxValue) return false;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I8, (long)uint.MaxValue);
        il.Emit(OpCodes.Bgt, returnFalseLabel);

        // return _sparse.ContainsKey((uint)index);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Callvirt, _tsArraySparseType.GetMethod("ContainsKey", [_types.UInt32])!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnFalseLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// <c>public object? GetRaw(long index)</c> — returns <c>$ArrayHole.Instance</c>
    /// for holes (in-range but not written); <c>$Undefined.Instance</c> for OOB.
    /// Built-ins that distinguish holes from explicit undefined use this.
    /// </summary>
    private void EmitTSArrayGetRaw(TypeBuilder typeBuilder, EmittedRuntime runtime, MethodBuilder getCore, MethodBuilder syncLength)
    {
        var method = typeBuilder.DefineMethod("GetRaw", MethodAttributes.Public, _types.Object, [_types.Int64]);
        runtime.TSArrayGetRaw = method;

        var il = method.GetILGenerator();
        var oobLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, syncLength);

        // if ((ulong)index >= (ulong)_length) return $Undefined.Instance;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Blt, oobLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Bge, oobLabel);

        // return GetCore(index);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, getCore);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(oobLabel);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// <c>public object? Get(long index)</c> — JS-semantic read: OOB and holes
    /// both return <c>$Undefined.Instance</c>.
    /// </summary>
    private void EmitTSArrayGetLong(TypeBuilder typeBuilder, EmittedRuntime runtime, MethodBuilder getCore, MethodBuilder syncLength)
    {
        var method = typeBuilder.DefineMethod("Get", MethodAttributes.Public, _types.Object, [_types.Int64]);
        runtime.TSArrayGetLong = method;

        var il = method.GetILGenerator();
        var oobLabel = il.DefineLabel();
        var notHoleLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, syncLength);

        // if ((ulong)index >= (ulong)_length) return $Undefined.Instance;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Blt, oobLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Bge, oobLabel);

        // var v = GetCore(index); if (v is $ArrayHole) return $Undefined.Instance; return v;
        var vLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, getCore);
        il.Emit(OpCodes.Stloc, vLocal);

        il.Emit(OpCodes.Ldloc, vLocal);
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        il.Emit(OpCodes.Brfalse, notHoleLabel);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notHoleLabel);
        il.Emit(OpCodes.Ldloc, vLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(oobLabel);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// <c>public void Set(long index, object? value)</c> — JS-semantic write.
    /// Extends the array; intermediate positions become holes. Transitions to
    /// sparse past SparseThreshold. Throws RangeError for negative indices and
    /// indices beyond the ECMA-262 uint32 maximum.
    /// </summary>
    private void EmitTSArraySetLong(TypeBuilder typeBuilder, EmittedRuntime runtime, MethodBuilder setCoreWithExtend, MethodBuilder syncLength)
    {
        var method = typeBuilder.DefineMethod("Set", MethodAttributes.Public, _types.Void,
            [_types.Int64, _types.Object]);
        runtime.TSArraySetLong = method;

        var il = method.GetILGenerator();
        var frozenReturnLabel = il.DefineLabel();
        var negThrowLabel = il.DefineLabel();
        var maxThrowLabel = il.DefineLabel();
        var notExtensibleReturnLabel = il.DefineLabel();
        var indexInRangeLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, syncLength);

        // if (_isFrozen) return;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayIsFrozenField);
        il.Emit(OpCodes.Brtrue, frozenReturnLabel);

        // if (index < 0) throw RangeError;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Blt, negThrowLabel);

        // if (index > MaxWriteIndex) throw RangeError;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I8, TSArrayMaxWriteIndex);
        il.Emit(OpCodes.Bgt, maxThrowLabel);

        // SetCoreWithExtend(index, value);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, setCoreWithExtend);

        il.MarkLabel(frozenReturnLabel);
        il.MarkLabel(notExtensibleReturnLabel);
        il.MarkLabel(indexInRangeLabel);
        il.Emit(OpCodes.Ret);

        EmitInlineThrowError(il, runtime, "Index out of bounds.", runtime.TSRangeErrorCtor, negThrowLabel);
        EmitInlineThrowError(il, runtime, "Array index exceeds ECMA-262 uint32 maximum.", runtime.TSRangeErrorCtor, maxThrowLabel);
    }

    /// <summary>
    /// <c>public void SetStrict(long index, object? value, bool strictMode)</c> —
    /// like <see cref="EmitTSArraySetLong"/> but throws TypeError for writes to
    /// frozen arrays in strict mode rather than silently no-op'ing.
    /// </summary>
    private void EmitTSArraySetStrictLong(TypeBuilder typeBuilder, EmittedRuntime runtime, MethodBuilder setCoreWithExtend, MethodBuilder syncLength)
    {
        var method = typeBuilder.DefineMethod("SetStrict", MethodAttributes.Public, _types.Void,
            [_types.Int64, _types.Object, _types.Boolean]);
        runtime.TSArraySetStrictLong = method;

        var il = method.GetILGenerator();
        var notFrozenLabel = il.DefineLabel();
        var frozenReturnLabel = il.DefineLabel();
        var negThrowLabel = il.DefineLabel();
        var maxThrowLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, syncLength);

        // if (_isFrozen)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayIsFrozenField);
        il.Emit(OpCodes.Brfalse, notFrozenLabel);

        // if (!strictMode) return;
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Brfalse, frozenReturnLabel);

        // throw TypeError
        EmitInlineThrowErrorInline(il, "TypeError: Cannot assign to read only property of array", runtime.TSTypeErrorCtor);

        il.MarkLabel(frozenReturnLabel);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notFrozenLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Blt, negThrowLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I8, TSArrayMaxWriteIndex);
        il.Emit(OpCodes.Bgt, maxThrowLabel);

        // SetCoreWithExtend(index, value); return;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, setCoreWithExtend);
        il.Emit(OpCodes.Ret);

        EmitInlineThrowError(il, runtime, "Index out of bounds.", runtime.TSRangeErrorCtor, negThrowLabel);
        EmitInlineThrowError(il, runtime, "Array index exceeds ECMA-262 uint32 maximum.", runtime.TSRangeErrorCtor, maxThrowLabel);
    }

    /// <summary>
    /// <c>public void SetLength(long newLength)</c> — implements <c>arr.length = N</c>.
    /// Truncates or extends with holes. Respects frozen state.
    /// </summary>
    private void EmitTSArraySetLength(TypeBuilder typeBuilder, EmittedRuntime runtime, MethodBuilder tryCollapseSparse, MethodBuilder syncLength)
    {
        var method = typeBuilder.DefineMethod("SetLength", MethodAttributes.Public, _types.Void, [_types.Int64]);
        runtime.TSArraySetLength = method;

        var il = method.GetILGenerator();
        var negThrowLabel = il.DefineLabel();
        var tooBigThrowLabel = il.DefineLabel();
        var notFrozenLabel = il.DefineLabel();
        var lengthChangedLabel = il.DefineLabel();
        var extendPathLabel = il.DefineLabel();
        var padHolesLabel = il.DefineLabel();
        var sparseExtendLabel = il.DefineLabel();
        var padLoopHead = il.DefineLabel();
        var padLoopExit = il.DefineLabel();
        var truncateSparseDoneLabel = il.DefineLabel();
        var truncateDenseLoopHead = il.DefineLabel();
        var truncateDenseLoopExit = il.DefineLabel();
        var truncateForLoopHead = il.DefineLabel();
        var truncateForLoopBody = il.DefineLabel();
        var truncateForLoopNext = il.DefineLabel();
        var truncateForLoopExit = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, syncLength);

        // if (_isFrozen) return;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayIsFrozenField);
        il.Emit(OpCodes.Brfalse, notFrozenLabel);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notFrozenLabel);

        // if (newLength < 0) throw;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Blt, negThrowLabel);

        // if (newLength > MaxLength) throw;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I8, TSArrayMaxLength);
        il.Emit(OpCodes.Bgt, tooBigThrowLabel);

        // if (newLength == _length) return;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Bne_Un, lengthChangedLabel);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(lengthChangedLabel);

        // if (newLength >= _length) goto extendPath;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Bge, extendPathLabel);

        // Truncate path.
        // if (_sparse != null) remove keys >= newLength.
        var sparseCheckSkipLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Brfalse, sparseCheckSkipLabel);

        // Snapshot keys into a List<uint> (can't remove while iterating Dictionary.Keys),
        // then index it with a plain for-loop to avoid struct-enumerator complexity.
        var keysEnumerableType = _types.MakeGenericType(_types.IEnumerableOpen, _types.UInt32);
        var keysCollectionGetter = _tsArraySparseType.GetProperty("Keys")!.GetGetMethod()!;
        var listUIntType = _types.MakeGenericType(_types.ListOpen, _types.UInt32);
        var listUIntCtorFromEnum = listUIntType.GetConstructor([keysEnumerableType])!;
        var listUIntGetCount = listUIntType.GetProperty("Count")!.GetGetMethod()!;
        var listUIntGetItem = listUIntType.GetMethod("get_Item", [_types.Int32])!;

        var keysListLocal = il.DeclareLocal(listUIntType);
        var iLocal = il.DeclareLocal(_types.Int32);
        var countLocal = il.DeclareLocal(_types.Int32);
        var keyLocal = il.DeclareLocal(_types.UInt32);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Callvirt, keysCollectionGetter);
        il.Emit(OpCodes.Newobj, listUIntCtorFromEnum);
        il.Emit(OpCodes.Stloc, keysListLocal);

        il.Emit(OpCodes.Ldloc, keysListLocal);
        il.Emit(OpCodes.Callvirt, listUIntGetCount);
        il.Emit(OpCodes.Stloc, countLocal);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(truncateForLoopHead);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, countLocal);
        il.Emit(OpCodes.Bge, truncateForLoopExit);

        // uint key = keysList[i];
        il.Emit(OpCodes.Ldloc, keysListLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, listUIntGetItem);
        il.Emit(OpCodes.Stloc, keyLocal);

        // if ((ulong)key < (ulong)newLength) skip;
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Conv_U8);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Blt_Un, truncateForLoopNext);

        // _sparse.Remove(key);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Callvirt, _tsArraySparseRemove!);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(truncateForLoopNext);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, truncateForLoopHead);
        il.MarkLabel(truncateForLoopExit);

        il.MarkLabel(sparseCheckSkipLabel);

        // while (_dense.Count > newLength) _dense.RemoveAt(_dense.Count - 1);
        il.MarkLabel(truncateDenseLoopHead);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ble, truncateDenseLoopExit);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Callvirt, _tsArrayListRemoveAt!);
        il.Emit(OpCodes.Br, truncateDenseLoopHead);

        il.MarkLabel(truncateDenseLoopExit);

        // _length = newLength; TryCollapseSparse(); return;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, _tsArrayLengthField);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, tryCollapseSparse);

        il.MarkLabel(truncateSparseDoneLabel);
        il.Emit(OpCodes.Ret);

        // Extend path.
        il.MarkLabel(extendPathLabel);

        // long growth = newLength - _length;
        var growthLocal = il.DeclareLocal(_types.Int64);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, growthLocal);

        // if (_sparse != null || growth > SparseThreshold || newLength > int.MaxValue) → sparse extend
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Brtrue, sparseExtendLabel);

        il.Emit(OpCodes.Ldloc, growthLocal);
        il.Emit(OpCodes.Ldc_I8, (long)TSArraySparseThreshold);
        il.Emit(OpCodes.Bgt, sparseExtendLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I8, (long)int.MaxValue);
        il.Emit(OpCodes.Bgt, sparseExtendLabel);

        il.MarkLabel(padHolesLabel);

        // while (_dense.Count < newLength) _dense.Add($ArrayHole.Instance);
        il.MarkLabel(padLoopHead);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Bge, padLoopExit);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, runtime.ArrayHoleInstance);
        il.Emit(OpCodes.Callvirt, _tsArrayListAdd!);
        il.Emit(OpCodes.Br, padLoopHead);

        il.MarkLabel(padLoopExit);
        // _length = (long)_dense.Count;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Stfld, _tsArrayLengthField);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(sparseExtendLabel);
        // _sparse ??= new Dictionary<uint,object?>();
        var hasSparseLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Brtrue, hasSparseLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, _tsArraySparseType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stfld, _tsArraySparseField);

        il.MarkLabel(hasSparseLabel);
        // _length = newLength;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, _tsArrayLengthField);
        il.Emit(OpCodes.Ret);

        // Throw a real $RangeError instance wrapped in a CLR Exception so
        // catch blocks unwrap to a RangeError per ECMA-262 22.1.5.4. The
        // wrapping is inlined here because CreateException isn't yet emitted
        // at TSArray-time. Uses Exception.Data["__tsValue"] = $RangeError so
        // WrapException's existing __tsValue path returns the error instance.
        EmitInlineThrowError(il, runtime, "Invalid array length.", runtime.TSRangeErrorCtor, negThrowLabel);
        EmitInlineThrowError(il, runtime, "Array length exceeds ECMA-262 uint32 maximum.", runtime.TSRangeErrorCtor, tooBigThrowLabel);
    }

    /// <summary>
    /// Inlined equivalent of `$Runtime.CreateException(new $XError(message))`
    /// + Throw. Used by emitters that run before $Runtime is built (e.g.
    /// $Array, $TSObject). Stores the original `$XError` instance in
    /// `ex.Data["__tsValue"]` so `WrapException` returns it on catch.
    /// </summary>
    private void EmitInlineThrowError(ILGenerator il, EmittedRuntime runtime, string message, ConstructorBuilder errorCtor, System.Reflection.Emit.Label markLabel)
    {
        il.MarkLabel(markLabel);
        EmitInlineThrowErrorInline(il, message, errorCtor);
    }

    /// <summary>Inline form of <see cref="EmitInlineThrowError"/> that doesn't mark a label first.</summary>
    private void EmitInlineThrowErrorInline(ILGenerator il, string message, ConstructorBuilder errorCtor)
    {
        var errLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldstr, message);
        il.Emit(OpCodes.Newobj, errorCtor);
        il.Emit(OpCodes.Stloc, errLocal);
        var exLocal2 = il.DeclareLocal(_types.Exception);
        il.Emit(OpCodes.Ldstr, message);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Stloc, exLocal2);
        il.Emit(OpCodes.Ldloc, exLocal2);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.Exception, "Data").GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, "__tsValue");
        il.Emit(OpCodes.Ldloc, errLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.IDictionary, "set_Item"));
        il.Emit(OpCodes.Ldloc, exLocal2);
        il.Emit(OpCodes.Throw);
    }

    /// <summary>
    /// <c>public void DeleteAt(long index)</c> — ECMA-262 <c>delete arr[i]</c>.
    /// Turns the slot into a hole; length is unchanged. No-op for frozen
    /// arrays or out-of-range indices.
    /// </summary>
    private void EmitTSArrayDeleteAt(TypeBuilder typeBuilder, EmittedRuntime runtime, MethodBuilder syncLength)
    {
        var method = typeBuilder.DefineMethod("DeleteAt", MethodAttributes.Public, _types.Void, [_types.Int64]);
        runtime.TSArrayDeleteAt = method;

        var il = method.GetILGenerator();
        var retLabel = il.DefineLabel();
        var denseDeleteLabel = il.DefineLabel();
        var sparseDeleteCheckLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, syncLength);

        // if (_isFrozen) return;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayIsFrozenField);
        il.Emit(OpCodes.Brtrue, retLabel);

        // if ((ulong)index >= (ulong)_length) return;
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Blt, retLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArrayLengthField);
        il.Emit(OpCodes.Bge, retLabel);

        // if (_sparse == null || index < _dense.Count) _dense[(int)index] = $ArrayHole.Instance;
        // else if (index <= uint.MaxValue) _sparse.Remove((uint)index);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Brfalse, denseDeleteLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _tsArrayListCountGetter!);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Bge, sparseDeleteCheckLabel);

        il.MarkLabel(denseDeleteLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldsfld, runtime.ArrayHoleInstance);
        il.Emit(OpCodes.Callvirt, _tsArrayListSetItem!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(sparseDeleteCheckLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I8, (long)uint.MaxValue);
        il.Emit(OpCodes.Bgt, retLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsArraySparseField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Callvirt, _tsArraySparseRemove!);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(retLabel);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSArrayToString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ToString",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            _types.String,
            Type.EmptyTypes
        );
        runtime.TSArrayToString = method;

        var il = method.GetILGenerator();

        // ToString renders _dense elements joined by comma — matches pre-refactor
        // behavior exactly. Hole-aware join lives in the array built-ins (M5).
        il.Emit(OpCodes.Ldstr, ",");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.ListOfObject, "ToArray"));
        il.Emit(OpCodes.Call, _types.String.GetMethod("Join", [_types.String, _types.ObjectArray])!);
        il.Emit(OpCodes.Ret);
    }


    // Reserved for a future int→long convenience shim if needed by legacy call
    // sites. M1 does not add one because the legacy int-indexed Get/Set above
    // already cover the pre-Stage-E surface.
    private const int _reservedStageEConstants = 0;
    private const long TSArraySparseThreshold = 1024;
    private const long TSArrayMaxWriteIndex = (long)uint.MaxValue - 1;
    private const long TSArrayMaxLength = (long)uint.MaxValue;
}
