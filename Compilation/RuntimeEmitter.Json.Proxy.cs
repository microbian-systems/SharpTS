using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits IL that materializes a SharpTSProxy into a Dictionary&lt;string, object?&gt; by
    /// dispatching the proxy's [[OwnPropertyKeys]] (TrapOwnKeys) and [[Get]] (TrapGet)
    /// traps. Used by JSON.stringify so the existing dict-iteration path can serialize
    /// the proxy without each call site needing trap awareness.
    ///
    /// On entry: <paramref name="valueLocal"/> holds the proxy reference.
    /// On exit (via fall-through): <paramref name="valueLocal"/> holds the materialized
    /// Dictionary&lt;string, object?&gt; and execution continues at the caller's instruction.
    /// On non-proxy: branches to <paramref name="notProxyLabel"/> with valueLocal unchanged.
    ///
    /// Uses late-bound reflection (Type.GetType(..., SharpTS)) to avoid embedding a
    /// SharpTS.dll reference in the emitted assembly per the standalone-DLL constraint.
    /// </summary>
    private void EmitProxyMaterializeForJson(ILGenerator il, LocalBuilder valueLocal, Label notProxyLabel)
    {
        var proxyLabel = il.DefineLabel();
        EmitProxyTypeCheck(il, () => il.Emit(OpCodes.Ldloc, valueLocal), proxyLabel, notProxyLabel);

        il.MarkLabel(proxyLabel);

        // proxyType = Type.GetType("SharpTS.Runtime.Types.SharpTSProxy, SharpTS")
        var proxyTypeLocal = il.DeclareLocal(_types.Type);
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.SharpTSProxy, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, proxyTypeLocal);

        // keys = (List<string>)proxyType.GetMethod("TrapOwnKeys").Invoke(proxy, new object[]{ null })
        // TrapOwnKeys throws if the proxy is revoked — surfaces the spec-required TypeError.
        var keysLocal = il.DeclareLocal(_types.ListOfString);
        il.Emit(OpCodes.Ldloc, proxyTypeLocal);
        il.Emit(OpCodes.Ldstr, "TrapOwnKeys");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        // [0] = null (Interpreter) — already null from Newarr
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Castclass, _types.ListOfString);
        il.Emit(OpCodes.Stloc, keysLocal);

        // Cache trapGet MethodInfo across the loop.
        var trapGetMiLocal = il.DeclareLocal(_types.MethodInfo);
        il.Emit(OpCodes.Ldloc, proxyTypeLocal);
        il.Emit(OpCodes.Ldstr, "TrapGet");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        il.Emit(OpCodes.Stloc, trapGetMiLocal);

        // dict = new Dictionary<string, object?>();
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.DictionaryStringObject, Type.EmptyTypes));
        il.Emit(OpCodes.Stloc, dictLocal);

        // for (int i = 0; i < keys.Count; i++)
        var iLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, keysLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfString, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // k = keys[i]
        var keyLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldloc, keysLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfString, "get_Item", [_types.Int32]));
        il.Emit(OpCodes.Stloc, keyLocal);

        // v = trapGetMi.Invoke(proxy, new object[]{ k, null })
        var valTmpLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, trapGetMiLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Stelem_Ref);
        // [1] = null (Interpreter) — already null from Newarr
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodBase, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Stloc, valTmpLocal);

        // dict[k] = v
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Ldloc, valTmpLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", [_types.String, _types.Object]));

        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        // valueLocal = dict — caller will continue down the dict-stringify path.
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Stloc, valueLocal);
    }
}
