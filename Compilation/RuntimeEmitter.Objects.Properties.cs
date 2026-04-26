using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Runtime.BuiltIns;
using SharpTS.Runtime.Types;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    internal void EmitToPascalCase(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // ToPascalCase(string name) -> string
        // Converts "camelCase" to "PascalCase" by upper-casing first character
        var method = typeBuilder.DefineMethod(
            "ToPascalCase",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String]
        );
        runtime.ToPascalCase = method;

        var il = method.GetILGenerator();
        var returnOriginalLabel = il.DefineLabel();

        // if (string.IsNullOrEmpty(name)) return name;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "IsNullOrEmpty"));
        il.Emit(OpCodes.Brtrue, returnOriginalLabel);

        // if (char.IsUpper(name[0])) return name;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "get_Chars", _types.Int32));
        il.Emit(OpCodes.Call, typeof(char).GetMethod("IsUpper", [typeof(char)])!);
        il.Emit(OpCodes.Brtrue, returnOriginalLabel);

        // return char.ToString(char.ToUpperInvariant(name[0])) + name.Substring(1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "get_Chars", _types.Int32));
        il.Emit(OpCodes.Call, typeof(char).GetMethod("ToUpperInvariant", [typeof(char)])!);
        il.Emit(OpCodes.Call, typeof(char).GetMethod("ToString", [typeof(char)])!);  // static char.ToString(char)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "Substring", _types.Int32));
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnOriginalLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a reflection helper <c>SafeGetMethod(Type, string, BindingFlags) -> MethodInfo</c>
    /// that wraps <see cref="Type.GetMethod(string, BindingFlags)"/> and degrades gracefully
    /// when the lookup would otherwise throw <see cref="System.Reflection.AmbiguousMatchException"/>
    /// because multiple overloads share the name. On ambiguity: prefer a zero-argument
    /// overload (matches the "read property, invoke with no args" pattern used by
    /// <c>GetFieldsProperty</c>'s callable wrapping); otherwise return the first
    /// name-matching overload. Returns null when no method matches the name.
    /// </summary>
    internal void EmitSafeGetMethod(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SafeGetMethod",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.MethodInfo,
            [_types.Type, _types.String, typeof(BindingFlags)]
        );
        runtime.SafeGetMethod = method;

        var il = method.GetILGenerator();
        var resultLocal = il.DeclareLocal(_types.MethodInfo);
        var methodsArrayType = _types.MethodInfo.MakeArrayType();
        var methodsLocal = il.DeclareLocal(methodsArrayType);
        var iLocal = il.DeclareLocal(_types.Int32);
        var mLocal = il.DeclareLocal(_types.MethodInfo);

        var retLabel = il.DefineLabel();

        // Happy path: return t.GetMethod(name, flags) when unambiguous.
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, typeof(BindingFlags)));
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Leave, retLabel);

        // Ambiguous — fall back to a deterministic pick.
        il.BeginCatchBlock(typeof(System.Reflection.AmbiguousMatchException));
        il.Emit(OpCodes.Pop); // discard exception
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, resultLocal);

        // methods = t.GetMethods(flags)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethods", typeof(BindingFlags)));
        il.Emit(OpCodes.Stloc, methodsLocal);

        // Pass 1: prefer a zero-arg overload.
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);
        var pass1Start = il.DefineLabel();
        var pass1End = il.DefineLabel();
        var pass1Continue = il.DefineLabel();
        il.MarkLabel(pass1Start);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, methodsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, pass1End);

        il.Emit(OpCodes.Ldloc, methodsLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, mLocal);

        // if (!m.Name.Equals(name, OrdinalIgnoreCase)) continue
        il.Emit(OpCodes.Ldloc, mLocal);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.MethodBase, "Name"));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4, (int)StringComparison.OrdinalIgnoreCase);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Equals", _types.String, _types.String, _types.StringComparison));
        il.Emit(OpCodes.Brfalse, pass1Continue);

        // if (m.GetParameters().Length != 0) continue
        il.Emit(OpCodes.Ldloc, mLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodBase, "GetParameters"));
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Brtrue, pass1Continue);

        // Zero-arg match — store and break out of pass 1.
        il.Emit(OpCodes.Ldloc, mLocal);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, pass1End);

        il.MarkLabel(pass1Continue);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, pass1Start);
        il.MarkLabel(pass1End);

        // If pass 1 found something, we're done.
        var catchEnd = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Brtrue, catchEnd);

        // Pass 2: first name match (arbitrary but deterministic).
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);
        var pass2Start = il.DefineLabel();
        var pass2End = il.DefineLabel();
        var pass2Continue = il.DefineLabel();
        il.MarkLabel(pass2Start);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, methodsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, pass2End);

        il.Emit(OpCodes.Ldloc, methodsLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, mLocal);

        il.Emit(OpCodes.Ldloc, mLocal);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.MethodBase, "Name"));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4, (int)StringComparison.OrdinalIgnoreCase);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Equals", _types.String, _types.String, _types.StringComparison));
        il.Emit(OpCodes.Brfalse, pass2Continue);

        il.Emit(OpCodes.Ldloc, mLocal);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, pass2End);

        il.MarkLabel(pass2Continue);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, pass2Start);
        il.MarkLabel(pass2End);

        il.MarkLabel(catchEnd);
        il.Emit(OpCodes.Leave, retLabel);
        il.EndExceptionBlock();

        il.MarkLabel(retLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetFieldsProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // GetFieldsProperty(object obj, string name) -> object
        // Resolves class-instance properties through emitted runtime state only:
        // descriptor store accessors, emitted $Object fields, and known method wrappers.
        var method = typeBuilder.DefineMethod(
            "GetFieldsProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.String]
        );
        runtime.GetFieldsProperty = method;

        var il = method.GetILGenerator();
        var nullLabel = il.DefineLabel();
        var tryMethodLabel = il.DefineLabel();

        // Declare locals upfront
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var valueLocal = il.DeclareLocal(_types.Object);
        var objectFieldsLocal = il.DeclareLocal(_types.DictionaryStringObject);

        // if (obj == null) return null;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // ECMA-262: `error.constructor` should return the constructor that built it.
        // For $Error subclasses (and $Error itself), return the instance's runtime
        // type as a System.Type. Compiled-mode `TypeError` resolves to the same
        // System.Type, so `caught.constructor === TypeError` strict-equality works.
        // Without this, test262's `assert.throws(TypeError, fn)` fails because
        // `thrown.constructor === expectedErrorConstructor` reads `undefined`.
        var afterErrorCtorLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSErrorType);
        il.Emit(OpCodes.Brfalse, afterErrorCtorLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "constructor");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, afterErrorCtorLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Ret);
        il.MarkLabel(afterErrorCtorLabel);

        // ECMA-262: `(42).constructor === Number`, `true.constructor === Boolean`.
        // Compiled `Number` resolves to typeof(double); `Boolean` to typeof(bool).
        // (String is handled inline in EmitGetProperty's stringLabel branch.)
        var afterPrimCtorLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "constructor");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, afterPrimCtorLabel);
        // double?
        var notDoubleCtorLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, notDoubleCtorLabel);
        il.Emit(OpCodes.Ldtoken, _types.Double);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notDoubleCtorLabel);
        // bool?
        var notBoolCtorLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brfalse, notBoolCtorLabel);
        il.Emit(OpCodes.Ldtoken, _types.Boolean);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notBoolCtorLabel);
        il.MarkLabel(afterPrimCtorLabel);

        // Special case: HashSet<object?>.size for compiled Sets
        // Compiled code uses HashSet<object?> for Sets, and structuredClone returns the same type.
        // When accessing .size, we need to return HashSet.Count.
        var hashSetSizeLabel = il.DefineLabel();
        var afterHashSetSizeLabel = il.DefineLabel();

        // Check if obj is HashSet<object?> and name == "size"
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.HashSetOfObject);
        il.Emit(OpCodes.Brfalse, afterHashSetSizeLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "size");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, afterHashSetSizeLabel);

        // It's a HashSet and property is "size" - return Count as double
        il.MarkLabel(hashSetSizeLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.HashSetOfObject);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.HashSetOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(afterHashSetSizeLabel);

        // Check $PropertyDescriptorStore for dynamically defined properties (via Object.defineProperty)
        // This allows defineProperty to work on class instances
        var afterPDSCheckLabel = il.DefineLabel();
        var pdsGetterLocal = il.DeclareLocal(_types.Object);
        var pdsDescriptorLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);

        // Try to get getter: PDSTryGetGetter(obj, name, out getter)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, pdsGetterLocal);
        il.Emit(OpCodes.Call, runtime.PDSTryGetGetter);
        var noGetterInPDSLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, noGetterInPDSLabel);

        // Getter was found - invoke it: InvokeMethodValue(obj, getter, emptyArgs)
        il.Emit(OpCodes.Ldarg_0);  // receiver (obj)
        il.Emit(OpCodes.Ldloc, pdsGetterLocal);  // function (getter)
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);  // empty args array
        il.Emit(OpCodes.Call, runtime.InvokeMethodValue);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(noGetterInPDSLabel);

        // Try to get descriptor: PDSGetPropertyDescriptor(obj, name)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
        il.Emit(OpCodes.Stloc, pdsDescriptorLocal);

        // If descriptor is null, continue to next checks
        il.Emit(OpCodes.Ldloc, pdsDescriptorLocal);
        il.Emit(OpCodes.Brfalse, afterPDSCheckLabel);

        // Descriptor found - return descriptor.Value
        il.Emit(OpCodes.Ldloc, pdsDescriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetGetMethod()!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(afterPDSCheckLabel);

        // If obj is emitted $Object, query its _fields dictionary directly.
        // If property not found in _fields, fall through to $IHasFields check
        // (since $Object subclasses may have typed properties with backing fields)
        var notTSObjectLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brfalse, notTSObjectLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectFieldsGetter);
        il.Emit(OpCodes.Stloc, objectFieldsLocal);

        il.Emit(OpCodes.Ldloc, objectFieldsLocal);
        il.Emit(OpCodes.Brfalse, notTSObjectLabel);  // Changed: check $IHasFields if _fields is null

        il.Emit(OpCodes.Ldloc, objectFieldsLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "TryGetValue"));
        il.Emit(OpCodes.Brfalse, notTSObjectLabel);  // Changed: check $IHasFields if not in _fields
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notTSObjectLabel);

        // Check plain Dictionary<string, object?> (vm.Script objects, CreateObject results, etc.)
        var notDictLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, notDictLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "TryGetValue"));
        il.Emit(OpCodes.Brfalse, notDictLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notDictLabel);

        // Check $IHasFields interface (covers user-defined classes and $Object subclasses with typed properties)
        var notHasFieldsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brfalse, notHasFieldsLabel);

        // Call interface method: ((IHasFields)obj).GetProperty(name)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.IHasFieldsGetProperty);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notHasFieldsLabel);

        // Check $Error - handle name, message, stack properties
        var notErrorLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSErrorType);
        il.Emit(OpCodes.Brfalse, notErrorLabel);

        // Check "name"
        var notErrorNameLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorNameLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorNameGetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notErrorNameLabel);

        // Check "message"
        var notErrorMessageLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "message");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorMessageLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorMessageGetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notErrorMessageLabel);

        // Check "stack"
        var notErrorStackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "stack");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorStackLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorStackGetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notErrorStackLabel);

        // Check "code" — only return if non-null (absent on plain Error objects)
        var notErrorCodeNameLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "code");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorCodeNameLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorCodeGetter);
        var codeNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, codeNullLabel);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(codeNullLabel);
        il.Emit(OpCodes.Pop); // discard null from Dup
        il.MarkLabel(notErrorCodeNameLabel);

        // Check "syscall" — only return if non-null
        var notErrorSyscallNameLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "syscall");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorSyscallNameLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorSyscallGetter);
        var syscallNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, syscallNullLabel);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(syscallNullLabel);
        il.Emit(OpCodes.Pop); // discard null from Dup
        il.MarkLabel(notErrorSyscallNameLabel);

        il.MarkLabel(notErrorLabel);

        // $StringDecoder dispatch removed — StringDecoder migrated to
        // stdlib/node/string_decoder.ts. Its instances now go through the
        // standard user-class property dispatch path like any other TS class.

        // Try to find a method with this name and wrap as TSFunction
        il.MarkLabel(tryMethodLabel);

        // First try array methods if it's an array
        var noArrayMethodLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.GetArrayMethod);
        var arrayMethodLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, arrayMethodLocal);
        il.Emit(OpCodes.Ldloc, arrayMethodLocal);
        il.Emit(OpCodes.Brfalse, noArrayMethodLabel);
        il.Emit(OpCodes.Ldloc, arrayMethodLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(noArrayMethodLabel);

        // Skip all .NET reflection fallbacks for System.Type instances. An
        // emitted `Array` / user-class token bound to a local becomes a
        // System.Type at runtime; `P_0.GetType()` returns RuntimeType, whose
        // .NET properties (IsArray, IsClass, Name, FullName, …) would
        // otherwise bleed through as JS property values — notably making
        // `var f = Array.isArray` resolve to the boolean `false` because
        // IgnoreCase matches System.Type.IsArray and returns its value
        // for typeof(IList<object>). The legitimate static-member lookups
        // for Type live in EmitGetProperty's Type branch (static method
        // → $TSFunction, static field → value, "name" → type.Name) and
        // already ran before falling through here; no further reflection
        // is correct for a Type. Returning $Undefined.Instance matches
        // ECMAScript §7.3.2 Get for absent properties.
        var skipTypeReflectionLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Type);
        il.Emit(OpCodes.Brtrue, skipTypeReflectionLabel);

        // Fallback: Try reflection-based property access for runtime-emitted types
        // This handles types like $Readable, $Writable, $Duplex that don't implement $IHasFields

        // Convert camelCase name to PascalCase for .NET property lookup
        // e.g., "readable" -> "Readable", "readableEnded" -> "ReadableEnded"
        var pascalNameLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ToPascalCase);
        il.Emit(OpCodes.Stloc, pascalNameLocal);

        // Try to get property: obj.GetType().GetProperty(pascalName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
        var propertyInfoLocal = il.DeclareLocal(_types.PropertyInfo);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldloc, pascalNameLocal);
        il.Emit(OpCodes.Ldc_I4, (int)(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String, typeof(System.Reflection.BindingFlags)));
        il.Emit(OpCodes.Stloc, propertyInfoLocal);

        // If property found, call getter
        var noPropertyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, propertyInfoLocal);
        il.Emit(OpCodes.Brfalse, noPropertyLabel);

        // return propertyInfo.GetValue(obj)
        il.Emit(OpCodes.Ldloc, propertyInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(noPropertyLabel);

        // Fallback: Try reflection-based method lookup for runtime-emitted types
        // This handles methods like Push, Pipe, etc. on $Readable, $Writable, $Dir, etc.
        // Uses SafeGetMethod so overloaded methods (e.g. Guid.ToString, StringBuilder.Append)
        // don't crash with AmbiguousMatchException.
        var methodInfoLocal = il.DeclareLocal(_types.MethodInfo);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldloc, pascalNameLocal);
        il.Emit(OpCodes.Ldc_I4, (int)(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase));
        il.Emit(OpCodes.Call, runtime.SafeGetMethod);
        il.Emit(OpCodes.Stloc, methodInfoLocal);

        // If method found, wrap in $TSFunction and return
        var noMethodLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Brfalse, noMethodLabel);

        // Wrap in $TSFunction: new $TSFunction(target, methodInfo)
        il.Emit(OpCodes.Ldarg_0);  // target object
        il.Emit(OpCodes.Ldloc, methodInfoLocal);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(noMethodLabel);

        // Fallback: Try GetMember(string) method for types like $DiffieHellman, $ECDH
        // that expose properties only through their GetMember dispatch method.
        var getMemberLocal = il.DeclareLocal(_types.MethodInfo);
        var noGetMemberLabel = il.DefineLabel();

        // Guard: if obj is a System.Type (e.g. a compiled class reference used as
        // a dynamic target), its inherited GetMember overloads cause
        // AmbiguousMatchException from GetMethod(name, flags). Skip the fallback
        // for Type instances — their PropertyDescriptorStore entries (if any)
        // were already consulted above.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Type);
        il.Emit(OpCodes.Brtrue, noGetMemberLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "GetMember");
        il.Emit(OpCodes.Ldc_I4, (int)(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
        il.Emit(OpCodes.Call, runtime.SafeGetMethod);
        il.Emit(OpCodes.Stloc, getMemberLocal);

        il.Emit(OpCodes.Ldloc, getMemberLocal);
        il.Emit(OpCodes.Brfalse, noGetMemberLabel);

        // Call GetMember(name): methodInfo.Invoke(obj, new object[] { name })
        var getMemberResultLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, getMemberLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1); // name
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Stloc, getMemberResultLocal);

        // If result is null, fall through to undefined
        var getMemberNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, getMemberResultLocal);
        il.Emit(OpCodes.Brfalse, getMemberNullLabel);

        // If result is $TSFunction or $BoundTSFunction, return as-is (fast path)
        var returnCallableAsIsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, getMemberResultLocal);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue_S, returnCallableAsIsLabel);
        il.Emit(OpCodes.Ldloc, getMemberResultLocal);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brtrue_S, returnCallableAsIsLabel);

        // Check if result has a "Call" method — if so it's a callable (BuiltInMethod etc.)
        // and should be wrapped in $MethodCallable for dispatch through InvokeMethodValue.
        // Objects without "Call" (property values like SearchParams) are returned as-is.
        var returnAsIsLabel = il.DefineLabel();
        var callMethodLocal = il.DeclareLocal(_types.MethodInfo);
        il.Emit(OpCodes.Ldloc, getMemberResultLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "Call");
        il.Emit(OpCodes.Ldc_I4, (int)(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
        il.Emit(OpCodes.Call, runtime.SafeGetMethod);
        il.Emit(OpCodes.Stloc, callMethodLocal);
        il.Emit(OpCodes.Ldloc, callMethodLocal);
        il.Emit(OpCodes.Brfalse, returnAsIsLabel);

        // Has "Call" method → wrap in $MethodCallable
        il.Emit(OpCodes.Ldloc, getMemberResultLocal);
        il.Emit(OpCodes.Newobj, runtime.MethodCallableCtor);
        il.Emit(OpCodes.Ret);

        // Return $TSFunction/$BoundTSFunction as-is
        il.MarkLabel(returnCallableAsIsLabel);
        il.Emit(OpCodes.Ldloc, getMemberResultLocal);
        il.Emit(OpCodes.Ret);

        // Return non-callable values as-is
        il.MarkLabel(returnAsIsLabel);
        il.Emit(OpCodes.Ldloc, getMemberResultLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(getMemberNullLabel);

        il.MarkLabel(noGetMemberLabel);
        il.MarkLabel(nullLabel);
        il.MarkLabel(skipTypeReflectionLabel);
        // Return $Undefined.Instance for non-existent properties (JavaScript semantics)
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Ret);
    }

    private void EmitSetFieldsProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // SetFieldsProperty(object obj, string name, object value) -> void
        // Updates class-instance state through emitted runtime state only:
        // descriptor store checks and emitted $Object fields.
        var method = typeBuilder.DefineMethod(
            "SetFieldsProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.Object, _types.String, _types.Object]
        );
        runtime.SetFieldsProperty = method;

        var il = method.GetILGenerator();
        var endLabel = il.DefineLabel();
        var tryFieldsLabel = il.DefineLabel();
        var trySetterLabel = il.DefineLabel();

        // Declare locals upfront
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var frozenCheckLocal = il.DeclareLocal(_types.Object);

        // if (obj == null) return;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, endLabel);

        // Check if frozen: _frozenObjects.TryGetValue(obj, out _)
        // If frozen, silently return (non-strict mode behavior)
        il.Emit(OpCodes.Ldsfld, runtime.FrozenObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, frozenCheckLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        il.Emit(OpCodes.Brtrue, endLabel); // Frozen - silently return

        // No additional setter fallback in standalone mode.
        il.MarkLabel(trySetterLabel);

        // Try _fields dictionary - walk up type hierarchy to find non-null _fields
        il.MarkLabel(tryFieldsLabel);
        var notTSObjectLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brfalse, notTSObjectLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectFieldsGetter);
        il.Emit(OpCodes.Stloc, dictLocal);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, endLabel);

        // Found a non-null _fields dictionary
        // Check if sealed: _sealedObjects.TryGetValue(obj, out _)
        var doSetFieldLabel = il.DefineLabel();
        var checkExtensibilityLabel = il.DefineLabel();
        var sealedCheckLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldsfld, runtime.SealedObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, sealedCheckLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, checkExtensibilityLabel); // Not sealed, check extensibility

        // Object is sealed - check if property exists in dictionary
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldarg_1); // name
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "ContainsKey", _types.String));
        il.Emit(OpCodes.Brfalse, endLabel); // Property doesn't exist on sealed object, silently return
        il.Emit(OpCodes.Br, doSetFieldLabel); // Property exists, proceed to set

        // Not sealed - check extensibility via $PropertyDescriptorStore - fully standalone, no reflection
        il.MarkLabel(checkExtensibilityLabel);
        il.Emit(OpCodes.Ldarg_0); // obj
        il.Emit(OpCodes.Ldarg_1); // name
        il.Emit(OpCodes.Call, runtime.PDSCanAddProperty);
        il.Emit(OpCodes.Brfalse, endLabel); // Cannot add property, silently return

        // Set the value: dict[name] = value;
        il.MarkLabel(doSetFieldLabel);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notTSObjectLabel);

        // Check $IHasFields interface (covers user-defined classes)
        var notHasFieldsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brfalse, notHasFieldsLabel);

        // Check extensibility before setting (handles sealed/non-extensible objects)
        il.Emit(OpCodes.Ldarg_0); // obj
        il.Emit(OpCodes.Ldarg_1); // name
        il.Emit(OpCodes.Call, runtime.PDSCanAddProperty);
        il.Emit(OpCodes.Brfalse, endLabel); // Cannot add property, silently return

        // Call interface method: ((IHasFields)obj).SetProperty(name, value)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.IHasFieldsSetProperty);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notHasFieldsLabel);

        // Check $Error - handle name, message, stack properties
        var notErrorLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSErrorType);
        il.Emit(OpCodes.Brfalse, notErrorLabel);

        // Check "name"
        var notErrorNameLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorNameLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorNameSetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notErrorNameLabel);

        // Check "message"
        var notErrorMessageLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "message");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorMessageLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorMessageSetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notErrorMessageLabel);

        // Check "stack"
        var notErrorStackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "stack");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorStackLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorStackSetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notErrorStackLabel);

        // Check "code"
        var notErrorCodeSetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "code");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorCodeSetLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorCodeSetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notErrorCodeSetLabel);

        // Check "syscall"
        var notErrorSyscallSetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "syscall");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorSyscallSetLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorSyscallSetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notErrorSyscallSetLabel);

        il.MarkLabel(notErrorLabel);

        // Fallback: Try SetMember(string, object) method for types like $HttpResponse
        // that expose property setters through their SetMember dispatch method.
        var setMemberLocal = il.DeclareLocal(_types.MethodInfo);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "SetMember");
        il.Emit(OpCodes.Ldc_I4, (int)(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
        il.Emit(OpCodes.Call, runtime.SafeGetMethod);
        il.Emit(OpCodes.Stloc, setMemberLocal);

        il.Emit(OpCodes.Ldloc, setMemberLocal);
        il.Emit(OpCodes.Brfalse, endLabel);

        // Call SetMember(name, value): methodInfo.Invoke(obj, new object[] { name, value })
        il.Emit(OpCodes.Ldloc, setMemberLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1); // name
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_2); // value
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Pop);

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits SetFieldsPropertyStrict(object obj, string name, object value, bool strictMode) -> void
    /// In strict mode, throws TypeError for modifications to frozen objects.
    /// </summary>
    private void EmitSetFieldsPropertyStrict(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SetFieldsPropertyStrict",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.Object, _types.String, _types.Object, _types.Boolean]
        );
        runtime.SetFieldsPropertyStrict = method;

        var il = method.GetILGenerator();
        var endLabel = il.DefineLabel();
        var notFrozenLabel = il.DefineLabel();
        var tryFieldsLabel = il.DefineLabel();

        // Declare locals upfront
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var frozenCheckLocal = il.DeclareLocal(_types.Object);

        // if (obj == null) return;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, endLabel);

        // Check if frozen: _frozenObjects.TryGetValue(obj, out _)
        il.Emit(OpCodes.Ldsfld, runtime.FrozenObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, frozenCheckLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, notFrozenLabel);

        // Frozen - check strictMode and throw if true
        il.Emit(OpCodes.Ldarg_3); // strictMode
        var frozenSilentLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, frozenSilentLabel);
        il.Emit(OpCodes.Ldstr, "Cannot assign to read only property '");
        il.Emit(OpCodes.Ldarg_1); // name
        il.Emit(OpCodes.Ldstr, "' of object");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String, _types.String));
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(frozenSilentLabel);
        il.Emit(OpCodes.Ret); // Silently return in non-strict mode

        il.MarkLabel(notFrozenLabel);

        // No reflection setter fallback in standalone mode.

        // Try _fields dictionary - walk up type hierarchy to find non-null _fields
        il.MarkLabel(tryFieldsLabel);

        var notTSObjectLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brfalse, notTSObjectLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectFieldsGetter);
        il.Emit(OpCodes.Stloc, dictLocal);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, endLabel);

        // Found a non-null _fields dictionary - set the value and return
        // dict[name] = value;
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notTSObjectLabel);

        // Check $IHasFields interface (covers user-defined classes)
        var notHasFieldsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brfalse, notHasFieldsLabel);

        // Check extensibility before setting (handles sealed/non-extensible objects)
        il.Emit(OpCodes.Ldarg_0); // obj
        il.Emit(OpCodes.Ldarg_1); // name
        il.Emit(OpCodes.Call, runtime.PDSCanAddProperty);
        il.Emit(OpCodes.Brfalse, endLabel); // Cannot add property, silently return

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.IHasFieldsSetProperty);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notHasFieldsLabel);

        // Check $Error - handle name, message, stack properties
        var notErrorLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSErrorType);
        il.Emit(OpCodes.Brfalse, notErrorLabel);

        var notErrorNameLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorNameLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorNameSetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notErrorNameLabel);

        var notErrorMessageLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "message");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorMessageLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorMessageSetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notErrorMessageLabel);

        var notErrorStackLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "stack");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notErrorStackLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSErrorType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, runtime.TSErrorStackSetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notErrorStackLabel);

        il.MarkLabel(notErrorLabel);

        // Fallback: Try SetMember(string, object) method for types like $HttpResponse
        var setMemberLocal = il.DeclareLocal(_types.MethodInfo);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "SetMember");
        il.Emit(OpCodes.Ldc_I4, (int)(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
        il.Emit(OpCodes.Call, runtime.SafeGetMethod);
        il.Emit(OpCodes.Stloc, setMemberLocal);

        il.Emit(OpCodes.Ldloc, setMemberLocal);
        il.Emit(OpCodes.Brfalse, endLabel);

        // Call SetMember(name, value): methodInfo.Invoke(obj, new object[] { name, value })
        il.Emit(OpCodes.Ldloc, setMemberLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1); // name
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_2); // value
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Pop);

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetListProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // GetListProperty(list: List<object>, name: string) -> object?
        // Returns length as double, or a $BoundArrayMethod for array methods
        var method = typeBuilder.DefineMethod(
            "GetListProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ListOfObject, _types.String]
        );
        runtime.GetListProperty = method;

        var il = method.GetILGenerator();

        var lengthLabel = il.DefineLabel();
        var returnNullLabel = il.DefineLabel();
        var createBoundMethodLabel = il.DefineLabel();

        // if (name == "length") goto lengthLabel
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, lengthLabel);

        // if (name == "raw") - check for TemplateStringsList.raw property
        var rawLabel = il.DefineLabel();
        var skipRawLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "raw");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, rawLabel);
        il.Emit(OpCodes.Br, skipRawLabel);

        il.MarkLabel(rawLabel);
        // For tagged template arrays, call emitted TemplateStringsList.raw getter.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TemplateStringsListType);
        il.Emit(OpCodes.Brfalse, returnNullLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TemplateStringsListType);
        il.Emit(OpCodes.Callvirt, runtime.TemplateStringsListRawGetter);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(skipRawLabel);

        // Check for known array method names
        // For each method name, if match, create $BoundArrayMethod
        // Must stay in sync with EmitBoundArrayMethodFinalize's dispatch switch
        // (RuntimeEmitter.Arrays.cs) and ArrayEmitter.cs static dispatch.
        string[] methodNames = [
            "join", "push", "pop", "shift", "unshift", "slice", "splice",
            "indexOf", "lastIndexOf", "includes", "concat", "reverse", "sort", "map", "filter", "forEach",
            "find", "findIndex", "findLast", "findLastIndex", "some", "every",
            "reduce", "reduceRight",
            "flat", "flatMap", "at",
            "toSorted", "toSpliced", "toReversed", "with",
            "fill", "copyWithin",
            "entries", "keys", "values"
        ];

        foreach (var methodName in methodNames)
        {
            var skipLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldstr, methodName);
            il.Emit(OpCodes.Call, _types.StringOpEquality);
            il.Emit(OpCodes.Brfalse, skipLabel);

            // Create $BoundArrayMethod(list, name) and return
            il.Emit(OpCodes.Ldarg_0); // list
            il.Emit(OpCodes.Ldarg_1); // name
            il.Emit(OpCodes.Newobj, runtime.BoundArrayMethodCtor);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(skipLabel);
        }

        // No known array method match — check PropertyDescriptorStore for custom defined properties
        il.MarkLabel(returnNullLabel);
        var reallyNullLabel = il.DefineLabel();
        var pdsDescLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        il.Emit(OpCodes.Ldarg_0); // list (as object)
        il.Emit(OpCodes.Ldarg_1); // name
        il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
        il.Emit(OpCodes.Stloc, pdsDescLocal);
        il.Emit(OpCodes.Ldloc, pdsDescLocal);
        il.Emit(OpCodes.Brfalse, reallyNullLabel);
        // Return descriptor.Value
        il.Emit(OpCodes.Ldloc, pdsDescLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetGetMethod()!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(reallyNullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        // length case: return (double)list.Count
        il.MarkLabel(lengthLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetPropertyGetter(_types.ListOfObject, "Count"));
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetMapProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // GetMapProperty(map: Dictionary<object,object>, name: string) -> object?
        // Returns size as double, or a $BoundMapMethod wrapper for known Map methods.
        // Mirrors GetListProperty — ensures duck typing works across module boundaries:
        // typeof map.get === 'function' and map.get.call(map, k) both work on a Map
        // received from another module.
        var method = typeBuilder.DefineMethod(
            "GetMapProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.DictionaryObjectObject, _types.String]
        );
        runtime.GetMapProperty = method;

        var il = method.GetILGenerator();

        var sizeLabel = il.DefineLabel();

        // if (name == "size") goto sizeLabel
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "size");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, sizeLabel);

        // For each known Map method name, return a $BoundMapMethod wrapper.
        string[] methodNames = ["get", "set", "has", "delete", "clear",
            "keys", "values", "entries", "forEach"];

        foreach (var methodName in methodNames)
        {
            var skipLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldstr, methodName);
            il.Emit(OpCodes.Call, _types.StringOpEquality);
            il.Emit(OpCodes.Brfalse, skipLabel);

            il.Emit(OpCodes.Ldarg_0); // map
            il.Emit(OpCodes.Ldarg_1); // name
            il.Emit(OpCodes.Newobj, runtime.BoundMapMethodCtor);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(skipLabel);
        }

        // Unknown property: return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        // size case: return (double)map.Count
        il.MarkLabel(sizeLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryObjectObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetSetProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // GetSetProperty(set: HashSet<object>, name: string) -> object?
        // Returns size as double, or a $BoundSetMethod wrapper for known Set methods
        // (including ES2025 set operations). Mirrors GetListProperty / GetMapProperty.
        var method = typeBuilder.DefineMethod(
            "GetSetProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.HashSetOfObject, _types.String]
        );
        runtime.GetSetProperty = method;

        var il = method.GetILGenerator();

        var sizeLabel = il.DefineLabel();

        // if (name == "size") goto sizeLabel
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "size");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, sizeLabel);

        // For each known Set method name, return a $BoundSetMethod wrapper.
        string[] methodNames = ["add", "has", "delete", "clear",
            "keys", "values", "entries", "forEach",
            "union", "intersection", "difference", "symmetricDifference",
            "isSubsetOf", "isSupersetOf", "isDisjointFrom"];

        foreach (var methodName in methodNames)
        {
            var skipLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldstr, methodName);
            il.Emit(OpCodes.Call, _types.StringOpEquality);
            il.Emit(OpCodes.Brfalse, skipLabel);

            il.Emit(OpCodes.Ldarg_0); // set
            il.Emit(OpCodes.Ldarg_1); // name
            il.Emit(OpCodes.Newobj, runtime.BoundSetMethodCtor);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(skipLabel);
        }

        // Unknown property: return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        // size case: return (double)set.Count
        il.MarkLabel(sizeLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.HashSetOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.String]
        );
        runtime.GetProperty = method;

        var il = method.GetILGenerator();
        var nullLabel = il.DefineLabel();
        var namespaceLabel = il.DefineLabel();
        var dictLabel = il.DefineLabel();
        var listLabel = il.DefineLabel();
        var stringLabel = il.DefineLabel();

        // null check
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // Proxy check: uses obj.GetType().FullName comparison (no SharpTS.dll dependency)
        var notProxyLabel = il.DefineLabel();
        EmitProxyGetPropertyCheck(il, () => il.Emit(OpCodes.Ldarg_0), () => il.Emit(OpCodes.Ldarg_1), notProxyLabel);

        il.MarkLabel(notProxyLabel);

        // $TSNamespace - call ns.Get(name)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSNamespaceType);
        il.Emit(OpCodes.Brtrue, namespaceLabel);

        // $Object (with getter/setter support) - call obj.GetProperty(name)
        var tsObjectLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brtrue, tsObjectLabel);

        // Map (Dictionary<object, object>) - check for "size" property
        var mapLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryObjectObject);
        il.Emit(OpCodes.Brtrue, mapLabel);

        // Set (HashSet<object>) - duck-typed access via GetSetProperty
        var setLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.HashSetOfObject);
        il.Emit(OpCodes.Brtrue, setLabel);

        // Dictionary (regular object)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // $Array - check for "length" (inherits List<object?>; MUST come
        // BEFORE the plain List check so sparse-aware length is used —
        // otherwise `new Array(10_000_000).length` returns 0).
        var sharpTSArrayLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSArrayType);
        il.Emit(OpCodes.Brtrue, sharpTSArrayLabel);

        // List - check for "length"
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brtrue, listLabel);

        // object[] (compiled `arguments` representation) - check for "length"
        var objectArrayLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ObjectArray);
        il.Emit(OpCodes.Brtrue, objectArrayLabel);

        // String - check for "length"
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, stringLabel);

        // $Buffer - check for "length" and "toString"
        var tsBufferLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSBufferType);
        il.Emit(OpCodes.Brtrue, tsBufferLabel);

        // $Stats - check for isFile, isDirectory, size, etc.
        var tsStatsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.StatsType);
        il.Emit(OpCodes.Brtrue, tsStatsLabel);

        // $TSFunction - check for bind/call/apply
        var tsFunctionLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, tsFunctionLabel);

        // $BoundTSFunction - also check for bind/call/apply
        var boundFunctionLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundTSFunctionType);
        il.Emit(OpCodes.Brtrue, boundFunctionLabel);

        // $CJSModule - route to the module's GetMember(name) for exports/id/filename/etc.
        var cjsModuleGetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.CjsModuleType);
        il.Emit(OpCodes.Brtrue, cjsModuleGetLabel);

        // Task<object?> (Promise) - check for then/catch/finally
        var promiseLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.TaskOfObject);
        il.Emit(OpCodes.Brtrue, promiseLabel);

        // $Promise type (used by fetch, etc.) - check for then/catch/finally
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSPromiseType);
        il.Emit(OpCodes.Brtrue, promiseLabel);

        // $ArrayBuffer - check for "byteLength"
        var arrayBufferLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.ArrayBufferType);
        il.Emit(OpCodes.Brtrue, arrayBufferLabel);

        // $SharedArrayBuffer - check for "byteLength"
        var sharedArrayBufferLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.SharedArrayBufferType);
        il.Emit(OpCodes.Brtrue, sharedArrayBufferLabel);

        // $DataView - check for "byteLength", "byteOffset", "buffer"
        var dataViewLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.DataViewType);
        il.Emit(OpCodes.Brtrue, dataViewLabel);

        // TypedArray - use emitted helper dispatch for standalone behavior
        var typedArrayLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.IsTypedArrayMethod);
        il.Emit(OpCodes.Brtrue, typedArrayLabel);

        // $Bound*Method and $BoundAnyFunction - callable wrappers that need .call/.apply/.bind
        // support. Route through GetFunctionMethod which handles bind/call/apply/length/name.
        // Bound methods already capture their receiver, so thisArg passed to .call/.apply is
        // ignored per JS bound-callable semantics — the CallWrapper/ApplyWrapper Invoke bodies
        // implement that via EmitDispatchToTarget.
        var callableWrapperLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundArrayMethodType);
        il.Emit(OpCodes.Brtrue, callableWrapperLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundMapMethodType);
        il.Emit(OpCodes.Brtrue, callableWrapperLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundSetMethodType);
        il.Emit(OpCodes.Brtrue, callableWrapperLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundAnyFunctionType);
        il.Emit(OpCodes.Brtrue, callableWrapperLabel);

        // System.Type (a class reference used as a value, e.g. `Scalar.PLAIN = 'x'` then
        // reading `Scalar.PLAIN`). JS allows arbitrary static property assignment on classes;
        // we store them in $PropertyDescriptorStore. Check PDS first; if no descriptor, fall
        // through to class-instance resolver (which on a Type will read its .NET members).
        var typeGetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Type);
        il.Emit(OpCodes.Brtrue, typeGetLabel);

        // Default - try class-instance fields/property resolution helper
        var classInstanceLabel = il.DefineLabel();
        il.Emit(OpCodes.Br, classInstanceLabel);

        // Class instance handler
        il.MarkLabel(classInstanceLabel);
        // Call GetFieldsProperty(obj, name) helper
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.GetFieldsProperty);
        il.Emit(OpCodes.Ret);

        // System.Type handler: check PropertyDescriptorStore first for user-added static
        // properties (`ClassName.foo = 'bar'`). Then look up declared static methods,
        // fields, and accessors on the .NET Type via reflection. Only falls through to
        // the class-instance handler (which returns Undefined for Type) if none match.
        //
        // Without the reflection step, `const Alias = Foo; Alias.bar()` and
        // `require('./mod').Cls.staticMethod()` silently bind to undefined — compiled
        // classes are emitted as System.Type tokens, so static access must walk the Type.
        il.MarkLabel(typeGetLabel);
        {
            // Boolean/Number/String.prototype — return the per-type singleton
            // Dictionary so writes/reads round-trip. Stage 4w: required for
            // test262 patterns like `Boolean.prototype[0] = true; Boolean.prototype.length = 1;
            // Array.prototype.every.call(false, cb)` to surface the customization
            // when the materializer falls back to the prototype for primitive
            // receivers. Check first, before PDS lookup, so the singleton wins
            // over any user-stored "prototype" descriptor.
            var notProtoNameLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldstr, "prototype");
            il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
            il.Emit(OpCodes.Brfalse, notProtoNameLabel);
            // typeof Boolean
            var notBoolLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldtoken, _types.Boolean);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
            il.Emit(OpCodes.Bne_Un, notBoolLabel);
            il.Emit(OpCodes.Ldsfld, runtime.BooleanPrototypeField);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(notBoolLabel);
            var notDoubleLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldtoken, _types.Double);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
            il.Emit(OpCodes.Bne_Un, notDoubleLabel);
            il.Emit(OpCodes.Ldsfld, runtime.NumberPrototypeField);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(notDoubleLabel);
            var notStringLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldtoken, _types.String);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
            il.Emit(OpCodes.Bne_Un, notStringLabel);
            il.Emit(OpCodes.Ldsfld, runtime.StringPrototypeField);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(notStringLabel);
            il.MarkLabel(notProtoNameLabel);

            var typePdsDescLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
            il.Emit(OpCodes.Stloc, typePdsDescLocal);
            il.Emit(OpCodes.Ldloc, typePdsDescLocal);
            var noTypePdsLabel = il.DefineLabel();
            il.Emit(OpCodes.Brfalse, noTypePdsLabel);
            il.Emit(OpCodes.Ldloc, typePdsDescLocal);
            il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetGetMethod()!);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(noTypePdsLabel);

            // Cache the casted Type reference for the three reflection probes below.
            var typeLocal = il.DeclareLocal(_types.Type);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, _types.Type);
            il.Emit(OpCodes.Stloc, typeLocal);

            const BindingFlags staticPublic = BindingFlags.Public | BindingFlags.Static;

            // Static method: SafeGetMethod(type, name, Public|Static).
            // SafeGetMethod handles AmbiguousMatchException deterministically, which matters
            // because user-declared statics can collide with inherited Type overloads.
            var staticMethodLocal = il.DeclareLocal(_types.MethodInfo);
            il.Emit(OpCodes.Ldloc, typeLocal);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, (int)staticPublic);
            il.Emit(OpCodes.Call, runtime.SafeGetMethod);
            il.Emit(OpCodes.Stloc, staticMethodLocal);

            var noStaticMethodLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, staticMethodLocal);
            il.Emit(OpCodes.Brfalse, noStaticMethodLabel);

            // Found a static method — wrap in $TSFunction(null, methodInfo) so callers
            // invoking it through InvokeValue/InvokeMethodValue treat it as a callable.
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldloc, staticMethodLocal);
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(noStaticMethodLabel);

            // Static field: type.GetField(name, Public|Static).
            var staticFieldLocal = il.DeclareLocal(typeof(FieldInfo));
            il.Emit(OpCodes.Ldloc, typeLocal);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, (int)staticPublic);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetField", _types.String, typeof(BindingFlags)));
            il.Emit(OpCodes.Stloc, staticFieldLocal);

            var noStaticFieldLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, staticFieldLocal);
            il.Emit(OpCodes.Brfalse, noStaticFieldLabel);

            // Found a static field — return field.GetValue(null).
            il.Emit(OpCodes.Ldloc, staticFieldLocal);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(typeof(FieldInfo), "GetValue", _types.Object));
            il.Emit(OpCodes.Ret);

            il.MarkLabel(noStaticFieldLabel);

            // Function.prototype.name: JS spec — classes expose their declared name as
            // `Foo.name === "Foo"`. Without this, `Class.name` falls through to Undefined
            // even though typeof(Class) === "function".
            var notClassNameLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldstr, "name");
            il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
            il.Emit(OpCodes.Brfalse, notClassNameLabel);
            il.Emit(OpCodes.Ldloc, typeLocal);
            il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.Type, "Name").GetGetMethod()!);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(notClassNameLabel);

            // Built-in static-member dispatch (#63): for Type tokens that
            // represent a JS-level built-in constructor (Array → IList<object>,
            // Number → double, String → string), look up (type, name) against
            // the runtime table that mirrors the compile-time static emitters.
            // This is what makes `var A = Array; A.isArray(x)` work — the
            // compile-time ArrayStaticEmitter only runs for bare `Array.isArray`.
            var builtInLocal = il.DeclareLocal(_types.Object);
            il.Emit(OpCodes.Ldloc, typeLocal);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, runtime.LookupBuiltInStaticMember);
            il.Emit(OpCodes.Stloc, builtInLocal);
            il.Emit(OpCodes.Ldloc, builtInLocal);
            var noBuiltInMatchLabel = il.DefineLabel();
            il.Emit(OpCodes.Brfalse, noBuiltInMatchLabel);
            il.Emit(OpCodes.Ldloc, builtInLocal);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(noBuiltInMatchLabel);

            // No static member matched — fall through to class-instance handler, which
            // on a Type returns Undefined (the intended absent-property signal).
            il.Emit(OpCodes.Br, classInstanceLabel);
        }

        // Callable wrapper handler: route .bind/.call/.apply/.length/.name through
        // GetFunctionMethod. Also handles .name specially for $BoundArrayMethod /
        // $BoundMapMethod / $BoundSetMethod by returning the captured method name,
        // which is more useful than GetFunctionMethod's empty-string fallback.
        il.MarkLabel(callableWrapperLabel);

        // Special-case "name" for bound methods — return the captured method name
        // (e.g. `map.get.name === 'get'`). GetFunctionMethod returns "" for unknown
        // callables, which is wrong for our wrappers.
        var notNameLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brfalse, notNameLabel);

        var notBAMNameLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundArrayMethodType);
        il.Emit(OpCodes.Brfalse, notBAMNameLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.BoundArrayMethodType);
        il.Emit(OpCodes.Ldfld, runtime.BoundArrayMethodNameField);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notBAMNameLabel);

        var notBMMNameLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundMapMethodType);
        il.Emit(OpCodes.Brfalse, notBMMNameLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.BoundMapMethodType);
        il.Emit(OpCodes.Ldfld, runtime.BoundMapMethodNameField);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notBMMNameLabel);

        var notBSMNameLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.BoundSetMethodType);
        il.Emit(OpCodes.Brfalse, notBSMNameLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.BoundSetMethodType);
        il.Emit(OpCodes.Ldfld, runtime.BoundSetMethodNameField);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notBSMNameLabel);

        // $BoundAnyFunction has no name field — fall through to GetFunctionMethod (returns "")

        il.MarkLabel(notNameLabel);

        // All other names (bind/call/apply/length/anything else) — delegate to GetFunctionMethod
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.GetFunctionMethod);
        il.Emit(OpCodes.Ret);

        // TypedArray handler - call emitted typed-array member helper
        il.MarkLabel(typedArrayLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.GetTypedArrayMemberMethod);
        il.Emit(OpCodes.Ret);

        // $ArrayBuffer handler - check for "byteLength"
        il.MarkLabel(arrayBufferLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "byteLength");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notArrayBufferByteLengthLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notArrayBufferByteLengthLabel);
        // Return ByteLength as double
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.ArrayBufferType);
        il.Emit(OpCodes.Callvirt, runtime.ArrayBufferByteLengthGetter);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notArrayBufferByteLengthLabel);
        // Return null for other properties
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        // $SharedArrayBuffer handler - check for "byteLength"
        il.MarkLabel(sharedArrayBufferLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "byteLength");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notSharedArrayBufferByteLengthLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notSharedArrayBufferByteLengthLabel);
        // Return ByteLength as double
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.SharedArrayBufferType);
        il.Emit(OpCodes.Callvirt, runtime.SharedArrayBufferByteLengthGetter);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notSharedArrayBufferByteLengthLabel);
        // Return null for other properties
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        // $DataView handler - check for "byteLength", "byteOffset", "buffer"
        il.MarkLabel(dataViewLabel);
        // Check "byteLength"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "byteLength");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notDataViewByteLengthLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notDataViewByteLengthLabel);
        // Return ByteLength as double
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.DataViewType);
        il.Emit(OpCodes.Callvirt, runtime.DataViewByteLengthGetter);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notDataViewByteLengthLabel);
        // Check "byteOffset"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "byteOffset");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notDataViewByteOffsetLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notDataViewByteOffsetLabel);
        // Return ByteOffset as double
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.DataViewType);
        il.Emit(OpCodes.Callvirt, runtime.DataViewByteOffsetGetter);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notDataViewByteOffsetLabel);
        // Check "buffer"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "buffer");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notDataViewBufferLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notDataViewBufferLabel);
        // Return Buffer
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.DataViewType);
        il.Emit(OpCodes.Callvirt, runtime.DataViewBufferGetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notDataViewBufferLabel);
        // Return null for other properties
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        // Namespace handler - call ns.Get(name)
        il.MarkLabel(namespaceLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSNamespaceType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSNamespaceGet);
        il.Emit(OpCodes.Ret);

        // $Object handler - call obj.GetProperty(name) which handles getters
        il.MarkLabel(tsObjectLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectGetProperty);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        // $TSFunction handler - call GetFunctionMethod(func, name)
        il.MarkLabel(tsFunctionLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.GetFunctionMethod);
        il.Emit(OpCodes.Ret);

        // $BoundTSFunction handler - call GetFunctionMethod(func, name)
        il.MarkLabel(boundFunctionLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.GetFunctionMethod);
        il.Emit(OpCodes.Ret);

        // $CJSModule handler - call module.GetMember(name)
        il.MarkLabel(cjsModuleGetLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.CjsModuleType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.CjsModuleType.GetMethod("GetMember", [_types.String])!);
        il.Emit(OpCodes.Ret);

        // Promise (Task<object?> or $Promise) handler - return TSFunction wrappers for then/catch/finally
        il.MarkLabel(promiseLabel);
        // First, extract the underlying Task if this is a $Promise object
        // Store the task in a local variable for use in creating TSFunction wrappers
        var taskLocal = il.DeclareLocal(_types.TaskOfObject);
        var isTSPromiseLabel = il.DefineLabel();
        var haveTaskLabel = il.DefineLabel();

        // Check if obj is $Promise
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSPromiseType);
        il.Emit(OpCodes.Brtrue, isTSPromiseLabel);

        // It's a raw Task<object?>, use directly
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.TaskOfObject);
        il.Emit(OpCodes.Stloc, taskLocal);
        il.Emit(OpCodes.Br, haveTaskLabel);

        // It's a $Promise, extract the Task property
        il.MarkLabel(isTSPromiseLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSPromiseType);
        il.Emit(OpCodes.Callvirt, runtime.TSPromiseTaskGetter);
        il.Emit(OpCodes.Stloc, taskLocal);

        il.MarkLabel(haveTaskLabel);

        // Check for "then"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "then");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notPromiseThenLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notPromiseThenLabel);
        // Return TSFunction wrapper for PromiseThen with the task as target
        il.Emit(OpCodes.Ldloc, taskLocal);  // target (the underlying task)
        il.Emit(OpCodes.Ldtoken, runtime.PromiseThen);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notPromiseThenLabel);

        // Check for "catch"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "catch");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notPromiseCatchLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notPromiseCatchLabel);
        // Return TSFunction wrapper for PromiseCatch with the task as target
        il.Emit(OpCodes.Ldloc, taskLocal);  // target (the underlying task)
        il.Emit(OpCodes.Ldtoken, runtime.PromiseCatch);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notPromiseCatchLabel);

        // Check for "finally"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "finally");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notPromiseFinallyLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notPromiseFinallyLabel);
        // Return TSFunction wrapper for PromiseFinally with the task as target
        il.Emit(OpCodes.Ldloc, taskLocal);  // target (the underlying task)
        il.Emit(OpCodes.Ldtoken, runtime.PromiseFinally);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notPromiseFinallyLabel);

        // Unknown promise property - return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictLabel);
        // Check for getter accessor via $PropertyDescriptorStore - fully standalone, no reflection
        var getterLocal = il.DeclareLocal(_types.Object);
        var noGetterLabel = il.DefineLabel();

        // Call PDSTryGetGetter(obj, name, out getter)
        il.Emit(OpCodes.Ldarg_0);  // obj
        il.Emit(OpCodes.Ldarg_1);  // name
        il.Emit(OpCodes.Ldloca, getterLocal);  // out getter
        il.Emit(OpCodes.Call, runtime.PDSTryGetGetter);
        il.Emit(OpCodes.Brfalse, noGetterLabel);

        // Getter was found - invoke it via InvokeMethodValue(obj, getter, emptyArgs)
        il.Emit(OpCodes.Ldarg_0);  // receiver (obj)
        il.Emit(OpCodes.Ldloc, getterLocal);  // function (getter)
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);  // empty args array
        il.Emit(OpCodes.Call, runtime.InvokeMethodValue);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(noGetterLabel);

        // dict.TryGetValue(name, out value) ? value : check prototype chain
        var valueLocal = il.DeclareLocal(_types.Object);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var protoLocal = il.DeclareLocal(_types.Object);

        // Store the dictionary in a local for later use with BindThis
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "TryGetValue"));
        var foundLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, foundLabel);

        // Property not found on object - check prototype chain
        // Get prototype: $PropertyDescriptorStore.GetPrototype(obj)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSGetPrototype);
        il.Emit(OpCodes.Stloc, protoLocal);

        // If prototype is null, return undefined
        var returnUndefinedLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, protoLocal);
        il.Emit(OpCodes.Brfalse, returnUndefinedLabel);

        // Recursively call GetProperty(prototype, name) to check prototype chain
        il.Emit(OpCodes.Ldloc, protoLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, method);  // Recursive call to GetProperty
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnUndefinedLabel);
        // ECMA-262: `({}).constructor === Object`. If user hasn't set a custom
        // constructor and no prototype overrides it, return typeof(object) which
        // matches what compiled-mode `Object` resolves to via globalThis.
        var notDictCtorLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "constructor");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notDictCtorLabel);
        il.Emit(OpCodes.Ldtoken, _types.Object);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notDictCtorLabel);
        // Return $Undefined.Instance for non-existent properties (JavaScript semantics)
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(foundLabel);

        // If value is a TSFunction, call BindThis(dict) on it
        // to bind 'this' for object method shorthand
        var notTSFunction = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brfalse, notTSFunction);

        // Call func.BindThis(dict)
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionBindThis);

        il.MarkLabel(notTSFunction);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Ret);

        // Map (Dictionary<object, object>) handler - check for "size" property
        il.MarkLabel(mapLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "size");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notMapSizeLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notMapSizeLabel);
        // Return map.Count as double
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryObjectObject);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryObjectObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notMapSizeLabel);
        // For other Map properties, dispatch via GetMapProperty — returns a $BoundMapMethod
        // wrapper for known methods (get/set/has/...) so that `typeof m.get === 'function'`
        // and `m.get.call(m, k)` work on a Map received from another module.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryObjectObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.GetMapProperty);
        il.Emit(OpCodes.Ret);

        // Set (HashSet<object>) handler - dispatch via GetSetProperty for size +
        // $BoundSetMethod wrappers on known methods (add/has/delete/... plus ES2025
        // set ops). Mirrors the Map handler above.
        il.MarkLabel(setLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.HashSetOfObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.GetSetProperty);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(listLabel);
        // Check for "length"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notLengthLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notLengthLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notLengthLabel);
        // ECMA-262: `[].constructor === Array`. Compiled `Array` resolves to
        // typeof(IList<object>) — return that here.
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "constructor");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notListCtorLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notListCtorLabel);
        il.Emit(OpCodes.Ldtoken, _types.IListOfObject);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notListCtorLabel);
        // For other properties on List (like methods push, pop, etc.), use GetListProperty
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.GetListProperty);
        il.Emit(OpCodes.Ret);

        // object[] handler — `arguments`-shape receiver. "length" → array.Length;
        // numeric-string indexes return the element; anything else → undefined.
        il.MarkLabel(objectArrayLabel);
        var objArrNotLengthLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, objArrNotLengthLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ObjectArray);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(objArrNotLengthLabel);
        // Try numeric-string index: int.TryParse(name, out i)
        var objArrIdxLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, objArrIdxLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Int32, "TryParse", _types.String, _types.Int32.MakeByRefType()));
        var objArrNotIndexLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, objArrNotIndexLabel);
        // Bounds check: i >= 0 && i < arr.Length
        il.Emit(OpCodes.Ldloc, objArrIdxLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, objArrNotIndexLabel);
        il.Emit(OpCodes.Ldloc, objArrIdxLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ObjectArray);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Bge, objArrNotIndexLabel);
        // Read element at index
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ObjectArray);
        il.Emit(OpCodes.Ldloc, objArrIdxLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(objArrNotIndexLabel);
        // Other property → undefined
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Ret);

        // $Array handler - access Elements.Count for "length"
        il.MarkLabel(sharpTSArrayLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notSharpTSArrayLengthLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notSharpTSArrayLengthLabel);
        // Use the LongLength getter — not the int-clamped Length — so `.length`
        // reads up to 2^32 - 1 survive (M3 acceptance: `a.length === 2147483649`).
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSArrayType);
        il.Emit(OpCodes.Callvirt, runtime.TSArrayLongLengthGetter);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notSharpTSArrayLengthLabel);
        // ECMA-262: `[].constructor === Array`. Mirror the listLabel branch.
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "constructor");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notTSArrayCtorLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notTSArrayCtorLabel);
        il.Emit(OpCodes.Ldtoken, _types.IListOfObject);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notTSArrayCtorLabel);
        // For other properties on $Array (method names like push/pop/sort/etc.),
        // reuse GetListProperty — it returns the $BoundArrayMethod wrapper, and
        // $Array IS a List<object?> by inheritance, so the cast works.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.GetListProperty);
        il.Emit(OpCodes.Ret);

        // $Buffer handler - "length" and "toString"
        il.MarkLabel(tsBufferLabel);
        // Check for "length"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notBufferLenLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notBufferLenLabel);
        // Get buf.Length
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSBufferType);
        il.Emit(OpCodes.Call, runtime.TSBufferLengthGetter);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notBufferLenLabel);
        // Check for "toString" - return a wrapper that calls ToEncodedString
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "toString");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notBufferToStringLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notBufferToStringLabel);
        // Create a TSFunction wrapper for ToEncodedString
        // For dynamically generated types, we need both method and type tokens
        il.Emit(OpCodes.Ldarg_0);  // target (the buffer)
        il.Emit(OpCodes.Ldtoken, runtime.TSBufferToString);
        il.Emit(OpCodes.Ldtoken, runtime.TSBufferType);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notBufferToStringLabel);
        // Unknown buffer property - return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        // $Stats handler - return method wrappers or property values
        il.MarkLabel(tsStatsLabel);
        // Check for "size" property
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "size");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notStatsSizeLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notStatsSizeLabel);
        // Return stats.size
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.StatsType);
        il.Emit(OpCodes.Call, runtime.StatsSizeGetter);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notStatsSizeLabel);
        // Check for "isFile" method - return TSFunction wrapper
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "isFile");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notStatsIsFileLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notStatsIsFileLabel);
        // Create TSFunction wrapper for isFile
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, runtime.StatsIsFile);
        il.Emit(OpCodes.Ldtoken, runtime.StatsType);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notStatsIsFileLabel);
        // Check for "isDirectory" method
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "isDirectory");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notStatsIsDirLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notStatsIsDirLabel);
        // Create TSFunction wrapper for isDirectory
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, runtime.StatsIsDirectory);
        il.Emit(OpCodes.Ldtoken, runtime.StatsType);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notStatsIsDirLabel);
        // Check for "isSymbolicLink" method
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "isSymbolicLink");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notStatsIsSymlinkLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notStatsIsSymlinkLabel);
        // Create TSFunction wrapper for isSymbolicLink
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, runtime.StatsIsSymbolicLink);
        il.Emit(OpCodes.Ldtoken, runtime.StatsType);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notStatsIsSymlinkLabel);
        // Check for "isBlockDevice" method
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "isBlockDevice");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notStatsIsBlockLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notStatsIsBlockLabel);
        // Create TSFunction wrapper for isBlockDevice
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, runtime.StatsIsBlockDevice);
        il.Emit(OpCodes.Ldtoken, runtime.StatsType);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notStatsIsBlockLabel);
        // Check for "isCharacterDevice" method
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "isCharacterDevice");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notStatsIsCharLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notStatsIsCharLabel);
        // Create TSFunction wrapper for isCharacterDevice
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, runtime.StatsIsCharacterDevice);
        il.Emit(OpCodes.Ldtoken, runtime.StatsType);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notStatsIsCharLabel);
        // Check for "isFIFO" method
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "isFIFO");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notStatsIsFIFOLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notStatsIsFIFOLabel);
        // Create TSFunction wrapper for isFIFO
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, runtime.StatsIsFIFO);
        il.Emit(OpCodes.Ldtoken, runtime.StatsType);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notStatsIsFIFOLabel);
        // Check for "isSocket" method
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "isSocket");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notStatsIsSocketLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notStatsIsSocketLabel);
        // Create TSFunction wrapper for isSocket
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, runtime.StatsIsSocket);
        il.Emit(OpCodes.Ldtoken, runtime.StatsType);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notStatsIsSocketLabel);
        // Unknown stats property - return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(stringLabel);
        // Check for "length"
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notStrLenLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notStrLenLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.String, "Length").GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notStrLenLabel);
        // ECMA-262: `"hello".constructor === String`. Compiled mode resolves
        // bare `String` to `typeof(string)` — returning that here makes the
        // strict-equality check hold.
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "constructor");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var notStrCtorLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notStrCtorLabel);
        il.Emit(OpCodes.Ldtoken, _types.String);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notStrCtorLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    private void EmitSetProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SetProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.Object, _types.String, _types.Object]
        );
        runtime.SetProperty = method;

        var il = method.GetILGenerator();
        var nullLabel = il.DefineLabel();
        var dictLabel = il.DefineLabel();
        var tsObjectLabel = il.DefineLabel();

        // null check
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // Proxy check: uses obj.GetType().FullName comparison (no SharpTS.dll dependency)
        var notProxyLabel = il.DefineLabel();
        EmitProxySetPropertyCheck(il, () => il.Emit(OpCodes.Ldarg_0), () => il.Emit(OpCodes.Ldarg_1), () => il.Emit(OpCodes.Ldarg_2), notProxyLabel);

        il.MarkLabel(notProxyLabel);

        // $Object (with setter support) - call obj.SetProperty(name, value)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brtrue, tsObjectLabel);

        // $Array — special-case `arr.length = N` (route through $Array.SetLength
        // so truncation/extension uses the sparse-aware path). Other named-
        // property writes on arrays are silently ignored; ECMA-262 §22.1.5
        // permits them but most emitters don't preserve them and the spec
        // compiler tests don't exercise that corner. Must come BEFORE the
        // Dictionary check (since $Array inherits List<object?> which is
        // neither), and BEFORE SetFieldsProperty fallthrough.
        var tsArraySetPropLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSArrayType);
        il.Emit(OpCodes.Brtrue, tsArraySetPropLabel);

        // Dictionary
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // $TSFunction — JS functions are objects and support arbitrary property assignment
        // (`fn.x = 42`, `lodash.chunk = function(...){}`). Store as a data descriptor in
        // $PropertyDescriptorStore; GetFunctionMethod's fallback path reads it back. Without
        // this, the assignment would fall through to SetFieldsProperty which is a
        // class-instance path that doesn't match $TSFunction.
        var tsFunctionSetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, tsFunctionSetLabel);

        // $CJSModule — `module.exports = X` (or any aliased write) goes through here.
        // The exports setter writes through to the module's static $exports field via
        // reflection so require() sees the update. Other property writes are silently
        // ignored (spec: id/filename/loaded are informational and not writable from userland).
        var cjsModuleSetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.CjsModuleType);
        il.Emit(OpCodes.Brtrue, cjsModuleSetLabel);

        // System.Type (class reference used as value, e.g. `Scalar.PLAIN = 'x'`). JS allows
        // arbitrary static property assignment on classes; we store them in PropertyDescriptorStore.
        var typeSetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Type);
        il.Emit(OpCodes.Brtrue, typeSetLabel);

        // Not a dict or $Object or $TSFunction or $CJSModule or Type - try SetFieldsProperty for class instances
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, runtime.SetFieldsProperty);
        il.Emit(OpCodes.Ret);

        // System.Type handler: store as data descriptor in PropertyDescriptorStore.
        // Read path (EmitGetProperty) looks it up before falling through to .NET member
        // resolution, so writes become visible as reads.
        il.MarkLabel(typeSetLabel);
        {
            var typeDescLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
            il.Emit(OpCodes.Newobj, runtime.CompiledPropertyDescriptorCtor);
            il.Emit(OpCodes.Stloc, typeDescLocal);
            il.Emit(OpCodes.Ldloc, typeDescLocal);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetSetMethod()!);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc, typeDescLocal);
            il.Emit(OpCodes.Call, runtime.PDSDefineProperty);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        }

        // $CJSModule handler — only "exports" is writable; others are no-ops (spec behavior).
        il.MarkLabel(cjsModuleSetLabel);
        {
            var notExportsLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldstr, "exports");
            il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
            il.Emit(OpCodes.Brfalse, notExportsLabel);
            // module.exports = value
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, runtime.CjsModuleType);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, runtime.CjsModuleExportsSetter);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(notExportsLabel);
            // Silently ignore writes to other module properties
            il.Emit(OpCodes.Ret);
        }

        // $Array handler — `arr.length = N` routes through SetLength. Any
        // other name falls off into the normal silent-ignore (JS permits
        // arbitrary named writes on arrays but we don't persist them).
        il.MarkLabel(tsArraySetPropLabel);
        {
            var notLengthLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldstr, "length");
            il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
            il.Emit(OpCodes.Brfalse, notLengthLabel);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, runtime.TSArrayType);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToInt64", _types.Object));
            il.Emit(OpCodes.Callvirt, runtime.TSArraySetLength);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(notLengthLabel);
            // Silently ignore other named writes on arrays.
            il.Emit(OpCodes.Ret);
        }

        // $TSFunction handler: create data descriptor with the value, store via PDSDefineProperty
        il.MarkLabel(tsFunctionSetLabel);
        {
            var descLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
            il.Emit(OpCodes.Newobj, runtime.CompiledPropertyDescriptorCtor);
            il.Emit(OpCodes.Stloc, descLocal);
            il.Emit(OpCodes.Ldloc, descLocal);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetSetMethod()!);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc, descLocal);
            il.Emit(OpCodes.Call, runtime.PDSDefineProperty);
            il.Emit(OpCodes.Pop); // discard bool return
            il.Emit(OpCodes.Ret);
        }

        // $Object handler - call obj.SetProperty(name, value) which handles setters
        il.MarkLabel(tsObjectLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectSetProperty);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictLabel);
        // For dictionaries, check frozen/sealed tables and silently ignore if frozen/sealed
        var sealedCheckLabel = il.DefineLabel();
        var doSetLabel = il.DefineLabel();
        var valueLocal = il.DeclareLocal(_types.Object);

        // Check if frozen: _frozenObjects.TryGetValue(obj, out _)
        il.Emit(OpCodes.Ldsfld, runtime.FrozenObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        il.Emit(OpCodes.Brtrue, nullLabel); // Frozen - silently return

        // Check if sealed and property doesn't exist
        il.Emit(OpCodes.Ldsfld, runtime.SealedObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        var extensibleCheckLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, extensibleCheckLabel); // Not sealed, check extensibility

        // Object is sealed - check if property exists
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "ContainsKey", _types.String));
        il.Emit(OpCodes.Brfalse, nullLabel); // Property doesn't exist, silently return
        il.Emit(OpCodes.Br, doSetLabel); // Property exists on sealed object, proceed to set

        // Check extensibility via $PropertyDescriptorStore.CanAddProperty - fully standalone, no reflection
        il.MarkLabel(extensibleCheckLabel);
        il.Emit(OpCodes.Ldarg_0);  // obj
        il.Emit(OpCodes.Ldarg_1);  // name
        il.Emit(OpCodes.Call, runtime.PDSCanAddProperty);
        il.Emit(OpCodes.Brfalse, nullLabel);  // Cannot add property, silently return

        // Actually set the property
        il.MarkLabel(doSetLabel);

        // Check for setter accessor via $PropertyDescriptorStore - fully standalone, no reflection
        var setterLocal = il.DeclareLocal(_types.Object);
        var noSetterLabel = il.DefineLabel();

        // Call PDSTryGetSetter(obj, name, out setter)
        il.Emit(OpCodes.Ldarg_0);  // obj
        il.Emit(OpCodes.Ldarg_1);  // name
        il.Emit(OpCodes.Ldloca, setterLocal);  // out setter
        il.Emit(OpCodes.Call, runtime.PDSTryGetSetter);
        il.Emit(OpCodes.Brfalse, noSetterLabel);

        // Setter was found - invoke it via InvokeMethodValue(obj, setter, [value])
        il.Emit(OpCodes.Ldarg_0);  // receiver (obj)
        il.Emit(OpCodes.Ldloc, setterLocal);  // function (setter)
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_2);  // value
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Call, runtime.InvokeMethodValue);
        il.Emit(OpCodes.Pop);  // Discard return value
        il.Emit(OpCodes.Ret);

        il.MarkLabel(noSetterLabel);

        // Check if property is writable via $PropertyDescriptorStore - fully standalone, no reflection
        il.Emit(OpCodes.Ldarg_0);  // obj
        il.Emit(OpCodes.Ldarg_1);  // name
        il.Emit(OpCodes.Call, runtime.PDSIsWritable);
        il.Emit(OpCodes.Brfalse, nullLabel);  // Not writable, silently return

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits SetPropertyStrict(object obj, string name, object value, bool strictMode) -> void
    /// In strict mode, throws TypeError for modifications to frozen objects or new properties on sealed objects.
    /// </summary>
    private void EmitSetPropertyStrict(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SetPropertyStrict",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.Object, _types.String, _types.Object, _types.Boolean]
        );
        runtime.SetPropertyStrict = method;

        var il = method.GetILGenerator();
        var nullLabel = il.DefineLabel();
        var dictLabel = il.DefineLabel();

        // null check
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // Check if $Object
        var sharpTSObjectLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brtrue, sharpTSObjectLabel);

        // Dictionary
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // $TSFunction — mirror the non-strict SetProperty branch (functions as objects carry
        // user-assigned properties through PDSDefineProperty).
        var tsFunctionSetStrictLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, tsFunctionSetStrictLabel);

        // $CJSModule — mirror the non-strict branch.
        var cjsModuleSetStrictLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.CjsModuleType);
        il.Emit(OpCodes.Brtrue, cjsModuleSetStrictLabel);

        // Not a dict or $Object or $TSFunction or $CJSModule - fall back to SetFieldsPropertyStrict
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3); // strictMode
        il.Emit(OpCodes.Call, runtime.SetFieldsPropertyStrict);
        il.Emit(OpCodes.Ret);

        // $CJSModule strict handler — same as non-strict for now.
        il.MarkLabel(cjsModuleSetStrictLabel);
        {
            var notExportsLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldstr, "exports");
            il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
            il.Emit(OpCodes.Brfalse, notExportsLabel);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, runtime.CjsModuleType);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, runtime.CjsModuleExportsSetter);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(notExportsLabel);
            il.Emit(OpCodes.Ret);
        }

        // $TSFunction handler: create data descriptor with the value, store via PDSDefineProperty
        il.MarkLabel(tsFunctionSetStrictLabel);
        {
            var descStrictLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
            il.Emit(OpCodes.Newobj, runtime.CompiledPropertyDescriptorCtor);
            il.Emit(OpCodes.Stloc, descStrictLocal);
            il.Emit(OpCodes.Ldloc, descStrictLocal);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetSetMethod()!);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc, descStrictLocal);
            il.Emit(OpCodes.Call, runtime.PDSDefineProperty);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        }

        // $Object - call SetPropertyStrict
        il.MarkLabel(sharpTSObjectLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3); // strictMode
        il.Emit(OpCodes.Callvirt, runtime.TSObjectSetPropertyStrict);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictLabel);
        // For dictionaries, check frozen/sealed tables
        var frozenCheckLabel = il.DefineLabel();
        var sealedCheckLabel = il.DefineLabel();
        var doSetLabel = il.DefineLabel();
        var valueLocal = il.DeclareLocal(_types.Object);

        // Check if frozen: _frozenObjects.TryGetValue(obj, out _)
        il.Emit(OpCodes.Ldsfld, runtime.FrozenObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, sealedCheckLabel); // Not frozen, check sealed

        // Object is frozen - check strict mode
        il.Emit(OpCodes.Ldarg_3); // strictMode
        il.Emit(OpCodes.Brfalse, nullLabel); // Not strict, silently return

        // Strict mode and frozen - throw TypeError
        il.Emit(OpCodes.Ldstr, "Cannot assign to read only property '");
        il.Emit(OpCodes.Ldarg_1); // name
        il.Emit(OpCodes.Ldstr, "' of object");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String, _types.String));
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        // Check if sealed and property doesn't exist
        il.MarkLabel(sealedCheckLabel);
        il.Emit(OpCodes.Ldsfld, runtime.SealedObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        var extensibleCheckLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, extensibleCheckLabel); // Not sealed, check extensibility

        // Object is sealed - check if property exists
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "ContainsKey", _types.String));
        il.Emit(OpCodes.Brtrue, doSetLabel); // Property exists, can modify

        // Property doesn't exist and object is sealed - check strict mode
        il.Emit(OpCodes.Ldarg_3); // strictMode
        il.Emit(OpCodes.Brfalse, nullLabel); // Not strict, silently return

        // Strict mode and sealed with new property - throw TypeError
        il.Emit(OpCodes.Ldstr, "Cannot add property '");
        il.Emit(OpCodes.Ldarg_1); // name
        il.Emit(OpCodes.Ldstr, "' to a sealed object");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String, _types.String));
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        // Check extensibility via $PropertyDescriptorStore.CanAddProperty - fully standalone, no reflection
        il.MarkLabel(extensibleCheckLabel);
        il.Emit(OpCodes.Ldarg_0);  // obj
        il.Emit(OpCodes.Ldarg_1);  // name
        il.Emit(OpCodes.Call, runtime.PDSCanAddProperty);
        il.Emit(OpCodes.Brtrue, doSetLabel);  // Can add property, proceed to set

        // Cannot add property (non-extensible) - check strict mode
        il.Emit(OpCodes.Ldarg_3);  // strictMode
        il.Emit(OpCodes.Brfalse, nullLabel);  // Not strict, silently return

        // Strict mode and non-extensible with new property - throw TypeError
        il.Emit(OpCodes.Ldstr, "Cannot add property '");
        il.Emit(OpCodes.Ldarg_1);  // name
        il.Emit(OpCodes.Ldstr, "' to a non-extensible object");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String, _types.String));
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        // Actually set the property
        il.MarkLabel(doSetLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits SetIndexStrict(object obj, object index, object value, bool strictMode) -> void
    /// In strict mode, throws TypeError for modifications to frozen/sealed arrays.
    /// </summary>
    private void EmitSetIndexStrict(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SetIndexStrict",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.Object, _types.Object, _types.Object, _types.Boolean]
        );
        runtime.SetIndexStrict = method;

        var il = method.GetILGenerator();
        var nullLabel = il.DefineLabel();
        var sharpTSArrayLabel = il.DefineLabel();
        var listLabel = il.DefineLabel();
        var dictLabel = il.DefineLabel();

        // null check
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // Check if $Array (for strict mode support)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSArrayType);
        il.Emit(OpCodes.Brtrue, sharpTSArrayLabel);

        // List<object?>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObjectNullable);
        il.Emit(OpCodes.Brtrue, listLabel);

        // Dictionary
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Default - just return
        il.Emit(OpCodes.Ret);

        // $Array - call SetStrict with index and strictMode via the long API.
        // Stage E.2 M6: widened from TSArraySetStrict (int) to TSArraySetStrictLong
        // so `"use strict"; arr[2147483648] = v` doesn't truncate to int.MinValue.
        // Parallel to the M3 GetIndex/SetIndex widening.
        il.MarkLabel(sharpTSArrayLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSArrayType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Convert, "ToInt64", _types.Object));
        il.Emit(OpCodes.Ldarg_2); // value
        il.Emit(OpCodes.Ldarg_3); // strictMode
        il.Emit(OpCodes.Callvirt, runtime.TSArraySetStrictLong);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(listLabel);
        // Check if frozen - in strict mode, throw TypeError
        var listSetLabel = il.DefineLabel();
        var listFrozenCheckLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldsfld, runtime.FrozenObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, listFrozenCheckLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        var listNotFrozenLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, listNotFrozenLabel);
        // Frozen - check strictMode and throw if true
        il.Emit(OpCodes.Ldarg_3); // strictMode
        var listFrozenSilentLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, listFrozenSilentLabel);
        il.Emit(OpCodes.Ldstr, "Cannot assign to read only property of frozen array");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(listFrozenSilentLabel);
        il.Emit(OpCodes.Ret); // Silently return in non-strict mode
        il.MarkLabel(listNotFrozenLabel);
        // Not frozen - set normally
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ListOfObjectNullable);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObjectNullable, "set_Item", _types.Int32, _types.Object));
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictLabel);
        // Dictionary uses string keys - convert index to string and set
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits DeleteProperty(object obj, string name) -> bool
    /// Removes a property from an object and returns true if successful.
    /// Returns false for frozen/sealed objects or if the object doesn't support deletion.
    /// </summary>
    private void EmitDeleteProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DeleteProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.String]
        );
        runtime.DeleteProperty = method;

        var il = method.GetILGenerator();
        var nullLabel = il.DefineLabel();
        var dictLabel = il.DefineLabel();
        var trueLabel = il.DefineLabel();

        // null check - return true (deleting from null is allowed in JS)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, trueLabel);

        // Proxy check: uses obj.GetType().FullName comparison (no SharpTS.dll dependency)
        var notProxyLabel = il.DefineLabel();
        EmitProxyDeleteCheck(il, () => il.Emit(OpCodes.Ldarg_0), () => il.Emit(OpCodes.Ldarg_1), notProxyLabel);

        il.MarkLabel(notProxyLabel);

        // Check if $TSObject
        var sharpTSObjectLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brtrue, sharpTSObjectLabel);

        // $Array — `delete arr[i]` turns the slot into a hole. Must come
        // BEFORE the Dictionary check (not relevant here, just ordering)
        // and BEFORE the trueLabel fallthrough so actual deletions happen.
        var tsArrayDelLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSArrayType);
        il.Emit(OpCodes.Brtrue, tsArrayDelLabel);

        // Dictionary
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Other types - cannot delete properties, return true (JS behavior for non-configurable)
        il.Emit(OpCodes.Br, trueLabel);

        // $Array delete handler — if the key is a numeric index call
        // DeleteAt; otherwise return true (JS non-configurable behavior).
        il.MarkLabel(tsArrayDelLabel);
        {
            var tsArrDelIndexLocal = il.DeclareLocal(_types.Int64);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloca, tsArrDelIndexLocal);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.Int64, "TryParse", _types.String, _types.Int64.MakeByRefType()));
            var tsArrDelNonNumericLabel = il.DefineLabel();
            il.Emit(OpCodes.Brfalse, tsArrDelNonNumericLabel);

            // arr.DeleteAt(idx); return true;
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, runtime.TSArrayType);
            il.Emit(OpCodes.Ldloc, tsArrDelIndexLocal);
            il.Emit(OpCodes.Callvirt, runtime.TSArrayDeleteAt);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(tsArrDelNonNumericLabel);
            // Non-numeric key — JS allows it (returns true) but no storage
            // on arrays. Matches pre-M3 fallthrough behavior.
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
        }

        // $TSObject - call DeleteProperty instance method
        il.MarkLabel(sharpTSObjectLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectDeleteProperty);
        il.Emit(OpCodes.Ret);

        // Dictionary - use Remove
        il.MarkLabel(dictLabel);
        var valueLocal = il.DeclareLocal(_types.Object);

        // Check if frozen
        il.Emit(OpCodes.Ldsfld, runtime.FrozenObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        var notFrozenLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notFrozenLabel);

        // Frozen - return false
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        // Check if sealed
        il.MarkLabel(notFrozenLabel);
        il.Emit(OpCodes.Ldsfld, runtime.SealedObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        var notSealedLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notSealedLabel);

        // Sealed - return false
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        // Not frozen/sealed - do the removal
        il.MarkLabel(notSealedLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "Remove", _types.String));
        il.Emit(OpCodes.Ret);

        // Return true (default for null and other types)
        il.MarkLabel(trueLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits DeletePropertyStrict(object obj, string name, bool strictMode) -> bool
    /// In strict mode, throws TypeError for frozen/sealed objects.
    /// In sloppy mode, returns false for frozen/sealed objects.
    /// </summary>
    private void EmitDeletePropertyStrict(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DeletePropertyStrict",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.String, _types.Boolean]
        );
        runtime.DeletePropertyStrict = method;

        var il = method.GetILGenerator();
        var nullLabel = il.DefineLabel();
        var dictLabel = il.DefineLabel();
        var trueLabel = il.DefineLabel();

        // null check - return true (deleting from null is allowed in JS)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, trueLabel);

        // Proxy check: uses obj.GetType().FullName comparison (no SharpTS.dll dependency)
        var notProxyLabel = il.DefineLabel();
        EmitProxyDeleteCheck(il, () => il.Emit(OpCodes.Ldarg_0), () => il.Emit(OpCodes.Ldarg_1), notProxyLabel);

        il.MarkLabel(notProxyLabel);

        // Check if $TSObject
        var sharpTSObjectLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brtrue, sharpTSObjectLabel);

        // Dictionary
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Other types - cannot delete properties, return true (JS behavior for non-configurable)
        il.Emit(OpCodes.Br, trueLabel);

        // $TSObject - call DeletePropertyStrict instance method
        il.MarkLabel(sharpTSObjectLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2); // strictMode
        il.Emit(OpCodes.Callvirt, runtime.TSObjectDeletePropertyStrict);
        il.Emit(OpCodes.Ret);

        // Dictionary - check frozen/sealed and handle strict mode
        il.MarkLabel(dictLabel);
        var valueLocal = il.DeclareLocal(_types.Object);

        // Check if frozen
        il.Emit(OpCodes.Ldsfld, runtime.FrozenObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        var notFrozenLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notFrozenLabel);

        // Frozen - check if strict mode
        var frozenSloppyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brfalse, frozenSloppyLabel);

        // Frozen + strict - throw TypeError
        il.Emit(OpCodes.Ldstr, "Cannot delete property '");
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "' of a frozen object");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String, _types.String));
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        // Frozen + sloppy - return false
        il.MarkLabel(frozenSloppyLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        // Check if sealed
        il.MarkLabel(notFrozenLabel);
        il.Emit(OpCodes.Ldsfld, runtime.SealedObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ConditionalWeakTable, "TryGetValue", _types.Object, _types.Object.MakeByRefType()));
        var notSealedLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notSealedLabel);

        // Sealed - check if strict mode
        var sealedSloppyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brfalse, sealedSloppyLabel);

        // Sealed + strict - throw TypeError
        il.Emit(OpCodes.Ldstr, "Cannot delete property '");
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "' of a sealed object");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String, _types.String));
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        // Sealed + sloppy - return false
        il.MarkLabel(sealedSloppyLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        // Not frozen/sealed - do the removal
        il.MarkLabel(notSealedLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "Remove", _types.String));
        il.Emit(OpCodes.Ret);

        // Return true (default for null and other types)
        il.MarkLabel(trueLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Phase 1: Define $MethodCallable type (wraps BuiltInMethod or other callable objects
    /// returned by GetMember so they can be dispatched through InvokeMethodValue/InvokeValue).
    /// </summary>
    internal void EmitMethodCallableTypeDefinition(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$MethodCallable",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );
        runtime.MethodCallableType = typeBuilder;

        // Field: object _callable
        var callableField = typeBuilder.DefineField("_callable", _types.Object, FieldAttributes.Private);
        runtime.MethodCallableField = callableField;

        // Constructor: $MethodCallable(object callable)
        var ctorBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.Object]
        );
        runtime.MethodCallableCtor = ctorBuilder;

        var ctorIL = ctorBuilder.GetILGenerator();
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.Object));
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Stfld, callableField);
        ctorIL.Emit(OpCodes.Ret);

        // Define Invoke method signature (body emitted in Phase 2)
        var invokeBuilder = typeBuilder.DefineMethod(
            "Invoke",
            MethodAttributes.Public,
            _types.Object,
            [_types.ObjectArray]
        );
        runtime.MethodCallableInvoke = invokeBuilder;
    }

    /// <summary>
    /// Phase 2: Emit Invoke method body for $MethodCallable and create the type.
    /// Uses reflection to call "Invoke" (for TSFunction) or "Call" (for BuiltInMethod) on the wrapped object.
    /// </summary>
    internal void EmitMethodCallableFinalize(EmittedRuntime runtime)
    {
        var callableField = runtime.MethodCallableField;
        var invokeBuilder = runtime.MethodCallableInvoke;

        var il = invokeBuilder.GetILGenerator();

        // Locals
        var callableLocal = il.DeclareLocal(_types.Object);         // 0: _callable
        var typeLocal = il.DeclareLocal(typeof(Type));               // 1: callable.GetType()
        var methodLocal = il.DeclareLocal(_types.MethodInfo);        // 2: MethodInfo

        // Load _callable
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, callableField);
        il.Emit(OpCodes.Stloc, callableLocal);

        // Get type
        il.Emit(OpCodes.Ldloc, callableLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Try "Invoke" method first (TSFunction, Func<>, etc.)
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "Invoke");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        il.Emit(OpCodes.Stloc, methodLocal);

        var noInvokeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, methodLocal);
        il.Emit(OpCodes.Brfalse, noInvokeLabel);

        // Found "Invoke" - call: methodInfo.Invoke(_callable, new object[] { args })
        il.Emit(OpCodes.Ldloc, methodLocal);
        il.Emit(OpCodes.Ldloc, callableLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1); // args (object[])
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        // Try "Call" method (BuiltInMethod.Call(Interpreter, List<object?>))
        il.MarkLabel(noInvokeLabel);
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "Call");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        il.Emit(OpCodes.Stloc, methodLocal);

        var noCallLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, methodLocal);
        il.Emit(OpCodes.Brfalse, noCallLabel);

        // Found "Call" - call: methodInfo.Invoke(_callable, new object[] { null, new List<object?>(args) })
        // null interpreter is an established pattern (SharpTSEventEmitter.InvokeListenerDirect)
        il.Emit(OpCodes.Ldloc, methodLocal);
        il.Emit(OpCodes.Ldloc, callableLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        // args[0] = null (interpreter)
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stelem_Ref);
        // args[1] = new List<object?>(args)
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1); // args (object[])
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor([typeof(IEnumerable<object>)])!);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        // No callable method found - return null
        il.MarkLabel(noCallLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        runtime.MethodCallableType.CreateType();
    }
}

