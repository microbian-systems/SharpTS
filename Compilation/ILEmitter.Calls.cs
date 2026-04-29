using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

/// <summary>
/// Main call dispatch and function call emission methods for the IL emitter.
/// </summary>
public partial class ILEmitter
{
    protected override void EmitCall(Expr.Call c)
    {
        // CommonJS require() lowering is handled by ExpressionEmitterBase.EmitCall
        // (called via base.EmitCall below), so it works in async/generator emitters too.

        // ECMA-262 Array.prototype.*.call(receiver, ...) — rewrite to
        // ArrayX(Materialize(receiver), ...). Intercepted at the syntactic level
        // because compiled mode does not emit a JS-shaped Array.prototype object;
        // real dispatch would need a full $ArrayPrototype surface. Handles the
        // dominant test262 pattern (direct syntactic usage). Aliased access
        // (`var m = Array.prototype.every; m.call(arr, cb)`) is NOT covered.
        // ECMA-262 Array.prototype.*.call(receiver, ...) — rewrite to
        // ArrayX(Materialize(receiver), ...args). Intercepted at the syntactic
        // level because compiled mode does not emit a JS-shaped Array.prototype
        // object. With Stages 0b/0c landed (function-prototype, instanceof, and
        // new-FuncDecl all working correctly), test262 harness asserts fire
        // properly so any false positives surfaced by this pattern matcher
        // reclassify as real Fails — making Test262 numbers meaningful.
        if (TryEmitArrayPrototypeCall(c)) return;

        // ECMA-262 Object.prototype.toString.call(x) — return the proper
        // "[object X]" tag for built-in types. Intercepted at the syntactic
        // level because compiled mode emits Object.prototype as null (no
        // user-callable Object.prototype.toString). Common idiom in test262:
        // `'[object Math]' === Object.prototype.toString.call(Math)`.
        if (TryEmitObjectPrototypeToStringCall(c)) return;

        // `String(x)` / `Number(x)` / `Boolean(x)` — non-`new` coercion calls.
        // Compiled mode would otherwise resolve `String` as `typeof(string)` and
        // try to "call" the Type, which devolves to Stringify → wrong format for
        // objects with a custom toString. Match the syntactic pattern early so
        // these coerce via ECMA-262 ToString/ToNumber/ToBoolean.
        if (c.Callee is Expr.Variable coerceVar && c.Arguments.Count == 1)
        {
            switch (coerceVar.Name.Lexeme)
            {
                case "String":
                    EmitExpression(c.Arguments[0]);
                    EmitBoxIfNeeded(c.Arguments[0]);
                    IL.Emit(OpCodes.Call, _ctx.Runtime!.ToJsString);
                    SetStackUnknown();
                    return;
                case "Number":
                    EmitExpression(c.Arguments[0]);
                    EmitBoxIfNeeded(c.Arguments[0]);
                    IL.Emit(OpCodes.Call, _ctx.Runtime!.ConvertToNumber);
                    IL.Emit(OpCodes.Box, _ctx.Types.Double);
                    SetStackUnknown();
                    return;
                case "Boolean":
                    EmitExpression(c.Arguments[0]);
                    EmitBoxIfNeeded(c.Arguments[0]);
                    IL.Emit(OpCodes.Call, _ctx.Runtime!.IsTruthy);
                    IL.Emit(OpCodes.Box, _ctx.Types.Boolean);
                    SetStackUnknown();
                    return;
            }
        }

        // ECMA-262 non-callable singletons: JSON, Math, Reflect, Atomics
        // are objects without [[Call]] internal method. Calling them must
        // throw TypeError. Pattern-match the syntactic Variable callee so
        // we don't trip on user code that names a local/parameter the
        // same — only fires when the resolver agrees the name resolves
        // to the global (we approximate by checking the JSON/Math/etc.
        // singleton field exists in the runtime and the local table
        // doesn't claim the identifier).
        if (c.Callee is Expr.Variable nonCallableVar)
        {
            var name = nonCallableVar.Name.Lexeme;
            bool isNonCallableSingleton = name switch
            {
                "JSON" or "Math" or "Reflect" or "Atomics" => true,
                _ => false
            };
            if (isNonCallableSingleton
                && _ctx.Locals.GetLocal(name) == null
                && _ctx.Functions.TryGetValue(_ctx.ResolveFunctionName(name), out _) == false)
            {
                IL.Emit(OpCodes.Ldstr, name + " is not a function");
                IL.Emit(OpCodes.Newobj, _ctx.Runtime!.TSTypeErrorCtor);
                IL.Emit(OpCodes.Call, _ctx.Runtime!.CreateException);
                IL.Emit(OpCodes.Throw);
                IL.Emit(OpCodes.Ldnull);  // unreachable, balance stack
                SetStackUnknown();
                return;
            }
        }

        // External .NET type static methods (e.g., Console.WriteLine() via @DotNetType)
        // This is ILEmitter-only — requires TypeMapper.ExternalTypes + complex type conversion helpers
        if (c.Callee is Expr.Get externalStaticGet &&
            externalStaticGet.Object is Expr.Variable externalClassVar &&
            _ctx.TypeMapper?.ExternalTypes.TryGetValue(externalClassVar.Name.Lexeme, out var externalType) == true)
        {
            EmitExternalStaticMethodCall(externalType, externalStaticGet.Name.Lexeme, c.Arguments);
            return;
        }

        // For Expr.Get callees: run base class dispatch for handler chain, module.promises,
        // class statics, super.method, Promise.then/catch/finally, etc. If none match,
        // fall through to ILEmitter's EmitMethodCall for optimized instance method dispatch.
        if (c.Callee is Expr.Get methodGet)
        {
            // Handler chain: static types, Date.now, built-in modules, process streams,
            // globalThis chaining, imported/class-expr/this statics
            if (_callHandlers.TryHandle(this, c))
                return;

            // module.promises.methodName() (fs.promises, dns.promises, stream.promises)
            if (methodGet.Object is Expr.Get promisesGet &&
                promisesGet.Name.Lexeme == "promises" &&
                promisesGet.Object is Expr.Variable promisesModuleVar &&
                _ctx.BuiltInModuleNamespaces != null &&
                _ctx.BuiltInModuleNamespaces.TryGetValue(promisesModuleVar.Name.Lexeme, out var promisesModuleName) &&
                _ctx.BuiltInModuleEmitterRegistry?.GetEmitter(promisesModuleName + "/promises") is { } promisesEmitter)
            {
                if (promisesEmitter.TryEmitMethodCall(this, methodGet.Name.Lexeme, c.Arguments))
                {
                    SetStackUnknown();
                    return;
                }
            }

            // Class.staticMethod() with generic class support
            if (methodGet.Object is Expr.Variable classVar &&
                _ctx.Classes.TryGetValue(_ctx.ResolveClassName(classVar.Name.Lexeme), out var classBuilder))
            {
                string resolvedClassName = _ctx.ResolveClassName(classVar.Name.Lexeme);
                if (_ctx.ClassRegistry!.TryGetCallableStaticMethod(resolvedClassName, methodGet.Name.Lexeme, classBuilder, out var callableMethod))
                {
                    var staticMethodParams = callableMethod!.GetParameters();
                    for (int i = 0; i < c.Arguments.Count; i++)
                    {
                        EmitExpression(c.Arguments[i]);
                        if (i < staticMethodParams.Length)
                            EmitConversionForParameter(c.Arguments[i], staticMethodParams[i].ParameterType);
                        else
                            EmitBoxIfNeeded(c.Arguments[i]);
                    }
                    for (int i = c.Arguments.Count; i < staticMethodParams.Length; i++)
                        EmitDefaultForType(staticMethodParams[i].ParameterType);
                    IL.Emit(OpCodes.Call, callableMethod);
                    SetStackUnknown();
                    return;
                }
            }

            // Instance method dispatch (Array/String/Map/Promise/etc.)
            EmitMethodCall(methodGet, c.Arguments);
            return;
        }

        // All non-Get call patterns — delegate to base class
        base.EmitCall(c);
    }

    /// <summary>
    /// Resolves a type argument string to a .NET Type for generic instantiation.
    /// </summary>
    protected override Type ResolveTypeArg(string typeArg)
    {
        return typeArg switch
        {
            "number" => _ctx.Types.Double,
            "string" => _ctx.Types.String,
            "boolean" => _ctx.Types.Boolean,
            _ when _ctx.GenericTypeParameters.TryGetValue(typeArg, out var gp) => gp,
            _ when _ctx.Classes.TryGetValue(_ctx.ResolveClassName(typeArg), out var tb) => tb,
            _ => _ctx.Types.Object
        };
    }

    /// <summary>
    /// Detects <c>Object.prototype.toString.call(receiver)</c> and emits
    /// IL that returns the proper ECMA-262 brand string ("[object Math]",
    /// "[object Array]", "[object String]", "[object Null]", "[object Undefined]",
    /// "[object Object]"). Compiled mode doesn't emit a user-callable
    /// Object.prototype.toString, so without this idiom returns `undefined`.
    /// </summary>
    private bool TryEmitObjectPrototypeToStringCall(Expr.Call c)
    {
        // Pattern: Get("call", Get("toString", Get("prototype", Variable("Object"))))
        if (c.Callee is not Expr.Get callGet || callGet.Name.Lexeme != "call")
            return false;
        if (callGet.Object is not Expr.Get toStringGet || toStringGet.Name.Lexeme != "toString")
            return false;
        if (toStringGet.Object is not Expr.Get protoGet || protoGet.Name.Lexeme != "prototype")
            return false;
        if (protoGet.Object is not Expr.Variable objVar || objVar.Name.Lexeme != "Object")
            return false;

        var runtime = _ctx.Runtime!;

        // Push receiver (or undefined if no args)
        if (c.Arguments.Count == 0)
        {
            IL.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        }
        else
        {
            // Syntactic shortcut: `Object.prototype.toString.call(arguments)`
            // — `arguments` is bound as List<object> in compiled mode, which
            // would fall through to "[object Array]" in the runtime ladder.
            // Per ECMA-262 sloppy-arguments brand, emit directly.
            if (c.Arguments[0] is Expr.Variable v && v.Name.Lexeme == "arguments")
            {
                IL.Emit(OpCodes.Ldstr, "[object Arguments]");
                return true;
            }
            EmitExpression(c.Arguments[0]);
            EmitBoxIfNeeded(c.Arguments[0]);
        }

        // Inline branch ladder:
        // if (receiver == null) return "[object Null]"
        // if (receiver is $Undefined) return "[object Undefined]"
        // if (receiver == Math singleton) return "[object Math]"
        // if (receiver is List<object> || $Array) return "[object Array]"
        // if (receiver is string) return "[object String]"
        // if (receiver is bool) return "[object Boolean]"
        // if (receiver is double) return "[object Number]"
        // if (receiver is $TSFunction || $BoundTSFunction) return "[object Function]"
        // else return "[object Object]"
        var receiverLocal = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Stloc, receiverLocal);

        var endLabel = IL.DefineLabel();

        void EmitTag(string tag)
        {
            IL.Emit(OpCodes.Ldstr, tag);
            IL.Emit(OpCodes.Br, endLabel);
        }

        // null
        var notNullLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Brtrue, notNullLabel);
        EmitTag("[object Null]");
        IL.MarkLabel(notNullLabel);

        // undefined
        var notUndefLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Isinst, runtime.UndefinedType);
        IL.Emit(OpCodes.Brfalse, notUndefLabel);
        EmitTag("[object Undefined]");
        IL.MarkLabel(notUndefLabel);

        // Math singleton — reference equality
        var notMathLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Ldsfld, runtime.MathSingletonField);
        IL.Emit(OpCodes.Bne_Un, notMathLabel);
        EmitTag("[object Math]");
        IL.MarkLabel(notMathLabel);

        // JSON singleton — reference equality. Per ECMA-262 §25.5,
        // JSON has Symbol.toStringTag = "JSON" so toString returns
        // "[object JSON]". Test262 uses `Array.prototype.reduce.call(JSON, …)`
        // patterns that depend on this branding.
        var notJsonLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Ldsfld, runtime.JsonSingletonField);
        IL.Emit(OpCodes.Bne_Un, notJsonLabel);
        EmitTag("[object JSON]");
        IL.MarkLabel(notJsonLabel);

        // Number/String/Boolean/Array .prototype singletons each carry the
        // [[Class]] of their underlying type per ECMA-262 §21/§22/§20/§23.
        // E.g. Number.prototype is itself a Number Exotic Object → its
        // toString brand is "[object Number]". Without these, lookups via
        // Object.getPrototypeOf(new X(…)) fall through to "[object Object]".
        var notNumberProtoLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Ldsfld, runtime.NumberPrototypeField);
        IL.Emit(OpCodes.Bne_Un, notNumberProtoLabel);
        EmitTag("[object Number]");
        IL.MarkLabel(notNumberProtoLabel);

        var notStringProtoLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Ldsfld, runtime.StringPrototypeField);
        IL.Emit(OpCodes.Bne_Un, notStringProtoLabel);
        EmitTag("[object String]");
        IL.MarkLabel(notStringProtoLabel);

        var notBoolProtoLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Ldsfld, runtime.BooleanPrototypeField);
        IL.Emit(OpCodes.Bne_Un, notBoolProtoLabel);
        EmitTag("[object Boolean]");
        IL.MarkLabel(notBoolProtoLabel);

        var notArrayProtoLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Ldsfld, runtime.ArrayPrototypeField);
        IL.Emit(OpCodes.Bne_Un, notArrayProtoLabel);
        EmitTag("[object Array]");
        IL.MarkLabel(notArrayProtoLabel);

        // $Arguments : List<object> — sloppy arguments object marker. Must
        // come before the List<object> check since $Arguments inherits from it
        // and would otherwise fall through to "[object Array]".
        var notArgumentsTypeLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Isinst, runtime.ArgumentsType);
        IL.Emit(OpCodes.Brfalse, notArgumentsTypeLabel);
        EmitTag("[object Arguments]");
        IL.MarkLabel(notArgumentsTypeLabel);

        // object[] (raw .NET object[] — used by the $TSFunction _currentArguments
        // thread-static and a few legacy bridge paths). Distinct from $Arguments
        // but shares the brand tag.
        var notArgsLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Isinst, _ctx.Types.ObjectArray);
        IL.Emit(OpCodes.Brfalse, notArgsLabel);
        EmitTag("[object Arguments]");
        IL.MarkLabel(notArgsLabel);

        // List<object>
        var notListLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Isinst, _ctx.Types.ListOfObject);
        IL.Emit(OpCodes.Brfalse, notListLabel);
        EmitTag("[object Array]");
        IL.MarkLabel(notListLabel);

        // $Array
        var notTSArrayLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Isinst, runtime.TSArrayType);
        IL.Emit(OpCodes.Brfalse, notTSArrayLabel);
        EmitTag("[object Array]");
        IL.MarkLabel(notTSArrayLabel);

        // string
        var notStringLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Isinst, _ctx.Types.String);
        IL.Emit(OpCodes.Brfalse, notStringLabel);
        EmitTag("[object String]");
        IL.MarkLabel(notStringLabel);

        // bool
        var notBoolLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Isinst, _ctx.Types.Boolean);
        IL.Emit(OpCodes.Brfalse, notBoolLabel);
        EmitTag("[object Boolean]");
        IL.MarkLabel(notBoolLabel);

        // double
        var notDoubleLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Isinst, _ctx.Types.Double);
        IL.Emit(OpCodes.Brfalse, notDoubleLabel);
        EmitTag("[object Number]");
        IL.MarkLabel(notDoubleLabel);

        // $TSFunction
        var notTSFunctionLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        IL.Emit(OpCodes.Brfalse, notTSFunctionLabel);
        EmitTag("[object Function]");
        IL.MarkLabel(notTSFunctionLabel);

        // Default
        IL.Emit(OpCodes.Ldstr, "[object Object]");

        IL.MarkLabel(endLabel);
        SetStackUnknown();
        return true;
    }

    /// <summary>
    /// Detects <c>Array.prototype.METHOD.call(receiver, ...args)</c> and emits:
    /// <c>ArrayMETHOD($Runtime.ArrayLikeMaterialize(receiver), ...args)</c>.
    /// Only non-mutating methods are supported — mutating methods (push/pop/shift/
    /// unshift/splice/sort/reverse/copyWithin/fill) need to write indexed
    /// properties back onto the original receiver, which is out of scope here
    /// (matches the interpreter-side boundary in commit 04c4b2b).
    /// </summary>
    private bool TryEmitArrayPrototypeCall(Expr.Call c)
    {
        // Pattern: Get("call", Get(METHOD, Get("prototype", Variable("Array"))))
        if (c.Callee is not Expr.Get callGet || callGet.Name.Lexeme != "call")
            return false;
        if (callGet.Object is not Expr.Get methodGet)
            return false;
        if (methodGet.Object is not Expr.Get protoGet || protoGet.Name.Lexeme != "prototype")
            return false;
        if (protoGet.Object is not Expr.Variable arrayVar || arrayVar.Name.Lexeme != "Array")
            return false;

        var methodName = methodGet.Name.Lexeme;
        var runtime = _ctx.Runtime!;
        // Map method name → runtime MethodBuilder + calling convention.
        // singleArg = one JS arg passed as `object`; argsArray = all JS args
        // packaged into an `object[]`. boxes describes the return-type boxing.
        (MethodInfo Method, string Kind, Type Box)? sig = methodName switch
        {
            "every"         => (runtime.ArrayEvery,      "single",    _ctx.Types.Boolean),
            "some"          => (runtime.ArraySome,       "single",    _ctx.Types.Boolean),
            "filter"        => (runtime.ArrayFilter,     "single",    _ctx.Types.Object),
            "map"           => (runtime.ArrayMap,        "single",    _ctx.Types.Object),
            "forEach"       => (runtime.ArrayForEach,    "single",    _ctx.Types.Object),
            "find"          => (runtime.ArrayFind,       "single",    _ctx.Types.Object),
            "findIndex"     => (runtime.ArrayFindIndex,  "single",    _ctx.Types.Double),
            "findLast"      => (runtime.ArrayFindLast,   "single",    _ctx.Types.Object),
            "findLastIndex" => (runtime.ArrayFindLastIndex,"single",  _ctx.Types.Double),
            "includes"      => (runtime.ArrayIncludes,   "single",    _ctx.Types.Boolean),
            "join"          => (runtime.ArrayJoin,       "single",    _ctx.Types.Object),
            "concat"        => (runtime.ArrayConcat,     "argsArray", _ctx.Types.Object),
            "flat"          => (runtime.ArrayFlat,       "single",    _ctx.Types.Object),
            "flatMap"       => (runtime.ArrayFlatMap,    "single",    _ctx.Types.Object),
            "at"            => (runtime.ArrayAt,         "single",    _ctx.Types.Object),
            "reduce"        => (runtime.ArrayReduce,     "argsArray", _ctx.Types.Object),
            "reduceRight"   => (runtime.ArrayReduceRight,"argsArray", _ctx.Types.Object),
            "slice"         => (runtime.ArraySlice,      "argsArray", _ctx.Types.Object),
            "indexOf"       => (runtime.ArrayIndexOf,    "search",    _ctx.Types.Double),
            "lastIndexOf"   => (runtime.ArrayLastIndexOf,"search",    _ctx.Types.Double),
            "entries"       => (runtime.ArrayEntries,    "noArg",     _ctx.Types.Object),
            "keys"          => (runtime.ArrayKeys,       "noArg",     _ctx.Types.Object),
            "values"        => (runtime.ArrayValues,     "noArg",     _ctx.Types.Object),
            "toReversed"    => (runtime.ArrayToReversed, "noArg",     _ctx.Types.Object),
            "toSorted"      => (runtime.ArrayToSorted,   "single",    _ctx.Types.Object),
            "toSpliced"     => (runtime.ArrayToSpliced,  "argsArray", _ctx.Types.Object),
            "with"          => (runtime.ArrayWith,       "argsArray", _ctx.Types.Object),
            _ => null,
        };
        if (sig is null)
            return false;
        var (runtimeMethod, kind, boxType) = sig.Value;

        // args[0] is the thisArg (receiver); the rest are the method's own args.
        if (c.Arguments.Count == 0)
        {
            // Array.prototype.X.call() — spec: this is undefined, throw TypeError.
            // Easiest: still emit the materializer on null; it throws.
            IL.Emit(OpCodes.Ldnull);
            IL.Emit(OpCodes.Call, runtime.ArrayLikeMaterialize);
            // Unreachable after throw, but keep stack balanced for any dead-code
            // path verification. Load default return and box.
            IL.Emit(OpCodes.Ldnull);
            return true;
        }

        // Save the original receiver into a local, then stash it on the
        // `_currentArrayLikeReceiver` thread-static so `EmitCallbackArgsAndInvoke`
        // reads it as the callback's 3rd arg (O per ECMA-262). Restore after
        // the helper returns so nested calls don't leak.
        var receiverLocal = IL.DeclareLocal(_ctx.Types.Object);
        EmitExpression(c.Arguments[0]);
        EmitBoxIfNeeded(c.Arguments[0]);
        IL.Emit(OpCodes.Dup);
        IL.Emit(OpCodes.Stloc, receiverLocal);

        var prevReceiverLocal = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Ldsfld, runtime.CurrentArrayLikeReceiverField);
        IL.Emit(OpCodes.Stloc, prevReceiverLocal);
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Stsfld, runtime.CurrentArrayLikeReceiverField);

        var methodArgs = c.Arguments.Skip(1).ToList();

        // ECMA-262 lazy-iteration order: callback validation happens AFTER
        // ToLength(O.length) but BEFORE element reads. For the static-
        // missing-callback case (kind=="single" with no methodArgs), read
        // length to trigger any accessor side effects, then throw TypeError
        // without materializing element accessors. Test262 tests like
        // `Array.prototype.every.call(obj)` (no cb) check this ordering via
        // assert(lengthAccessed) + assert(loopAccessed === false).
        // Only applies to methods whose first arg is a REQUIRED callback —
        // includes/join/concat/flat/at take other arg shapes and don't throw
        // when called with no args.
        bool needsCallableFirstArg = methodName is "every" or "some" or "filter"
            or "map" or "forEach" or "find" or "findIndex" or "findLast"
            or "findLastIndex" or "flatMap" or "reduce" or "reduceRight";

        // Helper: emit IL that fires length-side-effects without iterating
        // elements. ECMA-262 LengthOfArrayLike calls Get(O, "length") then
        // ToLength → ToInteger → ToNumber → ToPrimitive(value, "number").
        // For test fixtures that set length to a getter returning an object
        // with a custom toString, both the length getter AND the toString
        // must fire before the IsCallable(callbackfn) check throws. We
        // approximate by calling GetProperty + ToJsString (does ToPrimitive
        // valueOf/toString chain). Stack-in: [], stack-out: [].
        void EmitLengthSideEffect()
        {
            IL.Emit(OpCodes.Ldloc, receiverLocal);
            IL.Emit(OpCodes.Ldstr, "length");
            IL.Emit(OpCodes.Call, runtime.GetProperty);
            IL.Emit(OpCodes.Call, runtime.ToJsString);
            IL.Emit(OpCodes.Pop);
        }

        if (methodArgs.Count == 0 && needsCallableFirstArg)
        {
            // Pop the duplicated receiver from the stack — we won't be
            // calling materialize.
            IL.Emit(OpCodes.Pop);
            EmitLengthSideEffect();
            // Throw: TypeError("undefined is not a function")
            IL.Emit(OpCodes.Ldstr, "undefined is not a function");
            IL.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
            IL.Emit(OpCodes.Call, runtime.CreateException);
            IL.Emit(OpCodes.Throw);
            // Unreachable, but keep stack balanced for any dead-code analysis.
            return true;
        }

        // Runtime null/undefined callback check (when user passes the literal
        // undefined or a null-valued variable). Without this, the materializer
        // fires accessor getters on element indices BEFORE the runtime helper
        // validates callbackfn — but ECMA-262 requires "ToLength(O.length) →
        // throw on bad callback → iterate" order. Test262 tests like
        // `Array.prototype.reduceRight.call(obj, undefined)` set
        // Object.defineProperty(obj, "0", {get: side-effect}) and assert the
        // side effect did NOT fire. Read length + ToJsString first (spec
        // wants this access AND any toString side effects on the returned
        // value), then throw without invoking element getters.
        if (needsCallableFirstArg && methodArgs.Count >= 1)
        {
            var cbLocal = IL.DeclareLocal(_ctx.Types.Object);
            EmitExpression(methodArgs[0]);
            EmitBoxIfNeeded(methodArgs[0]);
            IL.Emit(OpCodes.Stloc, cbLocal);

            var throwPath = IL.DefineLabel();
            var cbValid = IL.DefineLabel();
            // null → throw
            IL.Emit(OpCodes.Ldloc, cbLocal);
            IL.Emit(OpCodes.Brfalse, throwPath);
            // $Undefined → throw
            IL.Emit(OpCodes.Ldloc, cbLocal);
            IL.Emit(OpCodes.Isinst, runtime.UndefinedType);
            IL.Emit(OpCodes.Brtrue, throwPath);
            IL.Emit(OpCodes.Br, cbValid);

            IL.MarkLabel(throwPath);
            IL.Emit(OpCodes.Pop); // Pop the duplicated receiver.
            EmitLengthSideEffect();
            IL.Emit(OpCodes.Ldstr, "undefined is not a function");
            IL.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
            IL.Emit(OpCodes.Call, runtime.CreateException);
            IL.Emit(OpCodes.Throw);

            IL.MarkLabel(cbValid);
            // The callback expression is re-emitted later when methodArgs
            // are materialized into the call. This is a benign double-eval
            // (callbacks are typically bare identifiers / literals); we
            // tolerate it to avoid a methodArgs rewrite.
        }

        // list = ArrayLikeMaterialize(receiver) — the Dup'd receiver is on the stack
        IL.Emit(OpCodes.Call, runtime.ArrayLikeMaterialize);

        // For iterator methods that accept thisArg (callbackfn, thisArg) per
        // ECMA-262, save the previous _currentCallbackThisArg, stash methodArgs[1]
        // (or null) into the thread-static, then restore after the call.
        // EmitCallbackArgsAndInvoke reads it as the receiver to InvokeMethodValue.
        bool hasThisArgSlot = methodName is "every" or "some" or "filter" or "map"
            or "forEach" or "find" or "findIndex" or "findLast" or "findLastIndex"
            or "flatMap";
        LocalBuilder? prevThisArgLocal = null;
        if (hasThisArgSlot)
        {
            prevThisArgLocal = IL.DeclareLocal(_ctx.Types.Object);
            IL.Emit(OpCodes.Ldsfld, runtime.CurrentCallbackThisArgField);
            IL.Emit(OpCodes.Stloc, prevThisArgLocal);
            if (methodArgs.Count >= 2)
            {
                EmitExpression(methodArgs[1]);
                EmitBoxIfNeeded(methodArgs[1]);
            }
            else
            {
                IL.Emit(OpCodes.Ldnull);
            }
            IL.Emit(OpCodes.Stsfld, runtime.CurrentCallbackThisArgField);
        }

        switch (kind)
        {
            case "single":
                if (methodArgs.Count > 0)
                {
                    EmitExpression(methodArgs[0]);
                    EmitBoxIfNeeded(methodArgs[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
                break;
            case "search":
                // searchElement + optional fromIndex
                if (methodArgs.Count > 0)
                {
                    EmitExpression(methodArgs[0]);
                    EmitBoxIfNeeded(methodArgs[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
                if (methodArgs.Count > 1)
                {
                    EmitExpression(methodArgs[1]);
                    EmitBoxIfNeeded(methodArgs[1]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
                break;
            case "argsArray":
                // Push the method args as an object[].
                IL.Emit(OpCodes.Ldc_I4, methodArgs.Count);
                IL.Emit(OpCodes.Newarr, _ctx.Types.Object);
                for (int i = 0; i < methodArgs.Count; i++)
                {
                    IL.Emit(OpCodes.Dup);
                    IL.Emit(OpCodes.Ldc_I4, i);
                    EmitExpression(methodArgs[i]);
                    EmitBoxIfNeeded(methodArgs[i]);
                    IL.Emit(OpCodes.Stelem_Ref);
                }
                break;
            case "noArg":
                // Helper takes only the materialized list — no extra args
                // (entries/keys/values).
                break;
        }

        IL.Emit(OpCodes.Call, runtimeMethod);

        // Box numeric returns to match expression-as-value conventions.
        // Void returns (forEach) need a Ldnull pushed so the caller has a
        // value on the stack — otherwise the JIT throws InvalidProgramException.
        // Inspect the actual ReturnType of the helper rather than the boxType
        // sigil so already-boxed `object`-returning helpers (every/some/etc.)
        // don't get a stray extra Box opcode that corrupts strict-equality
        // checks (`Array.prototype.every.call(...) === true` returned false
        // because the double-box yielded a different object identity).
        var rt = runtimeMethod.ReturnType;
        if (rt == typeof(void))
            IL.Emit(OpCodes.Ldnull);
        else if (rt == _ctx.Types.Double)
            IL.Emit(OpCodes.Box, _ctx.Types.Double);
        else if (rt == _ctx.Types.Boolean)
            IL.Emit(OpCodes.Box, _ctx.Types.Boolean);
        // else (already _types.Object / List<object> / etc.) → no boxing.

        // Restore the thread-static so nested prototype.call contexts don't leak.
        // Save result in temp, restore field, push result.
        var resultTmp = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Stloc, resultTmp);
        IL.Emit(OpCodes.Ldloc, prevReceiverLocal);
        IL.Emit(OpCodes.Stsfld, runtime.CurrentArrayLikeReceiverField);
        if (prevThisArgLocal != null)
        {
            IL.Emit(OpCodes.Ldloc, prevThisArgLocal);
            IL.Emit(OpCodes.Stsfld, runtime.CurrentCallbackThisArgField);
        }
        IL.Emit(OpCodes.Ldloc, resultTmp);

        SetStackUnknown();
        return true;
    }
}
