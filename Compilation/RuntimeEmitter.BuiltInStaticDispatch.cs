using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits <c>$Runtime.LookupBuiltInStaticMember</c>, a runtime helper that
/// mirrors the compile-time static emitter registry (<c>ArrayStaticEmitter</c>,
/// <c>NumberStaticEmitter</c>, <c>StringStaticEmitter</c>, <c>MathStaticEmitter</c>)
/// for the value-form access path. When a built-in type constructor is stored
/// in a variable — e.g. <c>var A = Array</c> → a <c>System.Type</c> token —
/// and then accessed for a static member (<c>A.isArray</c>), the generic
/// <c>$Runtime.GetProperty</c> Type branch reflects for real .NET statics and
/// finds nothing (<c>IList&lt;object&gt;</c>, <c>double</c>, <c>string</c> have
/// no matching static methods). This helper supplies the same <c>$TSFunction</c>
/// wrapper that the compile-time registry would have emitted at the bare
/// <c>Array.isArray</c> site.
///
/// Lodash hits this heavily: <c>var Array = context.Array; var isArray = Array.isArray;</c>
/// inside <c>runInContext</c> — without runtime dispatch the local <c>isArray</c>
/// ends up <c>undefined</c> and every internal <c>isArray(x)</c> silently
/// returns wrong values. See issue #63 for the full chain.
/// </summary>
public partial class RuntimeEmitter
{
    /// <summary>
    /// Defines the <c>LookupBuiltInStaticMember</c> <see cref="MethodBuilder"/>
    /// without writing its body. Must be called early in runtime emission —
    /// before <c>EmitGetProperty</c> — because <c>GetProperty</c>'s Type
    /// branch emits a call to this method. The body is filled in later by
    /// <see cref="EmitLookupBuiltInStaticMemberBody"/>, after all the backing
    /// static runtime methods (<c>IsArray</c>, <c>NumberIs*</c>, <c>StringFrom*</c>)
    /// have been emitted.
    /// </summary>
    private void DefineLookupBuiltInStaticMember(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.LookupBuiltInStaticMember = typeBuilder.DefineMethod(
            "LookupBuiltInStaticMember",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Type, _types.String]
        );
    }

    /// <summary>
    /// Emits the body of <c>LookupBuiltInStaticMember</c>. Depends on the
    /// backing runtime methods being defined, so must run after
    /// <c>EmitIsArray</c>, <c>EmitNumberMethods</c>, <c>EmitStringFromCharCode</c>,
    /// <c>EmitStringFromCodePoint</c>, and <c>EmitTSFunctionClass</c>.
    /// </summary>
    private void EmitLookupBuiltInStaticMemberBody(EmittedRuntime runtime)
    {
        var method = runtime.LookupBuiltInStaticMember;
        var il = method.GetILGenerator();
        var notFoundLabel = il.DefineLabel();

        // Pre-compute the MethodInfo object for op_Equality on System.Type and
        // GetTypeFromHandle on RuntimeTypeHandle — both called many times below.
        var strEquals = _types.Type.GetMethod("op_Equality", [_types.Type, _types.Type])!;
        var getTypeFromHandle = _types.Type.GetMethod("GetTypeFromHandle", [_types.RuntimeTypeHandle])!;
        var stringOpEq = _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String);
        // GetMethodFromHandle lives on MethodBase, not Type.
        var getMethodFromHandle = _types.MethodBase.GetMethod(
            "GetMethodFromHandle",
            [_types.RuntimeMethodHandle, _types.RuntimeTypeHandle])!;

        // Emit one branch per (Type, name, runtimeMethod) triple. Each branch:
        //   if (arg0 == typeof(T) && arg1 == "name") return new $TSFunction(null, method);
        void EmitLookup(Type targetType, string memberName, MethodInfo backingMethod)
        {
            var skipLabel = il.DefineLabel();

            // Type check
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldtoken, targetType);
            il.Emit(OpCodes.Call, getTypeFromHandle);
            il.Emit(OpCodes.Call, strEquals);
            il.Emit(OpCodes.Brfalse, skipLabel);

            // Name check
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldstr, memberName);
            il.Emit(OpCodes.Call, stringOpEq);
            il.Emit(OpCodes.Brfalse, skipLabel);

            // Match — return new $TSFunction(null, MethodInfo_of(backingMethod)).
            // Two-arg GetMethodFromHandle required: backingMethod's declaring type
            // is the emitted $Runtime TypeBuilder and token resolution in
            // persisted assemblies needs both handles.
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldtoken, backingMethod);
            il.Emit(OpCodes.Ldtoken, backingMethod.DeclaringType!);
            il.Emit(OpCodes.Call, getMethodFromHandle);
            il.Emit(OpCodes.Castclass, _types.MethodInfo);
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(skipLabel);
        }

        // Array.* — stored-as-value Array reference accessing static members.
        EmitLookup(_types.IListOfObject, "isArray", runtime.IsArray);

        // Number.* — bare `Number` identifier now resolves to typeof(double)
        // via issue #62, so the value-form path lands here.
        EmitLookup(_types.Double, "isNaN",         runtime.NumberIsNaN);
        EmitLookup(_types.Double, "isFinite",      runtime.NumberIsFinite);
        EmitLookup(_types.Double, "isInteger",     runtime.NumberIsInteger);
        EmitLookup(_types.Double, "isSafeInteger", runtime.NumberIsSafeInteger);

        // String.* — bare `String` identifier resolves to typeof(string).
        EmitLookup(_types.String, "fromCharCode",  runtime.StringFromCharCode);
        EmitLookup(_types.String, "fromCodePoint", runtime.StringFromCodePoint);

        // Math.* deliberately not handled here — bare `Math` emits the null
        // pseudo-variable (not a Type token), so its value-form access goes
        // through MathStaticEmitter.TryEmitStaticPropertyGet at compile time.
        // If a `var m = Math; m.floor(x)` pattern surfaces in real code, add
        // a separate dispatch keyed off the null receiver.

        il.MarkLabel(notFoundLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }
}
