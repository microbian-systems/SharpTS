using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Phase-1 forward declaration of the <c>$Runtime</c> class plus a small
    /// set of helper-method signatures. The full $Runtime class is emitted
    /// much later (<see cref="EmitRuntimeClass"/>), but other helper types
    /// — most importantly <c>$RegExp</c> whose Symbol.* protocol methods
    /// want to call <c>Stringify</c> and <c>CreateException</c> — emit
    /// before then. By pre-creating the TypeBuilder and reserving the two
    /// MethodBuilders here, those callers can emit a <c>Call</c> /
    /// <c>Newobj</c> against them right away; CLR finalises everything when
    /// each TypeBuilder's <c>CreateType</c> runs at the end of emission.
    /// </summary>
    private void DefineRuntimeClassPhase1(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        // Define class: public static class $Runtime
        var typeBuilder = moduleBuilder.DefineType(
            "$Runtime",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );
        runtime.RuntimeType = typeBuilder;
        _runtimeTypeBuilder = typeBuilder;

        // Reserve Stringify(object) → string. EmitStringify fills the body
        // later; it must skip its own DefineMethod call when this signature
        // is already present.
        runtime.Stringify = typeBuilder.DefineMethod(
            "Stringify",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]);

        // Reserve CreateException(object) → Exception. EmitCreateException
        // similarly fills the body later.
        runtime.CreateException = typeBuilder.DefineMethod(
            "CreateException",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Exception,
            [_types.Object]);

        // Reserve GetProperty(object, string) → object — generic property
        // reader used by $RegExp's Symbol.* protocol slow path to read
        // `exec`/`flags`/`lastIndex` etc. via the spec-aligned chain.
        runtime.GetProperty = typeBuilder.DefineMethod(
            "GetProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.String]);

        // Reserve SetProperty(object, string, object) → void.
        runtime.SetProperty = typeBuilder.DefineMethod(
            "SetProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.Object, _types.String, _types.Object]);

        // Reserve ToNumber(object) → double. Used by $RegExp's Symbol.split
        // to coerce `limit` per ECMA-262 §22.2.5.13 step 7 (and to throw
        // TypeError on Symbol limits). EmitToNumber later fills the body.
        runtime.ToNumber = typeBuilder.DefineMethod(
            "ToNumber",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]);

        // Reserve ToJsString(object) → string. Stringify (which $RegExp's
        // Symbol.* helpers currently call) doesn't run @@toPrimitive on
        // objects; ToJsString does (ECMA-262 §7.1.1). Forward-declared so
        // Symbol.{replace,split,matchAll}'s flags coercion can route through
        // the spec-aligned ToString chain.
        runtime.ToJsString = typeBuilder.DefineMethod(
            "ToJsString",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]);

        // Reserve the RegExp.prototype dictionary field. $RegExp emits
        // before EmitRuntimeClass and its accessor helpers
        // (TSRegExpProtoGet*) need to compare `__this` against this field
        // to detect the "called on RegExp.prototype itself" spec case.
        // Cctor in EmitRuntimeClass initializes it to a fresh Dictionary;
        // RegExpPrototypePopulate fills the per-process singleton lazily.
        runtime.RegExpPrototypeField = typeBuilder.DefineField(
            "_regexpPrototype",
            _types.DictionaryStringObject,
            FieldAttributes.Public | FieldAttributes.Static);
    }
}
