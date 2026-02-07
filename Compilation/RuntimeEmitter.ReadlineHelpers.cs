using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    // Stored field and type references for $ReadlineInterface (used in two-phase emission)
    private FieldBuilder _readlineInterfaceClosedField = null!;
    private TypeBuilder _readlineInterfaceTypeBuilder = null!;

    /// <summary>
    /// Phase 1: Defines the $ReadlineInterface type, fields, constructor, and simple methods.
    /// Must be called BEFORE EmitRuntimeClass so ReadlineCreateInterface can use the constructor.
    /// NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSReadlineInterface
    /// </summary>
    private void EmitReadlineInterfaceTypeDefinition(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        // Define class: public sealed class $ReadlineInterface
        var typeBuilder = moduleBuilder.DefineType(
            "$ReadlineInterface",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );
        _readlineInterfaceTypeBuilder = typeBuilder;

        // Field: private bool _closed
        _readlineInterfaceClosedField = typeBuilder.DefineField("_closed", _types.Boolean, FieldAttributes.Private);

        // Constructor: public $ReadlineInterface()
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes
        );
        runtime.ReadlineInterfaceCtor = ctor;

        var ctorIL = ctor.GetILGenerator();
        // Call base constructor
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.Object));
        // _closed = false
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldc_I4_0);
        ctorIL.Emit(OpCodes.Stfld, _readlineInterfaceClosedField);
        ctorIL.Emit(OpCodes.Ret);

        // Emit Close and Prompt methods (don't depend on InvokeValue)
        EmitReadlineInterfaceClose(typeBuilder, runtime);
        EmitReadlineInterfacePrompt(typeBuilder, runtime);

        // NOTE: Question method and CreateType() are deferred to Phase 2
    }

    /// <summary>
    /// Phase 2: Adds the Question method (requires InvokeValue) and finalizes the type.
    /// Must be called AFTER EmitRuntimeClass (Question uses InvokeValue).
    /// </summary>
    private void EmitReadlineInterfaceFinalize(EmittedRuntime runtime)
    {
        // Emit Question method (uses runtime.InvokeValue)
        EmitReadlineInterfaceQuestion(_readlineInterfaceTypeBuilder, runtime);

        runtime.ReadlineInterfaceType = _readlineInterfaceTypeBuilder.CreateType()!;
    }

    /// <summary>
    /// Emits: public object? Question(object query, object callback)
    /// Writes query to console, reads line, invokes callback with answer.
    /// </summary>
    private void EmitReadlineInterfaceQuestion(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Question",
            MethodAttributes.Public,
            _types.Object,
            [_types.Object, _types.Object]
        );

        var il = method.GetILGenerator();
        var returnNullLabel = il.DefineLabel();
        var callbackLabel = il.DefineLabel();

        // if (_closed) return null;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _readlineInterfaceClosedField);
        il.Emit(OpCodes.Brtrue, returnNullLabel);

        // var queryStr = query?.ToString() ?? ""
        var queryStrLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Dup);
        var queryNullLabel = il.DefineLabel();
        var queryDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, queryNullLabel);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Br, queryDoneLabel);
        il.MarkLabel(queryNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(queryDoneLabel);
        il.Emit(OpCodes.Stloc, queryStrLocal);

        // Console.Write(queryStr)
        il.Emit(OpCodes.Ldloc, queryStrLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Console, "Write", _types.String));

        // var answer = Console.ReadLine() ?? ""
        var answerLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Call, _types.GetMethodNoParams(_types.Console, "ReadLine"));
        var answerNotNull = il.DefineLabel();
        var answerDone = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, answerNotNull);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Br, answerDone);
        il.MarkLabel(answerNotNull);
        il.MarkLabel(answerDone);
        il.Emit(OpCodes.Stloc, answerLocal);

        // if (callback == null) return null;
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brfalse, returnNullLabel);

        // Invoke the callback: InvokeValue(callback, new object[] { answer })
        il.Emit(OpCodes.Ldarg_2);  // callback
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, answerLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Call, runtime.InvokeValue);
        il.Emit(OpCodes.Pop);  // Discard return value

        il.MarkLabel(returnNullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object? Close()
    /// Sets _closed = true and returns null.
    /// </summary>
    private void EmitReadlineInterfaceClose(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Close",
            MethodAttributes.Public,
            _types.Object,
            Type.EmptyTypes
        );

        var il = method.GetILGenerator();

        // _closed = true
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, _readlineInterfaceClosedField);

        // return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object? Prompt()
    /// Writes "> " to console if not closed, returns null.
    /// </summary>
    private void EmitReadlineInterfacePrompt(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Prompt",
            MethodAttributes.Public,
            _types.Object,
            Type.EmptyTypes
        );

        var il = method.GetILGenerator();
        var returnLabel = il.DefineLabel();

        // if (_closed) goto return
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _readlineInterfaceClosedField);
        il.Emit(OpCodes.Brtrue, returnLabel);

        // Console.Write("> ")
        il.Emit(OpCodes.Ldstr, "> ");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Console, "Write", _types.String));

        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits readline module helper methods.
    /// </summary>
    private void EmitReadlineMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitReadlineQuestionSync(typeBuilder, runtime);
        EmitReadlineCreateInterface(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits: public static string ReadlineQuestionSync(string query)
    /// </summary>
    private void EmitReadlineQuestionSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ReadlineQuestionSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String]);
        runtime.ReadlineQuestionSync = method;

        var il = method.GetILGenerator();

        // Console.Write(query)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Console, "Write", _types.String));

        // return Console.ReadLine() ?? ""
        il.Emit(OpCodes.Call, _types.GetMethodNoParams(_types.Console, "ReadLine"));

        var notNull = il.DefineLabel();
        var end = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, notNull);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Br, end);
        il.MarkLabel(notNull);
        il.MarkLabel(end);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object ReadlineCreateInterface(object options)
    /// Creates a new $ReadlineInterface instance (pure IL, no SharpTS.dll dependency).
    /// </summary>
    private void EmitReadlineCreateInterface(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ReadlineCreateInterface",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]);
        runtime.ReadlineCreateInterface = method;

        var il = method.GetILGenerator();

        // return new $ReadlineInterface();
        // Note: options parameter is ignored for now (matches SharpTSReadlineInterface behavior)
        il.Emit(OpCodes.Newobj, runtime.ReadlineInterfaceCtor);
        il.Emit(OpCodes.Ret);
    }
}
