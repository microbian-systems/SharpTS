using SharpTS.Compilation;
using SharpTS.Execution;
using SharpTS.Runtime;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

public static class ArrayBuiltIns
{
    private static readonly BuiltInTypeMemberLookup<SharpTSArray> _lookup =
        BuiltInTypeBuilder<SharpTSArray>.ForInstanceType()
            .Property("length", arr => (double)arr.Elements.Count)
            .MethodV2("push", 1, int.MaxValue, PushV2)
            .MethodV2("pop", 0, PopV2)
            .MethodV2("shift", 0, ShiftV2)
            .MethodV2("unshift", 1, UnshiftV2)
            .MethodV2("slice", 0, 2, SliceV2)
            .MethodV2("map", 1, MapV2)
            .MethodV2("filter", 1, FilterV2)
            .MethodV2("forEach", 1, ForEachV2)
            .MethodV2("find", 1, FindV2)
            .MethodV2("findIndex", 1, FindIndexV2)
            .MethodV2("some", 1, SomeV2)
            .MethodV2("every", 1, EveryV2)
            .MethodV2("reduce", 1, 2, ReduceV2)
            .MethodV2("reduceRight", 1, 2, ReduceRightV2)
            .MethodV2("includes", 1, IncludesV2)
            .MethodV2("indexOf", 1, IndexOfV2)
            .MethodV2("join", 0, 1, JoinV2)
            .MethodV2("concat", 1, ConcatV2)
            .MethodV2("reverse", 0, ReverseV2)
            .Method("flat", 0, 1, Flat)
            .Method("flatMap", 1, FlatMap)
            .Method("sort", 0, 1, Sort)
            .Method("toSorted", 0, 1, ToSorted)
            .Method("splice", 0, int.MaxValue, Splice)
            .Method("toSpliced", 0, int.MaxValue, ToSpliced)
            .MethodV2("findLast", 1, FindLastV2)
            .MethodV2("findLastIndex", 1, FindLastIndexV2)
            .MethodV2("toReversed", 0, ToReversedV2)
            .MethodV2("with", 2, WithV2)
            .MethodV2("at", 1, AtV2)
            .MethodV2("fill", 1, 3, FillV2)
            .MethodV2("copyWithin", 1, 3, CopyWithinV2)
            .MethodV2("entries", 0, (_, arr, _) => RuntimeValue.FromObject(new SharpTSIterator(EnumerateEntries(arr))))
            .MethodV2("keys", 0, (_, arr, _) => RuntimeValue.FromObject(new SharpTSIterator(EnumerateKeys(arr))))
            .MethodV2("values", 0, (_, arr, _) => RuntimeValue.FromObject(new SharpTSIterator(EnumerateValues(arr))))
            .Build();

    public static object? GetMember(SharpTSArray receiver, string name)
        => _lookup.GetMember(receiver, name);

    private static object? Push(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        // Frozen/sealed/non-extensible arrays cannot have elements added
        if (arr.IsFrozen || arr.IsSealed || !arr.IsExtensible)
        {
            return (double)arr.Elements.Count;
        }
        // Add all arguments (variadic push)
        foreach (var arg in args)
        {
            arr.Elements.Add(arg);
        }
        return (double)arr.Elements.Count;
    }

    private static object? Pop(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        // Frozen/sealed arrays cannot have elements removed
        if (arr.IsFrozen || arr.IsSealed)
        {
            return null;
        }
        if (arr.Elements.Count == 0) return null;
        return arr.Elements.RemoveLast();  // O(1) with Deque
    }

    private static object? Shift(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        // Frozen/sealed arrays cannot have elements removed
        if (arr.IsFrozen || arr.IsSealed)
        {
            return null;
        }
        if (arr.Elements.Count == 0) return null;
        return arr.Elements.RemoveFirst();  // O(1) with Deque
    }

    private static object? Unshift(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        // Frozen/sealed/non-extensible arrays cannot have elements added
        if (arr.IsFrozen || arr.IsSealed || !arr.IsExtensible)
        {
            return (double)arr.Elements.Count;
        }
        arr.Elements.AddFirst(args[0]);  // O(1) with Deque
        return (double)arr.Elements.Count;
    }

    private static object? Slice(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        var start = args.Count > 0 ? (int)(double)args[0]! : 0;
        var end = args.Count > 1 ? (int)(double)args[1]! : arr.Elements.Count;

        // Handle negative indices
        if (start < 0) start = Math.Max(0, arr.Elements.Count + start);
        if (end < 0) end = Math.Max(0, arr.Elements.Count + end);
        if (start > arr.Elements.Count) start = arr.Elements.Count;
        if (end > arr.Elements.Count) end = arr.Elements.Count;
        if (end <= start) return new SharpTSArray([]);

        var sliced = arr.Elements.GetRange(start, end - start);
        return new SharpTSArray(new Deque<object?>(sliced));
    }

    private static object? Map(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        using var iter = CallbackIterator.Create(args, arr, "map");
        List<object?> result = [];
        for (int i = 0; i < arr.Elements.Count; i++)
            result.Add(iter.Invoke(interp, arr.Elements[i], i));
        return new SharpTSArray(result);
    }

    private static object? Filter(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        using var iter = CallbackIterator.Create(args, arr, "filter");
        List<object?> result = [];
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                result.Add(arr.Elements[i]);
        }
        return new SharpTSArray(result);
    }

    private static object? ForEach(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        using var iter = CallbackIterator.Create(args, arr, "forEach");
        for (int i = 0; i < arr.Elements.Count; i++)
            iter.InvokeRV(interp, arr.Elements[i], i);
        return null;
    }

    private static object? Find(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        using var iter = CallbackIterator.Create(args, arr, "find");
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                return arr.Elements[i];
        }
        return null;
    }

    private static object? FindIndex(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        using var iter = CallbackIterator.Create(args, arr, "findIndex");
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                return (double)i;
        }
        return -1.0;
    }

    private static object? Some(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        using var iter = CallbackIterator.Create(args, arr, "some");
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                return true;
        }
        return false;
    }

    private static object? Every(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        using var iter = CallbackIterator.Create(args, arr, "every");
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (!iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                return false;
        }
        return true;
    }

    private static object? Reduce(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        var callback = args[0] as ISharpTSCallable
            ?? throw new Exception("Runtime Error: reduce requires a function argument.");

        int startIndex = 0;
        object? accumulator;

        if (args.Count > 1)
        {
            accumulator = args[1];
        }
        else
        {
            if (arr.Elements.Count == 0)
            {
                throw new Exception("Runtime Error: reduce of empty array with no initial value.");
            }
            accumulator = arr.Elements[0];
            startIndex = 1;
        }

        var callbackArgs = ArgumentListPool.Rent();
        try
        {
            callbackArgs.Add(null);
            callbackArgs.Add(null);
            callbackArgs.Add(null);
            callbackArgs.Add(arr);
            for (int i = startIndex; i < arr.Elements.Count; i++)
            {
                callbackArgs[0] = accumulator;
                callbackArgs[1] = arr.Elements[i];
                callbackArgs[2] = (double)i;
                accumulator = callback.Call(interp, callbackArgs);
            }
            return accumulator;
        }
        finally
        {
            ArgumentListPool.Return(callbackArgs);
        }
    }

    private static object? ReduceRight(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        var callback = args[0] as ISharpTSCallable
            ?? throw new Exception("Runtime Error: reduceRight requires a function argument.");

        int startIndex = arr.Elements.Count - 1;
        object? accumulator;

        if (args.Count > 1)
        {
            accumulator = args[1];
        }
        else
        {
            if (arr.Elements.Count == 0)
            {
                throw new Exception("Runtime Error: reduceRight of empty array with no initial value.");
            }
            accumulator = arr.Elements[arr.Elements.Count - 1];
            startIndex = arr.Elements.Count - 2;
        }

        var callbackArgs = ArgumentListPool.Rent();
        try
        {
            callbackArgs.Add(null);
            callbackArgs.Add(null);
            callbackArgs.Add(null);
            callbackArgs.Add(arr);
            for (int i = startIndex; i >= 0; i--)
            {
                callbackArgs[0] = accumulator;
                callbackArgs[1] = arr.Elements[i];
                callbackArgs[2] = (double)i;
                accumulator = callback.Call(interp, callbackArgs);
            }
            return accumulator;
        }
        finally
        {
            ArgumentListPool.Return(callbackArgs);
        }
    }

    private static object? Includes(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        var searchElement = args[0];

        foreach (var element in arr.Elements)
        {
            if (IsEqual(element, searchElement))
            {
                return true;
            }
        }
        return false;
    }

    private static object? IndexOf(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        var searchElement = args[0];

        for (int idx = 0; idx < arr.Elements.Count; idx++)
        {
            if (IsEqual(arr.Elements[idx], searchElement))
            {
                return (double)idx;
            }
        }
        return -1.0;
    }

    private static object? Join(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        var separator = args.Count > 0 ? Stringify(args[0]) : ",";

        // Pass enumerable directly to string.Join - no intermediate collection needed
        return string.Join(separator, arr.Elements.Select(Stringify));
    }

    // concat and reverse converted to V2 — see ConcatV2 and ReverseV2

    private static object? Flat(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        // Default depth is 1, handle Infinity for complete flatten
        var depth = args.Count > 0 && args[0] is double d
            ? (double.IsPositiveInfinity(d) ? int.MaxValue : (int)d)
            : 1;

        var result = new List<object?>();
        FlattenRecursive(arr.Elements, result, depth);
        return new SharpTSArray(result);
    }

    private static void FlattenRecursive(IEnumerable<object?> source, List<object?> result, int depth)
    {
        foreach (var item in source)
        {
            if (depth > 0 && item is SharpTSArray nestedArray)
            {
                FlattenRecursive(nestedArray.Elements, result, depth - 1);
            }
            else
            {
                result.Add(item);
            }
        }
    }

    private static object? FlatMap(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        using var iter = CallbackIterator.Create(args, arr, "flatMap");
        var result = new List<object?>();
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            var callResult = iter.InvokeRV(interp, arr.Elements[i], i).ToObject();
            // flatMap flattens by 1 level only
            if (callResult is SharpTSArray mappedArray)
                result.AddRange(mappedArray.Elements);
            else
                result.Add(callResult);
        }
        return new SharpTSArray(result);
    }

    private static object? Sort(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        // Frozen arrays cannot be modified; silent fail (matches reverse behavior)
        if (arr.IsFrozen) return arr;

        ISharpTSCallable? compareFn = args.Count > 0 ? args[0] as ISharpTSCallable : null;

        // Partition undefined to end (JS behavior)
        var defined = new List<(object? Element, int Index)>();
        int undefinedCount = 0;
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (IsUndefined(arr.Elements[i]))
                undefinedCount++;
            else
                defined.Add((arr.Elements[i], i));
        }

        var sorted = StableSort(defined, compareFn, interp);

        arr.Elements.Clear();
        arr.Elements.AddRange(sorted);
        for (int i = 0; i < undefinedCount; i++)
            arr.Elements.Add(SharpTSUndefined.Instance);

        return arr;
    }

    private static object? ToSorted(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        ISharpTSCallable? compareFn = args.Count > 0 ? args[0] as ISharpTSCallable : null;

        // Same logic but returns NEW array
        var defined = new List<(object? Element, int Index)>();
        int undefinedCount = 0;
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (IsUndefined(arr.Elements[i]))
                undefinedCount++;
            else
                defined.Add((arr.Elements[i], i));
        }

        var sorted = StableSort(defined, compareFn, interp);
        for (int i = 0; i < undefinedCount; i++)
            sorted.Add(SharpTSUndefined.Instance);

        return new SharpTSArray(sorted);
    }

    /// <summary>
    /// Performs a stable sort using LINQ OrderBy (which is stable).
    /// </summary>
    private static List<object?> StableSort(
        List<(object? Element, int Index)> items,
        ISharpTSCallable? compareFn,
        Interpreter interp)
    {
        if (items.Count <= 1)
            return items.Select(x => x.Element).ToList();

        IEnumerable<(object? Element, int Index)> sorted;
        if (compareFn != null)
        {
            sorted = items.OrderBy(x => x, new CompareFnComparer(compareFn, interp));
        }
        else
        {
            // Default lexicographic sort (JavaScript behavior: numbers sorted as strings)
            sorted = items.OrderBy(x => Stringify(x.Element), StringComparer.Ordinal)
                          .ThenBy(x => x.Index);
        }

        return sorted.Select(x => x.Element).ToList();
    }

    /// <summary>
    /// Comparer that uses a user-provided comparison function.
    /// </summary>
    private class CompareFnComparer : IComparer<(object? Element, int Index)>
    {
        private readonly ISharpTSCallable _fn;
        private readonly Interpreter _interp;
        private readonly List<object?> _compareArgs = new(2) { null, null };

        public CompareFnComparer(ISharpTSCallable fn, Interpreter interp)
            => (_fn, _interp) = (fn, interp);

        public int Compare((object? Element, int Index) x, (object? Element, int Index) y)
        {
            _compareArgs[0] = x.Element;
            _compareArgs[1] = y.Element;
            var result = _fn.Call(_interp, _compareArgs);
            if (result is double d && !double.IsNaN(d) && d != 0)
                return d < 0 ? -1 : 1;
            // Stability tie-breaker: preserve original order
            return x.Index.CompareTo(y.Index);
        }
    }

    /// <summary>
    /// Implements JavaScript's ToIntegerOrInfinity algorithm (ECMA-262 7.1.5).
    /// Converts a value to an integer, handling NaN, Infinity, and null.
    /// </summary>
    private static int ToIntegerOrInfinity(object? value, int defaultValue)
    {
        if (value == null) return defaultValue;
        if (value is int i) return i;
        if (value is double d)
        {
            if (double.IsNaN(d)) return 0;
            if (double.IsPositiveInfinity(d)) return int.MaxValue;
            if (double.IsNegativeInfinity(d)) return int.MinValue;
            return (int)Math.Truncate(d);
        }
        return defaultValue;
    }

    private static object? Splice(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        int len = arr.Elements.Count;

        // Frozen/sealed arrays throw TypeError
        if (arr.IsFrozen || arr.IsSealed)
            throw new Exception("TypeError: Cannot modify a frozen or sealed array");

        // If no arguments, return empty array (no elements deleted or inserted)
        if (args.Count == 0)
            return new SharpTSArray([]);

        // Parse start with negative handling (RelativeIndex to ActualIndex)
        int relStart = ToIntegerOrInfinity(args[0], 0);
        int actualStart = relStart < 0 ? Math.Max(len + relStart, 0) : Math.Min(relStart, len);

        // Parse deleteCount
        int actualDeleteCount;
        if (args.Count == 1)
        {
            // No deleteCount argument = delete to end
            actualDeleteCount = len - actualStart;
        }
        else
        {
            int dc = ToIntegerOrInfinity(args[1], 0);
            actualDeleteCount = Math.Max(0, Math.Min(dc, len - actualStart));
        }

        // Collect deleted elements directly into a Deque (single allocation)
        var deleted = new Deque<object?>(arr.Elements.GetRange(actualStart, actualDeleteCount));

        // Remove then insert
        arr.Elements.RemoveRange(actualStart, actualDeleteCount);
        if (args.Count > 2)
        {
            // InsertRange accepts IEnumerable - no need to materialize to list
            arr.Elements.InsertRange(actualStart, args.Skip(2));
        }

        return new SharpTSArray(deleted);
    }

    private static object? ToSpliced(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        int len = arr.Elements.Count;

        // toSpliced works on frozen/sealed arrays (creates new array)

        // If no arguments, return a copy of the array
        if (args.Count == 0)
            return new SharpTSArray(new List<object?>(arr.Elements));

        // Parse start with negative handling
        int relStart = ToIntegerOrInfinity(args[0], 0);
        int actualStart = relStart < 0 ? Math.Max(len + relStart, 0) : Math.Min(relStart, len);

        // Parse skipCount (deleteCount equivalent)
        int actualSkipCount;
        if (args.Count == 1)
        {
            // No skipCount argument = skip to end
            actualSkipCount = len - actualStart;
        }
        else
        {
            int sc = ToIntegerOrInfinity(args[1], 0);
            actualSkipCount = Math.Max(0, Math.Min(sc, len - actualStart));
        }

        // Build new array: before + items + after
        // Pre-size to avoid reallocations: before(actualStart) + inserted(args.Count-2) + after(len - actualStart - actualSkipCount)
        int insertCount = args.Count > 2 ? args.Count - 2 : 0;
        int afterCount = len - actualStart - actualSkipCount;
        var result = new List<object?>(actualStart + insertCount + afterCount);

        // Add elements before splice point
        for (int i = 0; i < actualStart; i++)
            result.Add(arr.Elements[i]);

        // Add inserted elements
        for (int i = 2; i < args.Count; i++)
            result.Add(args[i]);

        // Add elements after splice point
        for (int i = actualStart + actualSkipCount; i < len; i++)
            result.Add(arr.Elements[i]);

        return new SharpTSArray(result);
    }

    private static object? FindLast(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        using var iter = CallbackIterator.Create(args, arr, "findLast");
        for (int i = arr.Elements.Count - 1; i >= 0; i--)
        {
            if (iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                return arr.Elements[i];
        }
        return null;
    }

    private static object? FindLastIndex(Interpreter interp, SharpTSArray arr, List<object?> args)
    {
        using var iter = CallbackIterator.Create(args, arr, "findLastIndex");
        for (int i = arr.Elements.Count - 1; i >= 0; i--)
        {
            if (iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                return (double)i;
        }
        return -1.0;
    }

    // toReversed, with, at, fill, copyWithin converted to V2 — see V2 region

    #region V2 Implementations (RuntimeValue — no boxing)

    private static RuntimeValue PushV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        if (arr.IsFrozen || arr.IsSealed || !arr.IsExtensible)
            return RuntimeValue.FromNumber(arr.Elements.Count);
        foreach (var arg in args)
            arr.Elements.Add(arg.ToObject());
        return RuntimeValue.FromNumber(arr.Elements.Count);
    }

    private static RuntimeValue PopV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        if (arr.IsFrozen || arr.IsSealed || arr.Elements.Count == 0)
            return RuntimeValue.Null;
        return RuntimeValue.FromBoxed(arr.Elements.RemoveLast());
    }

    private static RuntimeValue ShiftV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        if (arr.IsFrozen || arr.IsSealed || arr.Elements.Count == 0)
            return RuntimeValue.Null;
        return RuntimeValue.FromBoxed(arr.Elements.RemoveFirst());
    }

    private static RuntimeValue UnshiftV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        if (arr.IsFrozen || arr.IsSealed || !arr.IsExtensible)
            return RuntimeValue.FromNumber(arr.Elements.Count);
        arr.Elements.AddFirst(args[0].ToObject());
        return RuntimeValue.FromNumber(arr.Elements.Count);
    }

    private static RuntimeValue SliceV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        var start = args.Length > 0 ? (int)args[0].AsNumber() : 0;
        var end = args.Length > 1 ? (int)args[1].AsNumber() : arr.Elements.Count;
        if (start < 0) start = Math.Max(0, arr.Elements.Count + start);
        if (end < 0) end = Math.Max(0, arr.Elements.Count + end);
        if (start > arr.Elements.Count) start = arr.Elements.Count;
        if (end > arr.Elements.Count) end = arr.Elements.Count;
        if (end <= start) return RuntimeValue.FromObject(new SharpTSArray([]));
        var sliced = arr.Elements.GetRange(start, end - start);
        return RuntimeValue.FromObject(new SharpTSArray(new Deque<object?>(sliced)));
    }

    private static RuntimeValue IncludesV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        var searchElement = args[0].ToObject();
        foreach (var element in arr.Elements)
        {
            if (IsEqual(element, searchElement))
                return RuntimeValue.True;
        }
        return RuntimeValue.False;
    }

    private static RuntimeValue IndexOfV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        var searchElement = args[0].ToObject();
        for (int idx = 0; idx < arr.Elements.Count; idx++)
        {
            if (IsEqual(arr.Elements[idx], searchElement))
                return RuntimeValue.FromNumber(idx);
        }
        return RuntimeValue.FromNumber(-1);
    }

    private static RuntimeValue JoinV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        var separator = args.Length > 0 ? Stringify(args[0].ToObject()) : ",";
        return RuntimeValue.FromString(string.Join(separator, arr.Elements.Select(Stringify)));
    }

    private static RuntimeValue ConcatV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        var arg = args[0].ToObject();
        var result = new List<object?>(arr.Elements);
        if (arg is SharpTSArray otherArr)
            result.AddRange(otherArr.Elements);
        else
            result.Add(arg);
        return RuntimeValue.FromObject(new SharpTSArray(result));
    }

    private static RuntimeValue ReverseV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        if (arr.IsFrozen)
            return RuntimeValue.FromObject(arr);
        arr.Elements.Reverse();
        return RuntimeValue.FromObject(arr);
    }

    private static RuntimeValue ToReversedV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        var result = new List<object?>(arr.Elements.Count);
        for (int i = arr.Elements.Count - 1; i >= 0; i--)
            result.Add(arr.Elements[i]);
        return RuntimeValue.FromObject(new SharpTSArray(result));
    }

    private static RuntimeValue WithV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        int len = arr.Elements.Count;
        int index = (int)args[0].AsNumber();
        int actualIndex = index < 0 ? len + index : index;
        if (actualIndex < 0 || actualIndex >= len)
            throw new Exception("RangeError: Invalid index for with()");
        var result = new List<object?>(arr.Elements);
        result[actualIndex] = args[1].ToObject();
        return RuntimeValue.FromObject(new SharpTSArray(result));
    }

    private static RuntimeValue AtV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        int len = arr.Elements.Count;
        int index = (int)args[0].AsNumber();
        int actualIndex = index < 0 ? len + index : index;
        if (actualIndex < 0 || actualIndex >= len)
            return RuntimeValue.Null;
        return RuntimeValue.FromBoxed(arr.Elements[actualIndex]);
    }

    private static RuntimeValue FillV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        if (arr.IsFrozen)
            return RuntimeValue.FromObject(arr);

        int len = arr.Elements.Count;
        if (len == 0) return RuntimeValue.FromObject(arr);

        var value = args.Length > 0 ? args[0].ToObject() : null;

        int relStart = args.Length > 1 ? ToIntegerOrInfinity(args[1].ToObject(), 0) : 0;
        int actualStart = relStart < 0 ? Math.Max(len + relStart, 0) : Math.Min(relStart, len);

        int relEnd = args.Length > 2 ? ToIntegerOrInfinity(args[2].ToObject(), len) : len;
        int actualEnd = relEnd < 0 ? Math.Max(len + relEnd, 0) : Math.Min(relEnd, len);

        for (int i = actualStart; i < actualEnd; i++)
            arr.Elements[i] = value;

        return RuntimeValue.FromObject(arr);
    }

    private static RuntimeValue CopyWithinV2(Interpreter _, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        if (arr.IsFrozen)
            return RuntimeValue.FromObject(arr);

        int len = arr.Elements.Count;
        if (len == 0) return RuntimeValue.FromObject(arr);

        int relTarget = args.Length > 0 ? (int)args[0].AsNumber() : 0;
        int to = relTarget < 0 ? Math.Max(len + relTarget, 0) : Math.Min(relTarget, len);

        int relStart = args.Length > 1 ? (int)args[1].AsNumber() : 0;
        int from = relStart < 0 ? Math.Max(len + relStart, 0) : Math.Min(relStart, len);

        int relEnd = args.Length > 2 ? (int)args[2].AsNumber() : len;
        int final_ = relEnd < 0 ? Math.Max(len + relEnd, 0) : Math.Min(relEnd, len);

        int count = Math.Min(final_ - from, len - to);

        if (count > 0)
        {
            if (from < to && to < from + count)
            {
                for (int i = count - 1; i >= 0; i--)
                    arr.Elements[to + i] = arr.Elements[from + i];
            }
            else
            {
                for (int i = 0; i < count; i++)
                    arr.Elements[to + i] = arr.Elements[from + i];
            }
        }

        return RuntimeValue.FromObject(arr);
    }

    // --- Callback-based V2 methods ---

    private static RuntimeValue MapV2(Interpreter interp, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        using var iter = CallbackIterator.CreateFromRV(args, arr, "map");
        List<object?> result = [];
        for (int i = 0; i < arr.Elements.Count; i++)
            result.Add(iter.Invoke(interp, arr.Elements[i], i));
        return RuntimeValue.FromObject(new SharpTSArray(result));
    }

    private static RuntimeValue FilterV2(Interpreter interp, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        using var iter = CallbackIterator.CreateFromRV(args, arr, "filter");
        List<object?> result = [];
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                result.Add(arr.Elements[i]);
        }
        return RuntimeValue.FromObject(new SharpTSArray(result));
    }

    private static RuntimeValue ForEachV2(Interpreter interp, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        using var iter = CallbackIterator.CreateFromRV(args, arr, "forEach");
        for (int i = 0; i < arr.Elements.Count; i++)
            iter.InvokeRV(interp, arr.Elements[i], i);
        return RuntimeValue.Undefined;
    }

    private static RuntimeValue FindV2(Interpreter interp, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        using var iter = CallbackIterator.CreateFromRV(args, arr, "find");
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                return RuntimeValue.FromBoxed(arr.Elements[i]);
        }
        return RuntimeValue.Null;
    }

    private static RuntimeValue FindIndexV2(Interpreter interp, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        using var iter = CallbackIterator.CreateFromRV(args, arr, "findIndex");
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                return RuntimeValue.FromNumber(i);
        }
        return RuntimeValue.FromNumber(-1);
    }

    private static RuntimeValue SomeV2(Interpreter interp, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        using var iter = CallbackIterator.CreateFromRV(args, arr, "some");
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                return RuntimeValue.True;
        }
        return RuntimeValue.False;
    }

    private static RuntimeValue EveryV2(Interpreter interp, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        using var iter = CallbackIterator.CreateFromRV(args, arr, "every");
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (!iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                return RuntimeValue.False;
        }
        return RuntimeValue.True;
    }

    private static RuntimeValue ReduceV2(Interpreter interp, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        var callback = args[0].ToObject() as ISharpTSCallable
            ?? throw new Exception("Runtime Error: reduce requires a function argument.");

        int startIndex = 0;
        object? accumulator;

        if (args.Length > 1)
        {
            accumulator = args[1].ToObject();
        }
        else
        {
            if (arr.Elements.Count == 0)
                throw new Exception("Runtime Error: reduce of empty array with no initial value.");
            accumulator = arr.Elements[0];
            startIndex = 1;
        }

        var callbackArgs = ArgumentListPool.Rent();
        try
        {
            callbackArgs.Add(null);
            callbackArgs.Add(null);
            callbackArgs.Add(null);
            callbackArgs.Add(arr);
            for (int i = startIndex; i < arr.Elements.Count; i++)
            {
                callbackArgs[0] = accumulator;
                callbackArgs[1] = arr.Elements[i];
                callbackArgs[2] = (double)i;
                accumulator = callback.Call(interp, callbackArgs);
            }
            return RuntimeValue.FromBoxed(accumulator);
        }
        finally
        {
            ArgumentListPool.Return(callbackArgs);
        }
    }

    private static RuntimeValue ReduceRightV2(Interpreter interp, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        var callback = args[0].ToObject() as ISharpTSCallable
            ?? throw new Exception("Runtime Error: reduceRight requires a function argument.");

        int startIndex = arr.Elements.Count - 1;
        object? accumulator;

        if (args.Length > 1)
        {
            accumulator = args[1].ToObject();
        }
        else
        {
            if (arr.Elements.Count == 0)
                throw new Exception("Runtime Error: reduceRight of empty array with no initial value.");
            accumulator = arr.Elements[arr.Elements.Count - 1];
            startIndex = arr.Elements.Count - 2;
        }

        var callbackArgs = ArgumentListPool.Rent();
        try
        {
            callbackArgs.Add(null);
            callbackArgs.Add(null);
            callbackArgs.Add(null);
            callbackArgs.Add(arr);
            for (int i = startIndex; i >= 0; i--)
            {
                callbackArgs[0] = accumulator;
                callbackArgs[1] = arr.Elements[i];
                callbackArgs[2] = (double)i;
                accumulator = callback.Call(interp, callbackArgs);
            }
            return RuntimeValue.FromBoxed(accumulator);
        }
        finally
        {
            ArgumentListPool.Return(callbackArgs);
        }
    }

    private static RuntimeValue FindLastV2(Interpreter interp, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        using var iter = CallbackIterator.CreateFromRV(args, arr, "findLast");
        for (int i = arr.Elements.Count - 1; i >= 0; i--)
        {
            if (iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                return RuntimeValue.FromBoxed(arr.Elements[i]);
        }
        return RuntimeValue.Null;
    }

    private static RuntimeValue FindLastIndexV2(Interpreter interp, SharpTSArray arr, ReadOnlySpan<RuntimeValue> args)
    {
        using var iter = CallbackIterator.CreateFromRV(args, arr, "findLastIndex");
        for (int i = arr.Elements.Count - 1; i >= 0; i--)
        {
            if (iter.InvokeRV(interp, arr.Elements[i], i).IsTruthy())
                return RuntimeValue.FromNumber(i);
        }
        return RuntimeValue.FromNumber(-1);
    }

    #endregion

    private static bool IsUndefined(object? obj)
    {
        return obj is SharpTSUndefined;
    }

    private static bool IsTruthy(object? obj) => RuntimeTypes.IsTruthy(obj);

    private static bool IsEqual(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null) return false;
        return a.Equals(b);
    }

    private static string Stringify(object? obj)
    {
        if (obj == null) return "null";
        if (obj is double d)
        {
            string text = d.ToString();
            if (text.EndsWith(".0"))
            {
                text = text[..^2];
            }
            return text;
        }
        if (obj is bool b) return b ? "true" : "false";
        return obj.ToString() ?? "null";
    }

    #region Iterator Methods

    private static object? Entries(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        return new SharpTSIterator(EnumerateEntries(arr));
    }

    private static object? Keys(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        return new SharpTSIterator(EnumerateKeys(arr));
    }

    private static object? Values(Interpreter _, SharpTSArray arr, List<object?> args)
    {
        return new SharpTSIterator(EnumerateValues(arr));
    }

    private static IEnumerable<object?> EnumerateEntries(SharpTSArray arr)
    {
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            yield return new SharpTSArray([(double)i, arr.Elements[i]]);
        }
    }

    private static IEnumerable<object?> EnumerateKeys(SharpTSArray arr)
    {
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            yield return (double)i;
        }
    }

    private static IEnumerable<object?> EnumerateValues(SharpTSArray arr)
    {
        foreach (var element in arr.Elements)
        {
            yield return element;
        }
    }

    #endregion

    private readonly struct CallbackIterator : IDisposable
    {
        private readonly ISharpTSCallable _callback;
        private readonly ISharpTSCallableV2? _callbackV2;
        private readonly PooledArgumentList _args;
        private readonly RuntimeValue[] _argsV2;

        private CallbackIterator(ISharpTSCallable callback, PooledArgumentList args, RuntimeValue arrRV)
        {
            _callback = callback;
            _callbackV2 = callback as ISharpTSCallableV2;
            _args = args;
            _argsV2 = [default, default, arrRV];
        }

        public static CallbackIterator Create(List<object?> args, SharpTSArray arr, string methodName)
        {
            var callback = args[0] as ISharpTSCallable
                ?? throw new Exception($"Runtime Error: {methodName} requires a function argument.");
            var callbackArgs = ArgumentListPool.Rent();
            callbackArgs.Add(null);
            callbackArgs.Add(null);
            callbackArgs.Add(arr);
            return new CallbackIterator(callback, callbackArgs, RuntimeValue.FromObject(arr));
        }

        public static CallbackIterator CreateFromRV(ReadOnlySpan<RuntimeValue> args, SharpTSArray arr, string methodName)
        {
            var callback = args[0].ToObject() as ISharpTSCallable
                ?? throw new Exception($"Runtime Error: {methodName} requires a function argument.");
            var callbackArgs = ArgumentListPool.Rent();
            callbackArgs.Add(null);
            callbackArgs.Add(null);
            callbackArgs.Add(arr);
            return new CallbackIterator(callback, callbackArgs, RuntimeValue.FromObject(arr));
        }

        public object? Invoke(Interpreter interp, object? element, int index)
        {
            // V2 fast path — avoids boxing element and index
            if (_callbackV2 != null)
            {
                _argsV2[0] = RuntimeValue.FromBoxed(element);
                _argsV2[1] = RuntimeValue.FromNumber(index);
                return _callbackV2.CallV2(interp, _argsV2).ToObject();
            }

            _args[0] = element;
            _args[1] = (double)index;
            return _callback.Call(interp, _args);
        }

        /// <summary>
        /// V2-native invoke — returns RuntimeValue without boxing at return boundary.
        /// </summary>
        public RuntimeValue InvokeRV(Interpreter interp, object? element, int index)
        {
            if (_callbackV2 != null)
            {
                _argsV2[0] = RuntimeValue.FromBoxed(element);
                _argsV2[1] = RuntimeValue.FromNumber(index);
                return _callbackV2.CallV2(interp, _argsV2);
            }

            _args[0] = element;
            _args[1] = (double)index;
            return RuntimeValue.FromBoxed(_callback.Call(interp, _args));
        }

        public void Dispose() => ArgumentListPool.Return(_args);
    }
}
