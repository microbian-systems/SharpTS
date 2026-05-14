using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Populates <see cref="EmittedRuntime.ErrorPrototypeField"/> with
    /// <c>$TSFunction</c> wrappers for the spec-compliant
    /// <c>ErrorToStringSpec</c> + <c>constructor</c> slot. Reached by
    /// <c>Error.prototype.toString.call(non-error)</c> via GetProperty's
    /// Type-receiver branch — required so the brand-checking helper runs
    /// instead of generic class reflection on $Error.
    /// </summary>
    private void DefineErrorPrototypePopulateShell(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.ErrorPrototypePopulateMethod = typeBuilder.DefineMethod(
            "_ErrorPrototypePopulate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            Type.EmptyTypes);
    }

    private void EmitErrorPrototypePopulate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Emit the spec-compliant toString helper before the populate body
        // that wires it up.
        var errorToStringSpec = EmitErrorToStringSpecHelper(typeBuilder, runtime);
        runtime.ErrorToStringSpec = errorToStringSpec;

        var method = runtime.ErrorPrototypePopulateMethod;
        var il = method.GetILGenerator();
        var setItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item",
            _types.String, _types.Object);

        // Idempotent guard.
        var doFillLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, runtime.ErrorPrototypeField);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, doFillLabel);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(doFillLabel);

        // ECMA-262 20.5.3 Error.prototype.constructor === Error. Compiled
        // bare `Error` resolves to typeof($Error).
        il.Emit(OpCodes.Ldsfld, runtime.ErrorPrototypeField);
        il.Emit(OpCodes.Ldstr, "constructor");
        il.Emit(OpCodes.Ldtoken, runtime.TSErrorType);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Callvirt, setItem);

        // ECMA-262 20.5.3 Error.prototype.name === "Error" and message === "".
        il.Emit(OpCodes.Ldsfld, runtime.ErrorPrototypeField);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Ldstr, "Error");
        il.Emit(OpCodes.Callvirt, setItem);
        il.Emit(OpCodes.Ldsfld, runtime.ErrorPrototypeField);
        il.Emit(OpCodes.Ldstr, "message");
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Callvirt, setItem);

        // Wire the toString $TSFunction wrapper. First parameter named "__this"
        // so $TSFunction.InvokeWithThis prepends the call-site receiver when
        // borrowed (`obj.toString = Error.prototype.toString; obj.toString()`).
        try { errorToStringSpec.DefineParameter(1, ParameterAttributes.None, "__this"); }
        catch { /* already named — ignore */ }
        var errToStringWrapperLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldtoken, errorToStringSpec);
        il.Emit(OpCodes.Ldtoken, errorToStringSpec.DeclaringType!);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle",
            _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Ldstr, "toString");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtorWithCache);
        il.Emit(OpCodes.Stloc, errToStringWrapperLocal);
        il.Emit(OpCodes.Ldsfld, runtime.ErrorPrototypeField);
        il.Emit(OpCodes.Ldstr, "toString");
        il.Emit(OpCodes.Ldloc, errToStringWrapperLocal);
        il.Emit(OpCodes.Callvirt, setItem);

        // Install non-enumerable PDS descriptors for constructor/name/message/
        // toString per ECMA-262 §20.5.3 + §17 (built-in data properties are
        // W:T,E:F,C:T).
        var errDescLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        void InstallNonEnumerableErr(string jsName, System.Action emitValue)
        {
            il.Emit(OpCodes.Newobj, runtime.CompiledPropertyDescriptorCtor);
            il.Emit(OpCodes.Stloc, errDescLocal);
            il.Emit(OpCodes.Ldloc, errDescLocal);
            emitValue();
            il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetSetMethod()!);
            il.Emit(OpCodes.Ldloc, errDescLocal);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorEnumerable.GetSetMethod()!);
            il.Emit(OpCodes.Ldsfld, runtime.ErrorPrototypeField);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldloc, errDescLocal);
            il.Emit(OpCodes.Call, runtime.PDSDefineProperty);
            il.Emit(OpCodes.Pop);
        }
        InstallNonEnumerableErr("constructor", () =>
        {
            il.Emit(OpCodes.Ldtoken, runtime.TSErrorType);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        });
        InstallNonEnumerableErr("name", () => il.Emit(OpCodes.Ldstr, "Error"));
        InstallNonEnumerableErr("message", () => il.Emit(OpCodes.Ldstr, ""));
        InstallNonEnumerableErr("toString", () => il.Emit(OpCodes.Ldloc, errToStringWrapperLocal));

        // Per ECMA-262 §20.5.3 Error.prototype's [[Prototype]] is %Object.prototype%.
        il.Emit(OpCodes.Ldsfld, runtime.ErrorPrototypeField);
        il.Emit(OpCodes.Ldsfld, runtime.ObjectPrototypeField);
        il.Emit(OpCodes.Call, runtime.PDSSetPrototype);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ECMA-262 20.5.3.4 Error.prototype.toString:
    /// <list type="number">
    /// <item>If <c>this</c> is not an Object, throw TypeError.</item>
    /// <item>Read <c>name</c> (default "Error") and <c>message</c> (default "")
    /// via Get; coerce both to strings via ToString.</item>
    /// <item>Return name + ": " + message, or just one if either is empty.</item>
    /// </list>
    /// "Object" here means anything that isn't <c>null</c>, <c>$Undefined</c>,
    /// <c>bool</c>, <c>double</c>, <c>string</c> — matches the $Runtime.TypeOf
    /// classification.
    /// </summary>
    private MethodBuilder EmitErrorToStringSpecHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ErrorToStringSpec",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]);
        var il = method.GetILGenerator();

        var throwLabel = il.DefineLabel();
        var passLabel = il.DefineLabel();

        // Step 2: brand check. Anything primitive throws TypeError.
        // null
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, throwLabel);
        // $Undefined
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, throwLabel);
        // bool
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brtrue, throwLabel);
        // double
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, throwLabel);
        // string
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, throwLabel);
        // Symbol (TSSymbol). ECMA-262 §20.5.3.4 step 2 brand check rejects all
        // primitives including Symbol; the dispatch above only catches the
        // common five. invalid-receiver.js iterates Symbol() alongside the
        // others.
        if (runtime.TSSymbolType != null)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, runtime.TSSymbolType);
            il.Emit(OpCodes.Brtrue, throwLabel);
        }

        il.Emit(OpCodes.Br, passLabel);

        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "Error.prototype.toString called on non-object");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(passLabel);

        // Step 3-4: name = Get(O, "name"); if undefined, name = "Error".
        var nameLocal = il.DeclareLocal(_types.Object);
        var nameStrLocal = il.DeclareLocal(_types.String);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Stloc, nameLocal);

        var nameDefinedLabel = il.DefineLabel();
        var nameDoneLabel = il.DefineLabel();
        // undefined?
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Brfalse, nameDefinedLabel); // null acts as undefined here
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brfalse, nameDefinedLabel);
        // name is undefined → "Error"
        il.Emit(OpCodes.Ldstr, "Error");
        il.Emit(OpCodes.Stloc, nameStrLocal);
        il.Emit(OpCodes.Br, nameDoneLabel);

        il.MarkLabel(nameDefinedLabel);
        // ToString(name) via $Runtime.ToJsString.
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Call, runtime.ToJsString);
        il.Emit(OpCodes.Stloc, nameStrLocal);
        il.MarkLabel(nameDoneLabel);

        // Step 5-6: message = Get(O, "message"); if undefined, message = "".
        var msgLocal = il.DeclareLocal(_types.Object);
        var msgStrLocal = il.DeclareLocal(_types.String);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "message");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Stloc, msgLocal);

        var msgDefinedLabel = il.DefineLabel();
        var msgDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, msgLocal);
        il.Emit(OpCodes.Brfalse, msgDefinedLabel);
        il.Emit(OpCodes.Ldloc, msgLocal);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brfalse, msgDefinedLabel);
        // message is undefined → ""
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, msgStrLocal);
        il.Emit(OpCodes.Br, msgDoneLabel);

        il.MarkLabel(msgDefinedLabel);
        il.Emit(OpCodes.Ldloc, msgLocal);
        il.Emit(OpCodes.Call, runtime.ToJsString);
        il.Emit(OpCodes.Stloc, msgStrLocal);
        il.MarkLabel(msgDoneLabel);

        // Step 7-9: combine.
        // if (name == "") return msg
        var nameNotEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, nameStrLocal);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, nameNotEmptyLabel);
        il.Emit(OpCodes.Ldloc, msgStrLocal);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(nameNotEmptyLabel);

        // if (msg == "") return name
        var msgNotEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, msgStrLocal);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, msgNotEmptyLabel);
        il.Emit(OpCodes.Ldloc, nameStrLocal);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(msgNotEmptyLabel);

        // return name + ": " + msg
        il.Emit(OpCodes.Ldloc, nameStrLocal);
        il.Emit(OpCodes.Ldstr, ": ");
        il.Emit(OpCodes.Ldloc, msgStrLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Ret);

        return method;
    }
}
