using SharpTS.Runtime.Exceptions;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Static methods on the Array namespace (e.g., Array.isArray(), Array.from())
/// </summary>
public static class ArrayStaticBuiltIns
{
    public static object? GetStaticMethod(string name)
    {
        return name switch
        {
            "isArray" => BuiltInMethod.CreateV2("isArray", 1, static (_, _, args) =>
            {
                return RuntimeValue.FromBoolean(args[0].ToObject() is SharpTSArray);
            }),
            "from" => BuiltInMethod.CreateV2("from", 1, 2, static (interpreter, _, args) =>
            {
                // ECMA-262 23.1.2.1: Array.from(null) and Array.from(undefined) throw TypeError
                // (via the GetMethod(@@iterator) → Get → ToObject(items) chain).
                if (args[0].IsNullish)
                    throw new ThrowException(new SharpTSTypeError(
                        "Cannot convert undefined or null to object"));
                var iterable = args[0].ToObject()!;

                // ECMA-262 23.1.2.1 step 3a: if mapfn is provided and not undefined,
                // it must be callable. `null` is NOT a stand-in for "no mapfn" — it
                // explicitly throws TypeError per spec (only `undefined` short-circuits).
                ISharpTSCallable? mapFn = null;
                if (args.Length > 1 && !args[1].IsUndefined)
                {
                    if (args[1].ToObject() is not ISharpTSCallable mapFnCallable)
                        throw new ThrowException(new SharpTSTypeError(
                            "Array.from mapFn argument must be a function"));
                    mapFn = mapFnCallable;
                }

                var elements = interpreter.GetIterableElements(iterable).ToList();

                if (mapFn != null)
                {
                    var result = new List<object?>();
                    var callbackArgs = new List<object?>(2) { null, null };
                    for (int i = 0; i < elements.Count; i++)
                    {
                        callbackArgs[0] = elements[i];
                        callbackArgs[1] = (double)i;
                        result.Add(mapFn.Call(interpreter, callbackArgs));
                    }
                    return RuntimeValue.FromObject(new SharpTSArray(result));
                }

                return RuntimeValue.FromObject(new SharpTSArray(elements));
            }),
            "of" => BuiltInMethod.CreateV2("of", 0, int.MaxValue, static (_, _, args) =>
            {
                // Array.of() creates an array from all arguments
                var items = new List<object?>(args.Length);
                foreach (var arg in args)
                    items.Add(arg.ToObject());
                return RuntimeValue.FromObject(new SharpTSArray(items));
            }),
            _ => null
        };
    }
}
