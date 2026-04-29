using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Defines <c>$Arguments : List&lt;object&gt;</c> — a marker subclass used to
    /// represent the JS <c>arguments</c> object in compiled mode. Inherits all
    /// List&lt;object&gt; behavior so existing dispatchers (Isinst List, Castclass
    /// List) continue working transparently. Distinguishable at runtime via
    /// <c>Isinst $Arguments</c> for two ECMA-262 spec checks:
    ///   - <c>Object.prototype.toString.call(arguments) === "[object Arguments]"</c>
    ///   - <c>Array.isArray(arguments) === false</c>
    /// </summary>
    /// <remarks>
    /// Without this marker, <c>arguments</c> bound as a plain List&lt;object&gt;
    /// is indistinguishable from a regular array, and the brand-tagger / IsArray
    /// fall through to the [object Array] / true paths, breaking ~30 test262
    /// tests that probe these spec details on the arguments object.
    /// </remarks>
    internal void EmitArgumentsTypeDefinition(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$Arguments",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.ListOfObject
        );
        runtime.ArgumentsType = typeBuilder;

        // Ctor: public $Arguments() : base() { }
        var defaultCtor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes
        );
        var defaultIL = defaultCtor.GetILGenerator();
        defaultIL.Emit(OpCodes.Ldarg_0);
        defaultIL.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.ListOfObject));
        defaultIL.Emit(OpCodes.Ret);
        runtime.ArgumentsDefaultCtor = defaultCtor;

        // Ctor: public $Arguments(int capacity) : base(capacity) { }
        var capacityCtor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.Int32]
        );
        var capIL = capacityCtor.GetILGenerator();
        capIL.Emit(OpCodes.Ldarg_0);
        capIL.Emit(OpCodes.Ldarg_1);
        capIL.Emit(OpCodes.Call, _types.GetConstructor(_types.ListOfObject, _types.Int32));
        capIL.Emit(OpCodes.Ret);
        runtime.ArgumentsCapacityCtor = capacityCtor;

        // Ctor: public $Arguments(IEnumerable<object> source) : base(source) { }
        var enumCtor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.IEnumerableOfObject]
        );
        var enumIL = enumCtor.GetILGenerator();
        enumIL.Emit(OpCodes.Ldarg_0);
        enumIL.Emit(OpCodes.Ldarg_1);
        enumIL.Emit(OpCodes.Call, _types.GetConstructor(_types.ListOfObject, _types.IEnumerableOfObject));
        enumIL.Emit(OpCodes.Ret);
        runtime.ArgumentsEnumerableCtor = enumCtor;

        typeBuilder.CreateType();
    }
}
