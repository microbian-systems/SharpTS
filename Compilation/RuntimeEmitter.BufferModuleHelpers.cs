using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    private MethodBuilder? _bufferCoerceString;
    private MethodBuilder? _bufferBytesOf;

    /// <summary>
    /// Emits the standalone <c>buffer</c> module helper functions (#1160): atob/btoa,
    /// isUtf8/isAscii, transcode, SlowBuffer, and the constants object. All pure-BCL —
    /// atob/btoa/transcode compose the existing $Buffer FromString/ToEncodedString
    /// helpers so they stay byte-identical to <c>BufferModuleHelpers</c> (interpreter).
    /// </summary>
    private void EmitBufferModuleMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitBufferCoerceString(typeBuilder);
        EmitBufferBytesOf(typeBuilder, runtime);
        EmitBufferAtobBtoa(typeBuilder, runtime);
        EmitBufferIsUtf8Ascii(typeBuilder, runtime);
        EmitBufferTranscode(typeBuilder, runtime);
        EmitBufferSlowBuffer(typeBuilder, runtime);
        EmitBufferModuleConstants(typeBuilder, runtime);
    }

    /// <summary>Emits <c>string BufferCoerceString(object)</c> — null → "", string → itself, else ToString().</summary>
    private void EmitBufferCoerceString(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod(
            "BufferCoerceString",
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            _types.String, [_types.Object]);
        _bufferCoerceString = method;

        var il = method.GetILGenerator();
        var retEmpty = il.DefineLabel();
        var retVal = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, retEmpty);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, retVal);   // it's a string — leave it on the stack
        il.Emit(OpCodes.Pop);              // discard the null from isinst
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.MarkLabel(retVal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(retEmpty);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Ret);
    }

    /// <summary>Emits <c>byte[] BufferBytesOf(object)</c> — $Buffer → GetData, string → UTF8 bytes.</summary>
    private void EmitBufferBytesOf(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "BufferBytesOf",
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            _types.MakeArrayType(_types.Byte), [_types.Object]);
        _bufferBytesOf = method;

        var il = method.GetILGenerator();
        var isStr = il.DefineLabel();
        var throwLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, throwLabel);
        // $Buffer?
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSBufferType);
        il.Emit(OpCodes.Brfalse, isStr);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSBufferType);
        il.Emit(OpCodes.Callvirt, runtime.TSBufferGetData);
        il.Emit(OpCodes.Ret);
        // string?
        il.MarkLabel(isStr);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, throwLabel);
        il.Emit(OpCodes.Call, _types.Encoding.GetProperty("UTF8")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, _types.Encoding.GetMethod("GetBytes", [_types.String])!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "buffer module helper requires a Buffer or string argument");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);
    }

    private void EmitBufferAtobBtoa(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // atob(data) = Buffer.from(data, 'base64').toString('latin1')
        var atob = typeBuilder.DefineMethod(
            "BufferAtob",
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            _types.String, [_types.Object]);
        runtime.BufferAtob = atob;
        var ail = atob.GetILGenerator();
        ail.Emit(OpCodes.Ldarg_0);
        ail.Emit(OpCodes.Call, _bufferCoerceString!);
        ail.Emit(OpCodes.Ldstr, "base64");
        ail.Emit(OpCodes.Call, runtime.TSBufferFromString);
        ail.Emit(OpCodes.Ldstr, "latin1");
        ail.Emit(OpCodes.Callvirt, runtime.TSBufferToString);
        ail.Emit(OpCodes.Ret);

        // btoa(data) = Buffer.from(data, 'latin1').toString('base64')
        var btoa = typeBuilder.DefineMethod(
            "BufferBtoa",
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            _types.String, [_types.Object]);
        runtime.BufferBtoa = btoa;
        var bil = btoa.GetILGenerator();
        bil.Emit(OpCodes.Ldarg_0);
        bil.Emit(OpCodes.Call, _bufferCoerceString!);
        bil.Emit(OpCodes.Ldstr, "latin1");
        bil.Emit(OpCodes.Call, runtime.TSBufferFromString);
        bil.Emit(OpCodes.Ldstr, "base64");
        bil.Emit(OpCodes.Callvirt, runtime.TSBufferToString);
        bil.Emit(OpCodes.Ret);
    }

    private void EmitBufferIsUtf8Ascii(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var byteArr = _types.MakeArrayType(_types.Byte);
        var roSpanByte = typeof(ReadOnlySpan<byte>);
        var roSpanOp = roSpanByte.GetMethod("op_Implicit", [byteArr])!;
        var utf8IsValid = typeof(System.Text.Unicode.Utf8).GetMethod("IsValid", [roSpanByte])!;
        var asciiIsValid = typeof(System.Text.Ascii).GetMethod("IsValid", [roSpanByte])!;

        void Emit(string name, System.Reflection.MethodInfo isValid, Action<MethodBuilder> store)
        {
            var method = typeBuilder.DefineMethod(
                name,
                System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
                _types.Object, [_types.Object]);
            store(method);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, _bufferBytesOf!);
            il.Emit(OpCodes.Call, roSpanOp);
            il.Emit(OpCodes.Call, isValid);
            il.Emit(OpCodes.Box, _types.Boolean);
            il.Emit(OpCodes.Ret);
        }

        Emit("BufferIsUtf8", utf8IsValid, m => runtime.BufferIsUtf8 = m);
        Emit("BufferIsAscii", asciiIsValid, m => runtime.BufferIsAscii = m);
    }

    private void EmitBufferTranscode(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // transcode(source, from, to) = Buffer.from(decode(sourceBytes, from), to)
        var method = typeBuilder.DefineMethod(
            "BufferTranscode",
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            _types.Object, [_types.Object, _types.Object, _types.Object]);
        runtime.BufferTranscode = method;

        var il = method.GetILGenerator();
        // str = new $Buffer(BufferBytesOf(source)).ToEncodedString(coerce(from))
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _bufferBytesOf!);
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _bufferCoerceString!);
        il.Emit(OpCodes.Callvirt, runtime.TSBufferToString);
        // Buffer.from(str, coerce(to))
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, _bufferCoerceString!);
        il.Emit(OpCodes.Call, runtime.TSBufferFromString);
        il.Emit(OpCodes.Ret);
    }

    private void EmitBufferSlowBuffer(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "BufferSlowBuffer",
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            _types.Object, [_types.Object]);
        runtime.BufferSlowBuffer = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Call, runtime.TSBufferAllocUnsafe);
        il.Emit(OpCodes.Ret);
    }

    private void EmitBufferModuleConstants(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "BufferModuleConstants",
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            _types.Object, Type.EmptyTypes);
        runtime.BufferModuleConstants = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        var objLocal = il.DeclareLocal(runtime.TSObjectType);
        il.Emit(OpCodes.Stloc, objLocal);

        void AddConst(string name, double value)
        {
            il.Emit(OpCodes.Ldloc, objLocal);
            il.Emit(OpCodes.Ldstr, name);
            il.Emit(OpCodes.Ldc_R8, value);
            il.Emit(OpCodes.Box, _types.Double);
            il.Emit(OpCodes.Callvirt, runtime.TSObjectSetProperty);
        }

        AddConst("MAX_LENGTH", 4294967296.0);
        AddConst("MAX_STRING_LENGTH", 536870888.0);

        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Ret);
    }
}
