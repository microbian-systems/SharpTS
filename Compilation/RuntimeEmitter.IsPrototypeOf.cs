using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits <c>$Runtime.IsPrototypeOfHelper(object receiverProto, object target) -&gt; bool</c>:
    /// returns true iff <paramref name="receiverProto"/> appears in
    /// <paramref name="target"/>'s prototype chain (walked via <c>PDSGetPrototype</c>).
    /// Per ECMA-262 §20.1.3.4 Object.prototype.isPrototypeOf.
    /// Used to back <c>obj.isPrototypeOf(other)</c> for $TSFunction / $Object /
    /// Dictionary receivers exposed via Object.prototype's populate.
    /// </summary>
    private void EmitIsPrototypeOfHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "IsPrototypeOfHelper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.Object]);
        runtime.IsPrototypeOfHelperMethod = method;
        // Note: do NOT name first param "__this". The wrappers use target
        // binding (TSFunctionCtor with target=receiver), so Invoke's
        // static-with-target path prepends target. If we also marked
        // __this, InvokeWithThis would double-prepend.

        var il = method.GetILGenerator();
        var falseLabel = il.DefineLabel();
        var trueLabel = il.DefineLabel();

        // null/undefined target → false
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, falseLabel);
        // null receiverProto → false (per spec ToObject(this) but we treat null as false)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, falseLabel);

        // Walk: current = PDSGetPrototype(target);
        // while (current != null) {
        //   if (current == receiverProto) return true;
        //   current = PDSGetPrototype(current);
        // }
        var currentLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ObjectGetPrototypeOf);
        il.Emit(OpCodes.Stloc, currentLocal);

        var loopStart = il.DefineLabel();
        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, currentLocal);
        il.Emit(OpCodes.Brfalse, falseLabel);

        // if (current == receiverProto) return true
        il.Emit(OpCodes.Ldloc, currentLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Beq, trueLabel);

        // current = PDSGetPrototype(current)
        il.Emit(OpCodes.Ldloc, currentLocal);
        il.Emit(OpCodes.Call, runtime.ObjectGetPrototypeOf);
        il.Emit(OpCodes.Stloc, currentLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(falseLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(trueLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }
}
