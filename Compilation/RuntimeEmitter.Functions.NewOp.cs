using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits <c>$Runtime.NewOnFunction(object fn, object[] args) → object</c>, the
    /// JS <c>new</c> protocol for runtime-valued function callees (<c>$TSFunction</c>,
    /// <c>$BoundTSFunction</c>). Builds a fresh <c>$Object</c> to serve as the
    /// constructor's <c>this</c>, invokes the function via <c>InvokeWithThis</c>
    /// (and also via a thread-local <c>this</c> slot so bodies without <c>__this</c>
    /// params resolve `this` to the new object), and returns the explicit object
    /// return value if the body yielded one — otherwise the constructed <c>this</c>.
    /// </summary>
    /// <remarks>
    /// Non-callable values return null, mirroring the pre-existing <c>Ldnull</c>
    /// behavior rather than throwing — legacy code paths for <c>new &lt;not-a-function&gt;</c>
    /// shouldn't regress. Must be emitted after <c>$Object</c>, <c>$TSFunction</c>,
    /// and <c>$BoundTSFunction</c> are defined.
    /// </remarks>
    internal void EmitNewOnFunction(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Thread-static "current function this" field. Function bodies with no
        // __this parameter (e.g. `function Ctor() { this.x = 1 }`) resolve `this`
        // by reading this field — see LocalVariableResolver.LoadThis's final
        // fallback path.
        var currentThisField = typeBuilder.DefineField(
            "_currentFunctionThis",
            _types.Object,
            FieldAttributes.Public | FieldAttributes.Static);
        var threadStaticCtor = typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes)!;
        currentThisField.SetCustomAttribute(new CustomAttributeBuilder(threadStaticCtor, []));
        runtime.CurrentFunctionThisField = currentThisField;

        var method = typeBuilder.DefineMethod(
            "NewOnFunction",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.ObjectArray]);
        runtime.NewOnFunction = method;

        var il = method.GetILGenerator();

        var newObjLocal = il.DeclareLocal(runtime.TSObjectType);
        var resultLocal = il.DeclareLocal(_types.Object);
        var prevThisLocal = il.DeclareLocal(_types.Object);
        var notCallableLocal = il.DeclareLocal(_types.Boolean);

        // newObj = new $Object(new Dictionary<string, object>())
        var dictCtor = _types.GetDefaultConstructor(_types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, dictCtor);
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        il.Emit(OpCodes.Stloc, newObjLocal);

        // Save current thread-local this so nested `new F()` inside `new G()` doesn't
        // leak G's newObj to F. Restored in the finally below.
        il.Emit(OpCodes.Ldsfld, currentThisField);
        il.Emit(OpCodes.Stloc, prevThisLocal);

        // Set _currentFunctionThis to newObj so the body's `LoadThis` picks it up.
        il.Emit(OpCodes.Ldloc, newObjLocal);
        il.Emit(OpCodes.Stsfld, currentThisField);

        il.BeginExceptionBlock();

        var tryBound = il.DefineLabel();
        var notCallable = il.DefineLabel();
        var afterTry = il.DefineLabel();

        // if (fn is $TSFunction) result = fn.InvokeWithThis(newObj, args);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, tryBound);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldloc, newObjLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvokeWithThis);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Leave, afterTry);

        // else if (fn is $BoundTSFunction) result = fn.InvokeWithThis(newObj, args);
        il.MarkLabel(tryBound);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brfalse, notCallable);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Ldloc, newObjLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.BoundTSFunctionInvokeWithThis);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Leave, afterTry);

        // Non-callable: flag so the post-finally code returns null (legacy behavior).
        il.MarkLabel(notCallable);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, notCallableLocal);
        il.Emit(OpCodes.Leave, afterTry);

        il.BeginFinallyBlock();
        // Restore thread-local this regardless of success/throw.
        il.Emit(OpCodes.Ldloc, prevThisLocal);
        il.Emit(OpCodes.Stsfld, currentThisField);
        il.Emit(OpCodes.Endfinally);
        il.EndExceptionBlock();

        il.MarkLabel(afterTry);

        // Non-callable callee → null (matches legacy `new <not-a-function>` behavior).
        var notCallableSkip = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, notCallableLocal);
        il.Emit(OpCodes.Brfalse, notCallableSkip);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notCallableSkip);

        // Per JS: return result if it's an object, else newObj.
        var returnNewObj = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Brfalse, returnNewObj);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Beq, returnNewObj);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, returnNewObj);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brtrue, returnNewObj);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, returnNewObj);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnNewObj);
        il.Emit(OpCodes.Ldloc, newObjLocal);
        il.Emit(OpCodes.Ret);
    }
}
