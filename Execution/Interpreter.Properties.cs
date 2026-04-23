using SharpTS.Parsing;
using SharpTS.Runtime;
using SharpTS.Runtime.BuiltIns;
using SharpTS.Runtime.DotNet;
using SharpTS.Runtime.Exceptions;
using SharpTS.Runtime.Types;
using SharpTS.TypeSystem;

namespace SharpTS.Execution;

// Note: This file uses InterpreterException for runtime errors

public partial class Interpreter
{
    /// <summary>
    /// Extracts the simple class name from a new expression callee for runtime use.
    /// </summary>
    private static string? GetSimpleClassName(Expr callee)
    {
        return callee is Expr.Variable v ? v.Name.Lexeme : null;
    }

    /// <summary>
    /// Checks if the callee is a simple identifier (not a member access or complex expression).
    /// </summary>
    private static bool IsSimpleIdentifier(Expr callee) => callee is Expr.Variable;

    /// <summary>
    /// Core implementation for evaluating 'new' expressions, shared between sync and async paths.
    /// Handles all built-in types (Date, RegExp, Map, Set, WeakMap, WeakSet, Error) and user classes.
    /// </summary>
    /// <param name="ctx">The evaluation context for evaluating arguments.</param>
    /// <param name="newExpr">The new expression AST node.</param>
    /// <returns>A ValueTask containing the instantiated object.</returns>
    private async ValueTask<object?> EvaluateNewCore(IEvaluationContext ctx, Expr.New newExpr)
    {
        // Built-in types only apply when callee is a simple identifier
        bool isSimpleName = IsSimpleIdentifier(newExpr.Callee);
        string? simpleClassName = GetSimpleClassName(newExpr.Callee);

        // Handle built-in constructors via factory
        if (isSimpleName && simpleClassName != null)
        {
            // Special case: Promise needs executor function evaluation, not standard arg evaluation
            if (simpleClassName == BuiltInNames.Promise)
            {
                if (newExpr.Arguments.Count != 1)
                {
                    throw new InterpreterException($"{BuiltInNames.Promise} constructor requires exactly 1 argument (executor function), got {newExpr.Arguments.Count}.");
                }
                object? executor = (await ctx.EvaluateExprAsync(newExpr.Arguments[0])).ToObject();
                return CreatePromiseFromExecutor(executor);
            }

            // Try factory for all other built-in constructors
            if (BuiltInConstructorFactory.IsBuiltIn(simpleClassName))
            {
                List<object?> args = await ctx.EvaluateAllAsync(newExpr.Arguments);
                return BuiltInConstructorFactory.TryCreate(simpleClassName, args, this);
            }
        }

        // Evaluate the callee expression to get the class/constructor
        object? klass = (await ctx.EvaluateExprAsync(newExpr.Callee)).ToObject();

        // Handle Proxy construct trap
        if (klass is SharpTSProxy proxy)
        {
            List<object?> proxyArgs = await ctx.EvaluateAllAsync(newExpr.Arguments);
            return proxy.TrapConstruct(proxyArgs, this);
        }

        // Constructor-function pattern: `function Foo() { if (!(this instanceof Foo)) return new Foo(); this.x = 1; }`.
        // Node's CJS packages (e.g. yallist) rely on JS `new` semantics
        // binding `this` to a fresh object whose prototype is Foo.prototype.
        // Without this, `self instanceof Yallist` is false and packages
        // recurse infinitely.
        if (klass is SharpTSFunction userFn)
        {
            List<object?> fnArgs = await ctx.EvaluateAllAsync(newExpr.Arguments);
            // Build a new `this` object backed by the function's prototype.
            if (!userFn.TryGetProperty("prototype", out var protoObj))
            {
                protoObj = new SharpTSObject(new Dictionary<string, object?>());
                userFn.SetProperty("prototype", protoObj);
            }
            var newThis = new SharpTSObject(new Dictionary<string, object?>
            {
                ["__proto__"] = protoObj,
                ["constructor"] = userFn,
            });
            var bound = userFn.BindThis(newThis);
            var result = bound.Call(this, fnArgs);
            // JS spec: if constructor returns an object, use it; otherwise use the new `this`.
            return result is SharpTSObject or SharpTSInstance or SharpTSArray
                ? result
                : newThis;
        }

        // Handle callable constructors (like SharpTSEventEmitterConstructor)
        // These implement ISharpTSCallable and are used for module-imported types
        if (klass is ISharpTSCallable callable && klass is not SharpTSClass && klass is not BoundFunction)
        {
            List<object?> ctorArgs = await ctx.EvaluateAllAsync(newExpr.Arguments);
            return callable.Call(this, ctorArgs);
        }

        // Bound functions cannot be used as constructors (JS spec compliance)
        if (klass is BoundFunction)
        {
            throw new InterpreterException("Bound functions cannot be used as constructors.");
        }

        if (klass is not SharpTSClass sharpClass)
        {
             throw new InterpreterException("Can only instantiate classes.");
        }

        // Runtime check for abstract class instantiation (backup to type checker)
        if (sharpClass.IsAbstract)
        {
            throw new InterpreterException($"Cannot create an instance of abstract class '{sharpClass.Name}'.");
        }

        List<object?> arguments = await ctx.EvaluateAllAsync(newExpr.Arguments);
        return sharpClass.Call(this, arguments);
    }

    /// <summary>
    /// Evaluates a <c>new</c> expression, instantiating a class.
    /// </summary>
    /// <param name="newExpr">The new expression AST node.</param>
    /// <returns>A new <see cref="SharpTSInstance"/> of the class.</returns>
    /// <remarks>
    /// Looks up the class by evaluating the callee expression,
    /// and invokes the class's <see cref="SharpTSClass.Call"/> method.
    /// Supports new on expressions: new ctor(), new Namespace.Class(), new (expr)()
    /// </remarks>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/2/classes.html#constructors">TypeScript Constructors</seealso>
    private RuntimeValue EvaluateNew(Expr.New newExpr)
    {
        // Built-in types only apply when callee is a simple identifier
        bool isSimpleName = IsSimpleIdentifier(newExpr.Callee);
        string? simpleClassName = GetSimpleClassName(newExpr.Callee);

        // Handle built-in constructors via factory
        if (isSimpleName && simpleClassName != null)
        {
            // Special case: Promise needs executor function evaluation, not standard arg evaluation
            if (simpleClassName == BuiltInNames.Promise)
            {
                if (newExpr.Arguments.Count != 1)
                {
                    throw new InterpreterException($"{BuiltInNames.Promise} constructor requires exactly 1 argument (executor function), got {newExpr.Arguments.Count}.");
                }
                object? executor = Evaluate(newExpr.Arguments[0]);
                return RuntimeValue.FromObject(CreatePromiseFromExecutor(executor));
            }

            // Try factory for all other built-in constructors
            if (BuiltInConstructorFactory.IsBuiltIn(simpleClassName))
            {
                List<object?> args = [];
                foreach (var arg in newExpr.Arguments)
                {
                    args.Add(Evaluate(arg));
                }
                return BuiltInConstructorFactory.TryCreateRV(simpleClassName, args, this);
            }
        }

        // Evaluate the callee expression to get the class/constructor
        object? klass = Evaluate(newExpr.Callee);

        // Handle Proxy construct trap
        if (klass is SharpTSProxy proxy)
        {
            List<object?> proxyArgs = [];
            foreach (var arg in newExpr.Arguments)
            {
                proxyArgs.Add(Evaluate(arg));
            }
            return proxy.TrapConstructRV(proxyArgs, this);
        }

        // Constructor-function pattern: `function Foo() { this.x = 1 }` called with `new`.
        // Build a fresh `this` with prototype linkage, bind it, then call —
        // so `this instanceof Foo` returns true (Node CJS pattern used by
        // e.g. yallist, EventEmitter sub-classes).
        if (klass is SharpTSFunction userFn)
        {
            List<object?> fnArgs = [];
            foreach (var arg in newExpr.Arguments)
            {
                fnArgs.Add(Evaluate(arg));
            }
            if (!userFn.TryGetProperty("prototype", out var protoObj))
            {
                protoObj = new SharpTSObject(new Dictionary<string, object?>());
                userFn.SetProperty("prototype", protoObj);
            }
            var newThis = new SharpTSObject(new Dictionary<string, object?>
            {
                ["__proto__"] = protoObj,
                ["constructor"] = userFn,
            });
            var bound = userFn.BindThis(newThis);
            var result = bound.Call(this, fnArgs);
            return RuntimeValue.FromBoxed(result is SharpTSObject or SharpTSInstance or SharpTSArray
                ? result
                : newThis);
        }

        // Handle callable constructors (like SharpTSEventEmitterConstructor)
        // These implement ISharpTSCallable and are used for module-imported types
        if (klass is ISharpTSCallable callable && klass is not SharpTSClass && klass is not BoundFunction)
        {
            List<object?> ctorArgs = [];
            foreach (var arg in newExpr.Arguments)
            {
                ctorArgs.Add(Evaluate(arg));
            }
            return RuntimeValue.FromBoxed(callable.Call(this, ctorArgs));
        }

        // Bound functions cannot be used as constructors (JS spec compliance)
        if (klass is BoundFunction)
        {
            throw new InterpreterException("Bound functions cannot be used as constructors.");
        }

        if (klass is not SharpTSClass sharpClass)
        {
             throw new InterpreterException("Can only instantiate classes.");
        }

        // Runtime check for abstract class instantiation (backup to type checker)
        if (sharpClass.IsAbstract)
        {
            throw new InterpreterException($"Cannot create an instance of abstract class '{sharpClass.Name}'.");
        }

        List<object?> arguments = [];
        foreach (var arg in newExpr.Arguments)
        {
            arguments.Add(Evaluate(arg));
        }
        return sharpClass.CallRV(this, arguments);
    }

    /// <summary>
    /// Evaluates a <c>this</c> expression, returning the current instance.
    /// </summary>
    /// <param name="expr">The this expression AST node.</param>
    /// <returns>The current class instance bound to <c>this</c>.</returns>
    /// <remarks>
    /// The <c>this</c> keyword is bound in the environment when a method is called
    /// on an instance.
    /// </remarks>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/2/classes.html#this-at-runtime-in-classes">TypeScript this in Classes</seealso>
    private RuntimeValue EvaluateThis(Expr.This expr)
    {
        return _environment.Get(expr.Keyword);
    }

    /// <summary>
    /// Evaluates a property access expression (dot notation).
    /// </summary>
    /// <param name="get">The property access expression AST node.</param>
    /// <returns>The value of the property, or a bound method.</returns>
    /// <remarks>
    /// Handles optional chaining (<c>?.</c>), static member access on classes,
    /// enum member access, instance properties/methods, object properties,
    /// string methods, array methods, and Math object members.
    /// </remarks>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/2/objects.html">TypeScript Object Types</seealso>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/release-notes/typescript-3-7.html#optional-chaining">TypeScript Optional Chaining</seealso>
    private RuntimeValue EvaluateGet(Expr.Get get)
    {
        // Handle namespace static property access (e.g., Number.MAX_VALUE, Number.NaN)
        // These namespaces don't have runtime values, but have static properties
        if (get.Object is Expr.Variable nsVar)
        {
            var member = BuiltInRegistry.Instance.GetStaticMethod(nsVar.Name.Lexeme, get.Name.Lexeme);
            if (member != null)
            {
                // Only invoke when the method is marked as wrapping a constant (e.g. Number.MAX_VALUE).
                // Previously the check was `MinArity == 0 && MaxArity == 0`, which also matched real
                // zero-arity methods like Date.now — breaking the `const nativeNow = Date.now;
                // nativeNow()` aliasing idiom (used by lodash/minimatch/etc.). IsConstant is set
                // by BuiltInMethod.CreateConstant / BuiltInStaticBuilder.CallableConstant.
                if (member is BuiltInMethod bm && bm.IsConstant)
                {
                    return RuntimeValue.FromBoxed(bm.Call(this, []));
                }
                return RuntimeValue.FromObject(member);
            }
        }

        object? obj = Evaluate(get.Object);
        return EvaluateGetOnObject(get, obj);
    }

    /// <summary>
    /// Core property access logic, shared between sync and async evaluation.
    /// Uses TypeCategoryResolver for unified type dispatch.
    /// </summary>
    private RuntimeValue EvaluateGetOnObject(Expr.Get get, object? obj)
    {
        // Handle optional chaining - return undefined if object is null or undefined
        if (get.Optional && (obj == null || obj is Runtime.Types.SharpTSUndefined))
        {
            return RuntimeValue.Undefined;
        }

        // Proxy interception - must be before any other dispatch
        if (obj is SharpTSProxy proxy)
        {
            return proxy.TrapGetRV(get.Name.Lexeme, this);
        }

        var category = TypeCategoryResolver.ClassifyRuntime(obj);
        string memberName = get.Name.Lexeme;

        // Category-based dispatch
        return category switch
        {
            // @DotNetType external types
            TypeCategory.External when obj is DotNetInstance dotNetInstance =>
                RuntimeValue.FromBoxed(dotNetInstance.GetMember(memberName)),
            TypeCategory.External when obj is DotNetClass dotNetClass =>
                RuntimeValue.FromBoxed(dotNetClass.GetStaticMember(memberName)),

            // User-defined types
            TypeCategory.Class when obj is SharpTSClass klass =>
                EvaluateGetOnClassRV(klass, memberName),
            TypeCategory.Namespace when obj is SharpTSNamespace nsObj =>
                EvaluateGetOnNamespaceRV(nsObj, memberName),
            TypeCategory.Enum when obj is SharpTSEnum enumObj =>
                enumObj.GetMemberRV(memberName),
            TypeCategory.Enum when obj is ConstEnumValues constEnumObj =>
                constEnumObj.GetMemberRV(memberName),
            TypeCategory.Instance when obj is SharpTSInstance instance =>
                EvaluateGetOnInstanceRV(instance, get.Name),
            TypeCategory.Record when obj is SharpTSObject simpleObj =>
                EvaluateGetOnRecordRV(simpleObj, memberName),

            // Array: needs override checks (named properties, ISharpTSPropertyAccessor)
            TypeCategory.Array => EvaluateGetOnArrayRV(obj!, memberName),

            // Fast path: built-in types with category-indexed dispatch
            _ when BuiltInRegistry.Instance.HasCategoryType(category) =>
                EvaluateGetOnBuiltInRV(category, obj!, memberName),

            // Fallback for remaining types (IDictionary, ISharpTSPropertyAccessor, unknown types)
            _ => RuntimeValue.FromBoxed(EvaluateGetOnFallback(obj, memberName))
        };
    }

    /// <summary>
    /// Evaluates property access on a class (static members).
    /// </summary>
    private RuntimeValue EvaluateGetOnClassRV(SharpTSClass klass, string memberName)
        => RuntimeValue.FromBoxed(EvaluateGetOnClass(klass, memberName));

    private static RuntimeValue EvaluateGetOnNamespaceRV(SharpTSNamespace nsObj, string memberName)
        => RuntimeValue.FromBoxed(EvaluateGetOnNamespace(nsObj, memberName));

    /// <summary>
    /// Fast path for property access on built-in types (string, number, map, set, etc.).
    /// Uses TypeCategory-indexed array dispatch instead of GetType() + Dictionary lookup.
    /// </summary>
    private RuntimeValue EvaluateGetOnBuiltInRV(TypeCategory category, object obj, string memberName)
    {
        // JS functions are objects — surface user-set properties before
        // falling through to built-in members (e.g. `bind`, `call`).
        if (obj is SharpTSFunction fn)
        {
            // Accessor defined via Object.defineProperty(fn, name, {get, set}).
            if (fn.TryGetAccessor(memberName, out var getter, out _) && getter != null)
            {
                return RuntimeValue.FromBoxed(getter.Call(this, []));
            }
            if (fn.TryGetProperty(memberName, out var userProp))
                return RuntimeValue.FromBoxed(userProp);
            // Lazy-init `fn.prototype` on first access (JS semantics).
            if (memberName == "prototype")
            {
                var proto = new SharpTSObject(new Dictionary<string, object?>());
                fn.SetProperty("prototype", proto);
                return RuntimeValue.FromBoxed(proto);
            }
        }
        if (obj is SharpTSArrowFunction arrowFn)
        {
            if (arrowFn.TryGetAccessor(memberName, out var getter, out _) && getter != null)
                return RuntimeValue.FromBoxed(getter.Call(this, []));
            if (arrowFn.TryGetProperty(memberName, out var arrowProp))
                return RuntimeValue.FromBoxed(arrowProp);
        }
        if (obj is SharpTSAsyncFunction asyncFn && asyncFn.TryGetProperty(memberName, out var asyncProp))
            return RuntimeValue.FromBoxed(asyncProp);
        if (obj is SharpTSAsyncArrowFunction asyncArrowFn && asyncArrowFn.TryGetProperty(memberName, out var asyncArrowProp))
            return RuntimeValue.FromBoxed(asyncArrowProp);

        var member = BuiltInRegistry.Instance.GetMemberByCategory(category, obj, memberName);
        if (member != null)
            return RuntimeValue.FromBoxed(BindBuiltInMember(member, obj));

        return RuntimeValue.Undefined;
    }

    /// <summary>
    /// Property access on arrays. Checks named properties and ISharpTSPropertyAccessor
    /// before falling through to built-in array members.
    /// </summary>
    private static RuntimeValue EvaluateGetOnArrayRV(object obj, string memberName)
    {
        // ISharpTSPropertyAccessor check (handles SharpTSTemplateStringsArray.raw)
        if (obj is ISharpTSPropertyAccessor accessor && accessor.HasProperty(memberName))
            return RuntimeValue.FromBoxed(accessor.GetProperty(memberName));

        // Named properties from Object.defineProperty
        if (obj is SharpTSArray array && array.HasNamedProperty(memberName))
            return RuntimeValue.FromBoxed(array.GetNamedProperty(memberName));

        // Standard array built-in members via category dispatch
        var member = BuiltInRegistry.Instance.GetMemberByCategory(TypeCategory.Array, obj, memberName);
        if (member != null)
            return RuntimeValue.FromBoxed(BindBuiltInMember(member, obj));

        return RuntimeValue.Undefined;
    }

    /// <summary>
    /// Binds a method to its receiver if needed. Methods from BuiltInTypeMemberLookup
    /// are already bound; inline BuiltInMethod instances need binding.
    /// </summary>
    private static object BindBuiltInMember(object member, object receiver)
    {
        if (member is BuiltInMethod m && !m.IsBound)
            return m.Bind(receiver);
        if (member is BuiltInAsyncMethod am)
            return am.Bind(receiver);
        return member;
    }

    private object? EvaluateGetOnClass(SharpTSClass klass, string memberName)
    {
        // Try static auto-accessor first (TypeScript 4.9+)
        if (klass.HasStaticAutoAccessor(memberName))
        {
            return klass.GetStaticAutoAccessorValue(memberName);
        }

        // Try static getter (`static get name() { ... }`).
        var staticGetter = klass.FindStaticGetter(memberName);
        if (staticGetter != null)
        {
            return staticGetter.BindStatic(klass).Call(this, []);
        }

        // Try static method
        ISharpTSCallable? staticMethod = klass.FindStaticMethod(memberName);
        if (staticMethod != null) return staticMethod switch
        {
            SharpTSFunction fn => fn.BindStatic(klass),
            SharpTSAsyncFunction afn => afn.BindStatic(klass),
            SharpTSGeneratorFunction gfn => gfn.BindStatic(klass),
            SharpTSAsyncGeneratorFunction agfn => agfn.BindStatic(klass),
            _ => staticMethod,
        };

        // Try static property
        if (klass.HasStaticProperty(memberName))
        {
            return klass.GetStaticProperty(memberName);
        }

        throw new InterpreterException($"Static member '{memberName}' does not exist on class '{klass.Name}'.");
    }

    /// <summary>
    /// Evaluates property access on a namespace.
    /// </summary>
    private static object? EvaluateGetOnNamespace(SharpTSNamespace nsObj, string memberName)
    {
        if (nsObj.HasMember(memberName))
        {
            return nsObj.Get(memberName);
        }
        throw new InterpreterException($"'{memberName}' does not exist on namespace '{nsObj.Name}'.");
    }

    /// <summary>
    /// Evaluates property access on a class instance.
    /// </summary>
    private object? EvaluateGetOnInstance(SharpTSInstance instance, Token memberName)
    {
        instance.SetInterpreter(this);
        return instance.Get(memberName);
    }

    /// <summary>
    /// Evaluates property access on a class instance, returning RuntimeValue directly.
    /// </summary>
    private RuntimeValue EvaluateGetOnInstanceRV(SharpTSInstance instance, Token memberName)
    {
        instance.SetInterpreter(this);
        return instance.GetRV(memberName);
    }

    /// <summary>
    /// Evaluates property access on a record/object literal, walking the __proto__
    /// chain when the property is not an own property. JS spec: property access
    /// traverses the prototype chain until a match or null is reached.
    /// </summary>
    private object? EvaluateGetOnRecord(SharpTSObject simpleObj, string memberName)
    {
        // Check for getter first on the own object
        var getter = simpleObj.GetGetter(memberName);
        if (getter != null)
        {
            // Invoke the getter with 'this' bound to the object
            var boundGetter = BindAccessorToObject(getter, simpleObj);
            return boundGetter.Call(this, []);
        }

        // Own property
        if (simpleObj.HasProperty(memberName))
        {
            var value = simpleObj.GetProperty(memberName);
            // Bind 'this' for function expressions and object method shorthand (HasOwnThis=true)
            if (value is SharpTSArrowFunction arrowFunc && arrowFunc.HasOwnThis)
            {
                return arrowFunc.Bind(simpleObj);
            }
            return value;
        }

        // Walk __proto__ chain — JS constructor-function pattern relies on this so methods
        // assigned via `Foo.prototype.x = ...` are reachable on `new Foo()` instances.
        // Lodash's MapCache (lodash.js ~2177) does `this.clear()` in its ctor where `clear`
        // lives on `MapCache.prototype`; without this walk it resolves to undefined.
        object? current = simpleObj.HasProperty("__proto__") ? simpleObj.GetProperty("__proto__") : null;
        for (int i = 0; i < 64 && current is SharpTSObject proto; i++)
        {
            var protoGetter = proto.GetGetter(memberName);
            if (protoGetter != null)
            {
                var boundProtoGetter = BindAccessorToObject(protoGetter, simpleObj);
                return boundProtoGetter.Call(this, []);
            }
            if (proto.HasProperty(memberName))
            {
                var value = proto.GetProperty(memberName);
                if (value is SharpTSArrowFunction arrowFunc && arrowFunc.HasOwnThis)
                {
                    return arrowFunc.Bind(simpleObj);
                }
                return value;
            }
            object? next = proto.HasProperty("__proto__") ? proto.GetProperty("__proto__") : null;
            if (ReferenceEquals(next, proto)) break; // cycle guard
            current = next;
        }

        return SharpTSUndefined.Instance;
    }

    /// <summary>
    /// Evaluates property access on a record/object literal, returning RuntimeValue directly.
    /// Walks __proto__ on miss so constructor-function prototype methods are reachable
    /// (see <see cref="EvaluateGetOnRecord"/> for full rationale).
    /// </summary>
    private RuntimeValue EvaluateGetOnRecordRV(SharpTSObject simpleObj, string memberName)
    {
        // Check for getter first on the own object
        var getter = simpleObj.GetGetter(memberName);
        if (getter != null)
        {
            var boundGetter = BindAccessorToObject(getter, simpleObj);
            // V2 fast path for getter invocation
            if (boundGetter is ISharpTSCallableV2 v2Getter)
                return v2Getter.CallV2(this, ReadOnlySpan<RuntimeValue>.Empty);
            return RuntimeValue.FromBoxed(boundGetter.Call(this, []));
        }

        if (simpleObj.HasProperty(memberName))
        {
            var value = simpleObj.GetProperty(memberName);
            if (value is SharpTSArrowFunction arrowFunc && arrowFunc.HasOwnThis)
            {
                return RuntimeValue.FromObject(arrowFunc.Bind(simpleObj));
            }
            return RuntimeValue.FromBoxed(value);
        }

        // Prototype-chain fallback
        object? current = simpleObj.HasProperty("__proto__") ? simpleObj.GetProperty("__proto__") : null;
        for (int i = 0; i < 64 && current is SharpTSObject proto; i++)
        {
            var protoGetter = proto.GetGetter(memberName);
            if (protoGetter != null)
            {
                var boundProtoGetter = BindAccessorToObject(protoGetter, simpleObj);
                if (boundProtoGetter is ISharpTSCallableV2 v2Proto)
                    return v2Proto.CallV2(this, ReadOnlySpan<RuntimeValue>.Empty);
                return RuntimeValue.FromBoxed(boundProtoGetter.Call(this, []));
            }
            if (proto.HasProperty(memberName))
            {
                var value = proto.GetProperty(memberName);
                if (value is SharpTSArrowFunction arrowFunc && arrowFunc.HasOwnThis)
                {
                    return RuntimeValue.FromObject(arrowFunc.Bind(simpleObj));
                }
                return RuntimeValue.FromBoxed(value);
            }
            object? next = proto.HasProperty("__proto__") ? proto.GetProperty("__proto__") : null;
            if (ReferenceEquals(next, proto)) break;
            current = next;
        }

        return RuntimeValue.Undefined;
    }

    /// <summary>
    /// Binds an accessor function to an object for 'this' binding.
    /// </summary>
    private static ISharpTSCallable BindAccessorToObject(ISharpTSCallable accessor, SharpTSObject obj)
    {
        if (accessor is SharpTSArrowFunction arrow && arrow.HasOwnThis)
        {
            return arrow.Bind(obj);
        }
        // For callables that don't support binding, return as-is
        return accessor;
    }

    /// <summary>
    /// Fallback for property access on built-in types and ISharpTSPropertyAccessor.
    /// </summary>
    private object? EvaluateGetOnFallback(object? obj, string memberName)
    {
        // JS functions are objects — support arbitrary property access on
        // user-defined functions. Built-in keys (`name`, `length`) come
        // from the function itself; user keys come from the property bag.
        if (obj is SharpTSFunction fn)
        {
            if (fn.TryGetAccessor(memberName, out var getter, out _) && getter != null)
                return getter.Call(this, []);
            if (fn.TryGetProperty(memberName, out var v)) return v;
            if (memberName == "name") return fn.TryGetProperty("name", out var n) ? n : "";
            if (memberName == "length") return (double)fn.Arity();
            if (memberName == "prototype")
            {
                if (!fn.TryGetProperty("prototype", out var proto))
                {
                    proto = new SharpTSObject(new Dictionary<string, object?>());
                    fn.SetProperty("prototype", proto);
                }
                return proto;
            }
            return SharpTSUndefined.Instance;
        }
        if (obj is SharpTSArrowFunction arrowFn2)
        {
            if (arrowFn2.TryGetAccessor(memberName, out var arrowGetter, out _) && arrowGetter != null)
                return arrowGetter.Call(this, []);
            if (arrowFn2.TryGetProperty(memberName, out var arrowProp2)) return arrowProp2;
            if (memberName == "length") return (double)arrowFn2.Arity();
            return SharpTSUndefined.Instance;
        }
        if (obj is SharpTSAsyncFunction asyncFn2)
        {
            if (asyncFn2.TryGetProperty(memberName, out var asyncProp2)) return asyncProp2;
            return SharpTSUndefined.Instance;
        }
        if (obj is SharpTSAsyncArrowFunction asyncArrowFn2)
        {
            if (asyncArrowFn2.TryGetProperty(memberName, out var asyncArrowProp2)) return asyncArrowProp2;
            return SharpTSUndefined.Instance;
        }

        // Array global constructor: resolves `Array.prototype`, `Array.from`, etc.
        if (obj is SharpTSArrayGlobal arrGlobal)
        {
            return arrGlobal.GetMember(memberName) ?? SharpTSUndefined.Instance;
        }
        if (obj is SharpTSArrayPrototype arrProto)
        {
            return arrProto.GetMember(memberName) ?? SharpTSUndefined.Instance;
        }
        if (obj is SharpTSArrayUnboundMethod unbound)
        {
            // call/apply/bind on unbound prototype methods go through FunctionBuiltIns.
            var fnMember = FunctionBuiltIns.GetMember(unbound, memberName);
            if (fnMember != null) return fnMember;
            return SharpTSUndefined.Instance;
        }
        if (obj is SharpTSFunctionGlobal fnGlobal)
        {
            return fnGlobal.GetMember(memberName) ?? SharpTSUndefined.Instance;
        }
        if (obj is SharpTSFunctionPrototype fnProto)
        {
            return fnProto.GetMember(memberName) ?? SharpTSUndefined.Instance;
        }
        if (obj is SharpTSFunctionProtoToString fnToStr)
        {
            var fnMember = FunctionBuiltIns.GetMember(fnToStr, memberName);
            if (fnMember != null) return fnMember;
            return SharpTSUndefined.Instance;
        }
        if (obj is SharpTSObjectUnboundMethod objUnbound)
        {
            var fnMember = FunctionBuiltIns.GetMember(objUnbound, memberName);
            if (fnMember != null) return fnMember;
            return SharpTSUndefined.Instance;
        }
        // Built-in constructor passed through a variable (e.g. `var D = Date; D.now()`).
        // Resolve static methods via the constructor's own GetMember.
        if (obj is SharpTSBuiltInConstructor ctor)
        {
            return ctor.GetMember(memberName) ?? SharpTSUndefined.Instance;
        }

        // Handle plain Dictionary<string, object?> objects (e.g., segment items from Intl.Segments)
        if (obj is IDictionary<string, object?> dict)
        {
            return dict.TryGetValue(memberName, out var val) ? val : SharpTSUndefined.Instance;
        }

        // Handle objects that implement ISharpTSPropertyAccessor (e.g., SharpTSTemplateStringsArray)
        // Only return if the accessor has this property, otherwise fall through to built-ins
        if (obj is ISharpTSPropertyAccessor accessor && accessor.HasProperty(memberName))
        {
            return accessor.GetProperty(memberName);
        }

        // Handle named properties on arrays (added via Object.defineProperty)
        if (obj is SharpTSArray array && array.HasNamedProperty(memberName))
        {
            return array.GetNamedProperty(memberName);
        }

        // Handle built-in instance members: strings, arrays, Math, Promise
        if (obj != null)
        {
            // Single registry lookup - TryGetInstanceMember returns both member and whether type is known
            var (member, isBuiltInType) = BuiltInRegistry.Instance.TryGetInstanceMember(obj, memberName);
            if (member != null)
            {
                // Bind methods to their receiver, return properties directly
                if (member is BuiltInMethod m) return m.Bind(obj);
                if (member is BuiltInAsyncMethod am) return am.Bind(obj);
                return member;
            }

            // If we have a built-in type but didn't find the member, return undefined
            // (JavaScript semantics: accessing a non-existent property returns undefined)
            if (isBuiltInType)
            {
                return SharpTSUndefined.Instance;
            }
        }

        throw new InterpreterException("Only instances and objects have properties.");
    }

    /// <summary>
    /// Gets a display name for a runtime object type.
    /// </summary>
    private static string GetRuntimeTypeName(object obj) => obj switch
    {
        string => "string",
        SharpTSArray => "array",
        SharpTSMath => "Math",
        SharpTSMap => "Map",
        SharpTSSet => "Set",
        SharpTSDate => "Date",
        SharpTSRegExp => "RegExp",
        SharpTSError => "Error",
        SharpTSInstance inst when inst.GetClass() is SharpTSErrorClass => inst.GetRawField("name")?.ToString() ?? "Error",
        SharpTSPromise => "Promise",
        _ => obj.GetType().Name
    };

    /// <summary>
    /// Evaluates a property assignment expression (dot notation with assignment).
    /// </summary>
    /// <param name="set">The property assignment expression AST node.</param>
    /// <returns>The assigned value.</returns>
    /// <remarks>
    /// Supports static property assignment on classes, instance field assignment,
    /// and simple object property assignment.
    /// </remarks>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/2/objects.html">TypeScript Object Types</seealso>
    private RuntimeValue EvaluateSet(Expr.Set set)
    {
        object? obj = Evaluate(set.Object);
        object? value = Evaluate(set.Value);
        return EvaluateSetOnObjectRV(set, obj, value);
    }

    /// <summary>
    /// Core property assignment logic, shared between sync and async evaluation.
    /// Returns RuntimeValue directly to avoid boxing.
    /// </summary>
    private RuntimeValue EvaluateSetOnObjectRV(Expr.Set set, object? obj, object? value)
    {
        return RuntimeValue.FromBoxed(EvaluateSetOnObject(set, obj, value));
    }

    /// <summary>
    /// Core property assignment logic, shared between sync and async evaluation.
    /// Uses TypeCategoryResolver for fast dispatch on common types.
    /// </summary>
    private object? EvaluateSetOnObject(Expr.Set set, object? obj, object? value)
    {
        // Proxy interception - must be before any other dispatch
        if (obj is SharpTSProxy proxy)
        {
            proxy.TrapSet(set.Name.Lexeme, value, this);
            return value;
        }

        // JS functions are objects — allow property assignment on them.
        if (obj is SharpTSFunction userFn)
        {
            // Accessor set path (Object.defineProperty setter).
            if (userFn.TryGetAccessor(set.Name.Lexeme, out _, out var setter) && setter != null)
            {
                setter.Call(this, [value]);
                return value;
            }
            userFn.SetProperty(set.Name.Lexeme, value);
            return value;
        }
        if (obj is SharpTSArrowFunction arrowFn)
        {
            if (arrowFn.TryGetAccessor(set.Name.Lexeme, out _, out var arrowSetter) && arrowSetter != null)
            {
                arrowSetter.Call(this, [value]);
                return value;
            }
            arrowFn.SetProperty(set.Name.Lexeme, value);
            return value;
        }
        if (obj is SharpTSAsyncFunction asyncFn)
        {
            asyncFn.SetProperty(set.Name.Lexeme, value);
            return value;
        }
        if (obj is SharpTSAsyncArrowFunction asyncArrowFn)
        {
            asyncArrowFn.SetProperty(set.Name.Lexeme, value);
            return value;
        }

        var category = TypeCategoryResolver.ClassifyRuntime(obj);
        string memberName = set.Name.Lexeme;
        bool strictMode = _environment.IsStrictMode;

        switch (category)
        {
            case TypeCategory.External when obj is DotNetInstance dotNetInstance:
                dotNetInstance.SetMember(memberName, value, this);
                return value;

            case TypeCategory.External when obj is DotNetClass dotNetClass:
                dotNetClass.SetStaticMember(memberName, value, this);
                return value;

            case TypeCategory.Class when obj is SharpTSClass klass:
                if (klass.HasStaticAutoAccessor(memberName))
                {
                    klass.SetStaticAutoAccessorValue(memberName, value);
                    return value;
                }
                var staticSetterClass = klass.FindStaticSetter(memberName);
                if (staticSetterClass != null)
                {
                    staticSetterClass.BindStatic(klass).Call(this, [value]);
                    return value;
                }
                klass.SetStaticProperty(memberName, value);
                return value;

            case TypeCategory.Instance when obj is SharpTSInstance instance:
                instance.SetInterpreter(this);
                if (strictMode)
                    instance.SetStrict(set.Name, value, strictMode);
                else
                    instance.Set(set.Name, value);
                return value;

            case TypeCategory.Record:
                return EvaluateSetOnRecord(set, obj!, memberName, value, strictMode);

            case TypeCategory.RegExp when obj is SharpTSRegExp regex:
                if (memberName == "lastIndex")
                {
                    regex.LastIndex = (int)(double)value!;
                    return value;
                }
                // JS: RegExp instances are objects; allow arbitrary property assignment
                // (minimatch stores `_src`/`_glob` this way).
                regex.SetProperty(memberName, value);
                return value;

            case TypeCategory.Error when obj is SharpTSError error:
                if (ErrorBuiltIns.SetMember(error, memberName, value))
                    return value;
                throw new InterpreterException($"Cannot set property '{memberName}' on Error.");

            case TypeCategory.Array when obj is SharpTSArray array:
                if (memberName == "length")
                {
                    // ECMA-262: `a.length = N` truncates (if N < length) or extends
                    // with holes (if N > length). SharpTSArray.SetLength handles both
                    // paths and transitions to sparse storage for large extensions.
                    if (value is double ld)
                    {
                        if (ld < 0 || ld > (double)uint.MaxValue || Math.Floor(ld) != ld)
                            throw new InterpreterException("RangeError: Invalid array length.");
                        array.SetLength((long)ld);
                        return value;
                    }
                    throw new InterpreterException("RangeError: Invalid array length.");
                }
                array.SetNamedProperty(memberName, value);
                return value;

            default:
                return EvaluateSetFallback(obj, memberName, value);
        }
    }

    /// <summary>
    /// Handles property assignment on Record-category types (SharpTSObject, HttpResponse,
    /// NetServer, TlsServer).
    /// </summary>
    private object? EvaluateSetOnRecord(Expr.Set set, object obj, string memberName, object? value, bool strictMode)
    {
        if (obj is SharpTSObject simpleObj)
        {
            var setter = simpleObj.GetSetter(memberName);
            if (setter != null)
            {
                var boundSetter = BindAccessorToObject(setter, simpleObj);
                boundSetter.Call(this, [value]);
                return value;
            }

            if (simpleObj.HasGetter(memberName))
            {
                if (strictMode)
                    throw new InterpreterException($"Cannot set property '{memberName}' which has only a getter.");
                return value;
            }

            if (strictMode)
                simpleObj.SetPropertyStrict(memberName, value, strictMode);
            else
                simpleObj.SetProperty(memberName, value);
            return value;
        }

        if (obj is SharpTSHttpResponse httpRes)
        {
            httpRes.SetMember(memberName, value);
            return value;
        }

        if (obj is SharpTSNetServer netServer)
        {
            netServer.SetMember(memberName, value);
            return value;
        }

        if (obj is SharpTSTlsServer tlsServer)
        {
            tlsServer.SetMember(memberName, value);
            return value;
        }

        throw new InterpreterException($"Only instances and objects have fields. Cannot set '{memberName}' on {obj?.GetType().Name ?? "null"}.");
    }

    /// <summary>
    /// Fallback for property assignment on types without TypeCategory dispatch
    /// (GlobalThis, Agent, AbortSignal).
    /// </summary>
    private static object? EvaluateSetFallback(object? obj, string memberName, object? value)
    {
        if (obj is SharpTSGlobalThis globalThis)
        {
            globalThis.SetProperty(memberName, value);
            return value;
        }

        if (obj is SharpTSAgent agent)
        {
            agent.SetMember(memberName, value);
            return value;
        }

        if (obj is SharpTSAbortSignal signal)
        {
            if (memberName == "onabort")
            {
                signal.OnAbort = value;
                return value;
            }
            throw new InterpreterException($"Cannot set property '{memberName}' on AbortSignal.");
        }

        if (obj is SharpTSBroadcastChannel bc)
        {
            if (bc.SetMember(memberName, value))
                return value;
            throw new InterpreterException($"Cannot set property '{memberName}' on BroadcastChannel.");
        }

        throw new InterpreterException($"Only instances and objects have fields. Cannot set '{memberName}' on {obj?.GetType().Name ?? "null"}.");
    }

    /// <summary>
    /// Evaluates a variable assignment expression.
    /// </summary>
    /// <param name="assign">The assignment expression AST node.</param>
    /// <returns>The assigned value.</returns>
    /// <remarks>
    /// Evaluates the right-hand side value and updates the variable
    /// in the current <see cref="RuntimeEnvironment"/>.
    /// </remarks>
    /// <seealso href="https://www.typescriptlang.org/docs/handbook/variable-declarations.html">TypeScript Variable Declarations</seealso>
    private RuntimeValue EvaluateAssign(Expr.Assign assign)
    {
        RuntimeValue value = EvaluateRV(assign.Value);

        if (_locals.TryGetValue(assign, out int distance))
        {
            _environment.AssignAt(distance, assign.Name, value);
        }
        else
        {
            _environment.Assign(assign.Name, value);
        }

        return value;
    }

    #region ES2022 Private Class Elements

    /// <summary>
    /// Evaluates a private field access expression (obj.#field).
    /// </summary>
    private RuntimeValue EvaluateGetPrivate(Expr.GetPrivate expr)
    {
        object? obj = Evaluate(expr.Object);
        string fieldName = expr.Name.Lexeme;

        // Handle static private field access on class
        if (obj is SharpTSClass klass)
        {
            // For static private fields, the class being accessed IS the declaring class
            // The type checker already verified we're inside this class
            if (klass.HasStaticPrivateField(fieldName))
            {
                return klass.GetStaticPrivateFieldRV(fieldName);
            }

            throw new InterpreterException($"Static private field '{fieldName}' does not exist on class '{klass.Name}'.");
        }

        // Instance private field access
        if (obj is SharpTSInstance instance)
        {
            // For instance private fields, use the instance's class as the declaring class
            // The type checker already verified brand checking
            var declaringClass = instance.RuntimeClass;
            return declaringClass.GetPrivateFieldRV(instance, fieldName);
        }

        throw new InterpreterException($"Cannot read private field '{fieldName}' from non-class value.");
    }

    /// <summary>
    /// Evaluates a private field assignment expression (obj.#field = value).
    /// </summary>
    private RuntimeValue EvaluateSetPrivate(Expr.SetPrivate expr)
    {
        object? obj = Evaluate(expr.Object);
        RuntimeValue value = EvaluateRV(expr.Value);
        string fieldName = expr.Name.Lexeme;

        // Handle static private field assignment on class
        if (obj is SharpTSClass klass)
        {
            // For static private fields, the class being accessed IS the declaring class
            // The type checker already verified we're inside this class
            if (klass.HasStaticPrivateField(fieldName))
            {
                klass.SetStaticPrivateField(fieldName, value.ToObject());
                return value;
            }

            throw new InterpreterException($"Static private field '{fieldName}' does not exist on class '{klass.Name}'.");
        }

        // Instance private field assignment
        if (obj is SharpTSInstance instance)
        {
            // For instance private fields, use the instance's class as the declaring class
            // The type checker already verified brand checking
            var declaringClass = instance.RuntimeClass;
            declaringClass.SetPrivateField(instance, fieldName, value.ToObject());
            return value;
        }

        throw new InterpreterException($"Cannot write private field '{fieldName}' to non-class value.");
    }

    /// <summary>
    /// Evaluates a private method call expression (obj.#method(...)).
    /// </summary>
    private RuntimeValue EvaluateCallPrivate(Expr.CallPrivate expr)
    {
        object? obj = Evaluate(expr.Object);
        string methodName = expr.Name.Lexeme;

        // Evaluate arguments
        List<object?> arguments = [];
        foreach (var arg in expr.Arguments)
        {
            arguments.Add(Evaluate(arg));
        }

        // Handle static private method call on class
        if (obj is SharpTSClass klass)
        {
            // For static private methods, the class being accessed IS the declaring class
            // The type checker already verified we're inside this class
            var method = klass.GetStaticPrivateMethod(methodName);
            if (method == null)
            {
                throw new InterpreterException($"Static private method '{methodName}' does not exist on class '{klass.Name}'.");
            }

            return RuntimeValue.FromBoxed(method.Call(this, arguments));
        }

        // Instance private method call
        if (obj is SharpTSInstance instance)
        {
            // For instance private methods, use the instance's class as the declaring class
            // The type checker already verified brand checking
            var declaringClass = instance.RuntimeClass;
            var method = declaringClass.GetPrivateMethod(methodName);
            if (method == null)
            {
                throw new InterpreterException($"Private method '{methodName}' does not exist on class '{declaringClass.Name}'.");
            }

            // Bind method to instance
            return RuntimeValue.FromBoxed(SharpTSClass.BindMethod(method, instance).Call(this, arguments));
        }

        throw new InterpreterException($"Cannot call private method '{methodName}' on non-class value.");
    }

    #endregion
}
