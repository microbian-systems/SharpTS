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
            "isArray" => new BuiltInMethod("isArray", 1, (_, _, args) =>
            {
                return args[0] is SharpTSArray;
            }),
            "from" => new BuiltInMethod("from", 1, 2, (interpreter, _, args) =>
            {
                // ECMA-262 23.1.2.1: Array.from(null) and Array.from(undefined) throw TypeError
                // (via the GetMethod(@@iterator) → Get → ToObject(items) chain).
                if (args[0] is null or SharpTSUndefined)
                    throw new ThrowException(new SharpTSTypeError(
                        "Cannot convert undefined or null to object"));
                var iterable = args[0]!;

                // ECMA-262 23.1.2.1 step 3a: if mapfn is provided and not undefined,
                // it must be callable. `null` is NOT a stand-in for "no mapfn" — it
                // explicitly throws TypeError per spec (only `undefined` short-circuits).
                ISharpTSCallable? mapFn = null;
                if (args.Count > 1 && args[1] is not SharpTSUndefined)
                {
                    if (args[1] is not ISharpTSCallable mapFnCallable)
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
                        result.Add(mapFn.CallBoxed(interpreter, callbackArgs));
                    }
                    return new SharpTSArray(result);
                }

                return new SharpTSArray(elements);
            }),
            "of" => new BuiltInMethod("of", 0, int.MaxValue, (_, _, args) =>
            {
                // Array.of() creates an array from all arguments
                return new SharpTSArray(args.ToList());
            }),
            _ => null
        };
    }
}
