using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    // Slot layout inside each per-(Type,symbol) object[4].
    private const int SymGetterInstanceSlot = 0;
    private const int SymSetterInstanceSlot = 1;
    private const int SymGetterStaticSlot = 2;
    private const int SymSetterStaticSlot = 3;

    private Type SymInnerDictType => _types.MakeGenericType(_types.DictionaryOpen, _types.Object, _types.ObjectArray);
    private Type SymOuterDictType => _types.MakeGenericType(_types.DictionaryOpen, _types.Type, SymInnerDictType);

    /// <summary>
    /// Forward-declares the symbol-accessor registry field and its three helper
    /// methods (#266). GetIndex/SetIndex — emitted before the bodies — call the
    /// FindSymbol* methods, and class .cctors (emitted after all runtime methods)
    /// call RegisterSymbolAccessor, so the signatures must exist up front.
    /// </summary>
    private void DefineSymbolAccessorRegistry(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.SymbolAccessorRegistryField = typeBuilder.DefineField(
            "_symbolAccessors", SymOuterDictType,
            FieldAttributes.Public | FieldAttributes.Static);

        runtime.RegisterSymbolAccessor = typeBuilder.DefineMethod(
            "RegisterSymbolAccessor",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.Type, _types.Object, _types.Object, _types.Object, _types.Boolean]);

        runtime.FindSymbolGetter = typeBuilder.DefineMethod(
            "FindSymbolGetterFor",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);

        runtime.FindSymbolSetter = typeBuilder.DefineMethod(
            "FindSymbolSetterFor",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
    }

    /// <summary>Emits the registry field initialization into the $Runtime cctor.</summary>
    private void InitSymbolAccessorRegistry(ILGenerator cctorIL, EmittedRuntime runtime)
    {
        cctorIL.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(SymOuterDictType));
        cctorIL.Emit(OpCodes.Stsfld, runtime.SymbolAccessorRegistryField);
    }

    /// <summary>Fills the bodies of Register/FindGetter/FindSetter (#266).</summary>
    private void EmitSymbolAccessorRegistryBodies(EmittedRuntime runtime)
    {
        var outer = SymOuterDictType;
        var inner = SymInnerDictType;
        var field = runtime.SymbolAccessorRegistryField;

        var outerTryGetValue = outer.GetMethod("TryGetValue", [_types.Type, inner.MakeByRefType()])!;
        var outerSetItem = outer.GetMethod("set_Item", [_types.Type, inner])!;
        var innerTryGetValue = inner.GetMethod("TryGetValue", [_types.Object, _types.ObjectArray.MakeByRefType()])!;
        var innerSetItem = inner.GetMethod("set_Item", [_types.Object, _types.ObjectArray])!;
        var getBaseType = _types.GetProperty(_types.Type, "BaseType").GetGetMethod()!;
        var getType = _types.GetMethod(_types.Object, "GetType");

        EmitRegisterSymbolAccessorBody(runtime, inner, outerTryGetValue, outerSetItem, innerTryGetValue, innerSetItem);
        EmitFindSymbolAccessorBody(runtime.FindSymbolGetter, field, inner,
            SymGetterInstanceSlot, SymGetterStaticSlot, outerTryGetValue, innerTryGetValue, getBaseType, getType);
        EmitFindSymbolAccessorBody(runtime.FindSymbolSetter, field, inner,
            SymSetterInstanceSlot, SymSetterStaticSlot, outerTryGetValue, innerTryGetValue, getBaseType, getType);
    }

    private void EmitRegisterSymbolAccessorBody(
        EmittedRuntime runtime, Type inner,
        MethodInfo outerTryGetValue, MethodInfo outerSetItem,
        MethodInfo innerTryGetValue, MethodInfo innerSetItem)
    {
        // RegisterSymbolAccessor(Type owner, object symbol, object getter, object setter, bool isStatic)
        var il = runtime.RegisterSymbolAccessor.GetILGenerator();
        var field = runtime.SymbolAccessorRegistryField;
        var innerLocal = il.DeclareLocal(inner);
        var slotLocal = il.DeclareLocal(_types.ObjectArray);

        // if (symbol == null) return; — a non-well-known key that couldn't be
        // resolved at .cctor time (e.g. a module-local Symbol whose binding isn't
        // yet visible) must not crash the type initializer. Well-known-symbol keys
        // (the common case: species/toStringTag/toPrimitive/iterator) are global
        // constants and always resolve. Module-local Symbol keys tracked by #282.
        var symbolOkLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brtrue, symbolOkLabel);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(symbolOkLabel);

        // if (!_symbolAccessors.TryGetValue(owner, out inner)) { inner = new(); _symbolAccessors[owner] = inner; }
        var haveInner = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, innerLocal);
        il.Emit(OpCodes.Callvirt, outerTryGetValue);
        il.Emit(OpCodes.Brtrue, haveInner);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(inner));
        il.Emit(OpCodes.Stloc, innerLocal);
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, innerLocal);
        il.Emit(OpCodes.Callvirt, outerSetItem);
        il.MarkLabel(haveInner);

        // if (!inner.TryGetValue(symbol, out slot)) { slot = new object[4]; inner[symbol] = slot; }
        var haveSlot = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, innerLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, slotLocal);
        il.Emit(OpCodes.Callvirt, innerTryGetValue);
        il.Emit(OpCodes.Brtrue, haveSlot);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, slotLocal);
        il.Emit(OpCodes.Ldloc, innerLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, slotLocal);
        il.Emit(OpCodes.Callvirt, innerSetItem);
        il.MarkLabel(haveSlot);

        // if (getter != null) slot[isStatic ? 2 : 0] = getter;
        EmitStoreSlotIfPresent(il, slotLocal, argIndex: 2, staticSlot: SymGetterStaticSlot, instanceSlot: SymGetterInstanceSlot);
        // if (setter != null) slot[isStatic ? 3 : 1] = setter;
        EmitStoreSlotIfPresent(il, slotLocal, argIndex: 3, staticSlot: SymSetterStaticSlot, instanceSlot: SymSetterInstanceSlot);

        il.Emit(OpCodes.Ret);
    }

    // if (argN != null) slot[isStatic ? staticSlot : instanceSlot] = argN;
    private static void EmitStoreSlotIfPresent(ILGenerator il, LocalBuilder slotLocal, int argIndex, int staticSlot, int instanceSlot)
    {
        var skip = il.DefineLabel();
        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Brfalse, skip);
        il.Emit(OpCodes.Ldloc, slotLocal);
        // index = isStatic(arg4) ? staticSlot : instanceSlot
        var useStatic = il.DefineLabel();
        var haveIdx = il.DefineLabel();
        il.Emit(OpCodes.Ldarg, 4);
        il.Emit(OpCodes.Brtrue, useStatic);
        il.Emit(OpCodes.Ldc_I4, instanceSlot);
        il.Emit(OpCodes.Br, haveIdx);
        il.MarkLabel(useStatic);
        il.Emit(OpCodes.Ldc_I4, staticSlot);
        il.MarkLabel(haveIdx);
        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Stelem_Ref);
        il.MarkLabel(skip);
    }

    // object FindSymbol{Getter,Setter}For(object obj, object symbol):
    //   owner = obj is Type t ? t : obj.GetType();  idx = obj is Type ? staticSlot : instanceSlot;
    //   for (; owner != null; owner = owner.BaseType)
    //     if (reg.TryGetValue(owner, out inner) && inner.TryGetValue(symbol, out slot) && slot[idx] != null) return slot[idx];
    //   return null;
    private void EmitFindSymbolAccessorBody(
        MethodBuilder method, FieldBuilder field, Type inner,
        int instanceSlot, int staticSlot,
        MethodInfo outerTryGetValue, MethodInfo innerTryGetValue, MethodInfo getBaseType, MethodInfo getType)
    {
        var il = method.GetILGenerator();
        var ownerLocal = il.DeclareLocal(_types.Type);
        var idxLocal = il.DeclareLocal(_types.Int32);
        var innerLocal = il.DeclareLocal(inner);
        var slotLocal = il.DeclareLocal(_types.ObjectArray);
        var asTypeLocal = il.DeclareLocal(_types.Type);

        var retNull = il.DefineLabel();

        // if (_symbolAccessors == null) return null;
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Brfalse, retNull);

        // owner/idx from whether obj is a Type (static) or an instance.
        var isType = il.DefineLabel();
        var afterOwner = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Type);
        il.Emit(OpCodes.Stloc, asTypeLocal);
        il.Emit(OpCodes.Ldloc, asTypeLocal);
        il.Emit(OpCodes.Brtrue, isType);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, getType);
        il.Emit(OpCodes.Stloc, ownerLocal);
        il.Emit(OpCodes.Ldc_I4, instanceSlot);
        il.Emit(OpCodes.Stloc, idxLocal);
        il.Emit(OpCodes.Br, afterOwner);
        il.MarkLabel(isType);
        il.Emit(OpCodes.Ldloc, asTypeLocal);
        il.Emit(OpCodes.Stloc, ownerLocal);
        il.Emit(OpCodes.Ldc_I4, staticSlot);
        il.Emit(OpCodes.Stloc, idxLocal);
        il.MarkLabel(afterOwner);

        // base-chain walk
        var loopTop = il.DefineLabel();
        var nextBase = il.DefineLabel();
        var retIt = il.DefineLabel();
        var runClassCtor = typeof(System.Runtime.CompilerServices.RuntimeHelpers)
            .GetMethod("RunClassConstructor", [typeof(RuntimeTypeHandle)])!;
        var getTypeHandle = _types.GetProperty(_types.Type, "TypeHandle").GetGetMethod()!;

        il.MarkLabel(loopTop);
        il.Emit(OpCodes.Ldloc, ownerLocal);
        il.Emit(OpCodes.Brfalse, retNull);
        // Static-side accessors are registered in the owner class's .cctor, which
        // the CLR runs lazily — merely using the class as a Type value (typeof) does
        // not trigger it. Force it so a static `Class[Symbol.x]` access sees the
        // registration. Idempotent and cheap after the first run. (Instance-side
        // accessors are already registered by the time an instance exists.)
        {
            var skipCctor = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, asTypeLocal);
            il.Emit(OpCodes.Brfalse, skipCctor);
            il.Emit(OpCodes.Ldloc, ownerLocal);
            il.Emit(OpCodes.Callvirt, getTypeHandle);
            il.Emit(OpCodes.Call, runClassCtor);
            il.MarkLabel(skipCctor);
        }
        // if (!reg.TryGetValue(owner, out inner)) goto nextBase;
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ldloc, ownerLocal);
        il.Emit(OpCodes.Ldloca, innerLocal);
        il.Emit(OpCodes.Callvirt, outerTryGetValue);
        il.Emit(OpCodes.Brfalse, nextBase);
        // if (!inner.TryGetValue(symbol, out slot)) goto nextBase;
        il.Emit(OpCodes.Ldloc, innerLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, slotLocal);
        il.Emit(OpCodes.Callvirt, innerTryGetValue);
        il.Emit(OpCodes.Brfalse, nextBase);
        // v = slot[idx]; if (v != null) return v;
        il.Emit(OpCodes.Ldloc, slotLocal);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, retIt);
        il.Emit(OpCodes.Pop);
        il.MarkLabel(nextBase);
        il.Emit(OpCodes.Ldloc, ownerLocal);
        il.Emit(OpCodes.Callvirt, getBaseType);
        il.Emit(OpCodes.Stloc, ownerLocal);
        il.Emit(OpCodes.Br, loopTop);

        il.MarkLabel(retIt);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(retNull);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }
}
