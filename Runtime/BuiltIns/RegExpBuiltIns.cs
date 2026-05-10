using SharpTS.Execution;
using SharpTS.Runtime.Exceptions;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Provides implementations for JavaScript RegExp object members.
/// Includes instance properties (source, flags, global, etc.) and methods (test, exec, toString).
/// </summary>
public static class RegExpBuiltIns
{
    /// <summary>
    /// Gets an instance member (property or method) for a RegExp object.
    /// Pass <paramref name="interpreter"/> when the caller has one available
    /// — it's required to invoke user-installed accessor getters
    /// (Object.defineProperty path, ECMA-262 §22.2). Without it, the legacy
    /// path returns the bound built-in slot.
    /// </summary>
    public static object? GetMember(SharpTSRegExp receiver, string name, Interpreter? interpreter = null)
    {
        // User-installed accessor wins over the built-in slot — ECMA-262
        // declares flags/global/unicode/lastIndex as configurable, so a
        // throwing getter installed via defineProperty MUST fire and
        // propagate. Without an interpreter we can't invoke it; legacy
        // call sites without the parameter fall through to the built-in.
        if (interpreter != null
            && receiver.TryGetAccessor(name, out var userGetter, out _)
            && userGetter != null)
        {
            return userGetter.Call(interpreter, []);
        }

        // User-set properties shadow nothing built-in but are returned when no
        // builtin matches (JS: RegExp instances are ordinary objects).
        var builtIn = name switch
        {
            // ========== Properties ==========
            "source" => (object?)receiver.Source,
            "flags" => receiver.Flags,
            "global" => receiver.Global,
            "ignoreCase" => receiver.IgnoreCase,
            "multiline" => receiver.Multiline,
            "lastIndex" => (double)receiver.LastIndex,

            // ========== Methods ==========
            "test" => new BuiltInMethod("test", 1, (_, recv, args) =>
            {
                var regex = (SharpTSRegExp)recv!;
                var str = args[0]?.ToString() ?? "";
                return regex.Test(str);
            }),

            "exec" => new BuiltInMethod("exec", 1, (_, recv, args) =>
            {
                var regex = (SharpTSRegExp)recv!;
                var str = args[0]?.ToString() ?? "";
                return regex.Exec(str);
            }),

            "toString" => new BuiltInMethod("toString", 0, (_, recv, _) =>
                ((SharpTSRegExp)recv!).ToString()),

            _ => null
        };
        if (builtIn != null) return builtIn;
        return receiver.TryGetProperty(name, out var userVal) ? userVal : null;
    }

    /// <summary>
    /// Sets an instance member (property) for a RegExp object.
    /// Only lastIndex is writable.
    /// </summary>
    /// <returns>True if the property was set, false if the property is not writable.</returns>
    public static bool SetMember(SharpTSRegExp receiver, string name, object? value)
    {
        if (name == "lastIndex")
        {
            // ECMA-262 ToLength: assignments like `re.lastIndex = undefined`,
            // `= "1.9"`, `= {valueOf: ...}` are legal — they coerce via
            // ToNumber → ToInteger → bounded to [0, 2^53-1]. A hard
            // (int)(double)value cast throws InvalidCastException on the
            // primitives that aren't already double, so route via JS coercion.
            receiver.LastIndex = ToLengthAsInt32(value);
            return true;
        }
        return false;
    }

    /// <summary>
    /// ECMA-262 §7.1.20 ToLength via §7.1.5 ToInteger via §7.1.4 ToNumber,
    /// clamped to int32 (lastIndex storage).
    /// </summary>
    private static int ToLengthAsInt32(object? value)
    {
        double d = value switch
        {
            null => 0,
            SharpTSUndefined => double.NaN,
            double dv => dv,
            int iv => iv,
            long lv => lv,
            bool bv => bv ? 1 : 0,
            string sv => double.TryParse(sv, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN,
            _ => double.NaN
        };
        if (double.IsNaN(d)) return 0;
        if (d <= 0) return 0;
        if (d > int.MaxValue) return int.MaxValue;
        return (int)d;
    }

    // ECMA-262 §22.2.5 well-known-symbol-keyed methods on RegExp.prototype.
    // Hoisted as static singletons so:
    //   1. `RegExp.prototype[Symbol.match]` and `r[Symbol.match]` reference
    //      the *same* function value (spec requires this; tests check via ===).
    //   2. The function metadata (name, length) is set up once.
    //   3. Property-descriptor introspection on RegExp.prototype works through
    //      SharpTSObject's symbol-keyed storage.
    // Spec lengths: match/matchAll/search = 1; replace/split = 2.

    // minArity is 0 here so test262 patterns like
    // `RegExp.prototype[Symbol.search].call(fakeRe)` (no string arg) reach
    // the body and ToString-coerce undefined to "undefined". JS-spec
    // `length` is still 1/2 (set via WithSpecLength below) — the runtime
    // arity check only governs argument-count rejection, not the visible
    // .length value used by isConstructor / verifyProperty introspection.
    private static readonly BuiltInMethod _symbolMatch =
        new BuiltInMethod("[Symbol.match]", 0, int.MaxValue, SymbolMatchImpl).WithSpecLength(1);

    private static readonly BuiltInMethod _symbolMatchAll =
        new BuiltInMethod("[Symbol.matchAll]", 0, int.MaxValue, SymbolMatchAllImpl).WithSpecLength(1);

    private static readonly BuiltInMethod _symbolReplace =
        new BuiltInMethod("[Symbol.replace]", 0, int.MaxValue, SymbolReplaceImpl).WithSpecLength(2);

    private static readonly BuiltInMethod _symbolSearch =
        new BuiltInMethod("[Symbol.search]", 0, int.MaxValue, SymbolSearchImpl).WithSpecLength(1);

    private static readonly BuiltInMethod _symbolSplit =
        new BuiltInMethod("[Symbol.split]", 0, int.MaxValue, SymbolSplitImpl).WithSpecLength(2);

    /// <summary>
    /// Singleton SharpTSObject standing in for RegExp.prototype. Holds the
    /// five well-known-symbol-keyed methods so they're reachable via
    /// `RegExp.prototype[Symbol.match]` etc., with proper property descriptors
    /// (writable, !enumerable, configurable) provided by SharpTSObject's
    /// symbol-storage layer.
    /// </summary>
    public static readonly SharpTSObject Prototype = BuildPrototype();

    private static SharpTSObject BuildPrototype()
    {
        var proto = new SharpTSObject(new Dictionary<string, object?>());
        proto.SetBySymbol(SharpTSSymbol.Match, _symbolMatch);
        proto.SetBySymbol(SharpTSSymbol.MatchAll, _symbolMatchAll);
        proto.SetBySymbol(SharpTSSymbol.Replace, _symbolReplace);
        proto.SetBySymbol(SharpTSSymbol.Search, _symbolSearch);
        proto.SetBySymbol(SharpTSSymbol.Split, _symbolSplit);
        return proto;
    }

    /// <summary>
    /// Resolves a well-known symbol on the RegExp prototype to the corresponding
    /// callable per ECMA-262 §22.2.5 (@@match, @@matchAll, @@replace, @@search, @@split).
    /// Returns null for symbols not present on the prototype. The returned
    /// method is bound to the supplied receiver so `regex[Symbol.match]('s')`
    /// dispatches with `regex` as `this`.
    /// </summary>
    public static object? GetSymbolMember(SharpTSRegExp receiver, SharpTSSymbol symbol)
    {
        var unbound = GetSymbolMethodUnbound(symbol);
        return unbound?.Bind(receiver);
    }

    /// <summary>
    /// Returns the unbound singleton callable for a well-known RegExp symbol,
    /// or null if the symbol isn't one of the five protocol methods. Used by
    /// the prototype object so accessing `RegExp.prototype[Symbol.match]`
    /// returns the same function value across calls (spec invariant).
    /// </summary>
    public static BuiltInMethod? GetSymbolMethodUnbound(SharpTSSymbol symbol)
    {
        if (ReferenceEquals(symbol, SharpTSSymbol.Match)) return _symbolMatch;
        if (ReferenceEquals(symbol, SharpTSSymbol.MatchAll)) return _symbolMatchAll;
        if (ReferenceEquals(symbol, SharpTSSymbol.Replace)) return _symbolReplace;
        if (ReferenceEquals(symbol, SharpTSSymbol.Search)) return _symbolSearch;
        if (ReferenceEquals(symbol, SharpTSSymbol.Split)) return _symbolSplit;
        return null;
    }

    // ===== ECMA-262 §22.2.5 abstract-operation helpers =====
    //
    // Each protocol method below now follows the spec algorithm: it accepts
    // ANY object as `this` (the test262 tests pass plain objects pretending
    // to be regex via `RegExp.prototype[Symbol.X].call(fakeRe, str)`) and
    // reads/writes flags, lastIndex, exec, etc. via Get/Set so user-defined
    // getters/setters (Object.defineProperty path) fire and propagate
    // their thrown errors. The previous implementations cast `recv` to
    // SharpTSRegExp directly, bypassing user overrides.

    /// <summary>
    /// ECMA-262 §22.2.5.2.1 RegExpExec(R, S) — invokes R.exec(S) if R has a
    /// callable `exec` property; otherwise falls back to RegExpBuiltinExec
    /// (only valid when R is a real SharpTSRegExp). Throws TypeError if
    /// exec returns a non-object non-null value.
    /// </summary>
    private static object? RegExpExec(Interpreter interp, object? rx, string s)
    {
        var exec = interp.GetProperty(rx, "exec");
        if (exec is ISharpTSCallable callable)
        {
            var result = FunctionBuiltIns.CallWithThis(interp, callable, rx, [s]);
            if (result is null || result is SharpTSUndefined) return null;
            // Per spec, exec must return Object or Null. Anything else → TypeError.
            if (result is not (SharpTSObject or SharpTSArray or SharpTSInstance))
                throw new ThrowException(new SharpTSTypeError(
                    "RegExp.prototype exec returned a non-object"));
            return result;
        }
        // No callable exec → fall back to the built-in matcher; only valid
        // when receiver is an actual RegExp (otherwise spec throws).
        if (rx is SharpTSRegExp regex)
        {
            var raw = regex.Exec(s);
            return raw;
        }
        throw new ThrowException(new SharpTSTypeError(
            "RegExp.prototype method called on non-RegExp without a callable exec"));
    }

    /// <summary>
    /// Throws TypeError if <paramref name="rx"/> is not an Object per
    /// ECMA-262 §22.2.5 step 2. Strings/numbers/booleans/null/undefined are
    /// rejected; objects (including plain SharpTSObject, SharpTSRegExp, etc.)
    /// pass.
    /// </summary>
    private static void RequireObject(object? rx, string siteName)
    {
        if (rx is null or SharpTSUndefined or bool or double or string)
            throw new ThrowException(new SharpTSTypeError(
                $"RegExp.prototype{siteName} called on non-object"));
    }

    /// <summary>
    /// Coerces <paramref name="value"/> to a JS-spec string. Wraps the
    /// interpreter's Stringify with explicit handling for the args[0] empty
    /// case (treated as `undefined`).
    /// </summary>
    private static string ToStr(Interpreter interp, object? value)
        => value is null ? "undefined" : interp.Stringify(value);

    /// <summary>
    /// ECMA-262 §7.1.20 ToLength as int. NaN/negative → 0; clamped to int.MaxValue.
    /// </summary>
    private static int ToLengthAsInt(object? value)
    {
        double d = value switch
        {
            null => 0,
            SharpTSUndefined => double.NaN,
            double dv => dv,
            int iv => iv,
            long lv => lv,
            bool bv => bv ? 1 : 0,
            string sv => double.TryParse(sv, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN,
            _ => double.NaN
        };
        if (double.IsNaN(d) || d <= 0) return 0;
        if (d > int.MaxValue) return int.MaxValue;
        return (int)d;
    }

    /// <summary>
    /// ECMA-262 §22.2.5.7 RegExp.prototype [@@match].
    /// </summary>
    private static object? SymbolMatchImpl(Interpreter interp, object? recv, List<object?> args)
    {
        RequireObject(recv, "[Symbol.match]");

        // 3. Let S be ? ToString(string).
        var s = ToStr(interp, args.Count > 0 ? args[0] : null);

        // 4. Let flags be ? ToString(? Get(rx, "flags")).
        var flags = ToStr(interp, interp.GetProperty(recv, "flags"));

        // 5. If flags does not contain "g", return ? RegExpExec(rx, S).
        if (!flags.Contains('g'))
            return RegExpExec(interp, recv, s);

        // 6.a fullUnicode = flags contains "u"
        bool fullUnicode = flags.Contains('u');

        // 6.b Set(rx, "lastIndex", 0, true).
        interp.SetProperty(recv, "lastIndex", 0.0);

        // 6.c-e Loop, accumulate matched strings.
        var results = new List<object?>();
        while (true)
        {
            var result = RegExpExec(interp, recv, s);
            if (result is null)
            {
                if (results.Count == 0) return null;
                return new SharpTSArray(results);
            }

            // matchStr = ToString(Get(result, "0"))
            var matchStr = ToStr(interp, interp.GetProperty(result, "0"));
            results.Add(matchStr);

            // If matchStr is empty, advance lastIndex to avoid infinite loop.
            if (matchStr.Length == 0)
            {
                var thisIndex = ToLengthAsInt(interp.GetProperty(recv, "lastIndex"));
                int nextIndex = AdvanceStringIndex(s, thisIndex, fullUnicode);
                interp.SetProperty(recv, "lastIndex", (double)nextIndex);
            }
        }
    }

    /// <summary>
    /// ECMA-262 §22.2.5.11 RegExp.prototype [@@search].
    /// </summary>
    private static object? SymbolSearchImpl(Interpreter interp, object? recv, List<object?> args)
    {
        RequireObject(recv, "[Symbol.search]");
        var s = ToStr(interp, args.Count > 0 ? args[0] : null);

        // Save lastIndex, set to 0, run RegExpExec, restore lastIndex.
        var previousLastIndex = interp.GetProperty(recv, "lastIndex");
        if (!IsZero(previousLastIndex))
            interp.SetProperty(recv, "lastIndex", 0.0);

        var result = RegExpExec(interp, recv, s);

        var currentLastIndex = interp.GetProperty(recv, "lastIndex");
        if (!SameValue(currentLastIndex, previousLastIndex))
            interp.SetProperty(recv, "lastIndex", previousLastIndex);

        if (result is null) return -1.0;
        var index = interp.GetProperty(result, "index");
        return index ?? 0.0;
    }

    /// <summary>
    /// ECMA-262 §22.2.5.10 RegExp.prototype [@@replace] — simplified
    /// implementation supporting string + callable replacement values.
    /// Reads flags via Get; routes through RegExpExec so user-installed
    /// `exec` overrides participate.
    /// </summary>
    private static object? SymbolReplaceImpl(Interpreter interp, object? recv, List<object?> args)
    {
        RequireObject(recv, "[Symbol.replace]");
        var s = ToStr(interp, args.Count > 0 ? args[0] : null);
        var replaceValue = args.Count > 1 ? args[1] : null;

        // Read flags via Get so user getters fire.
        var flags = ToStr(interp, interp.GetProperty(recv, "flags"));
        bool global = flags.Contains('g');
        bool fullUnicode = global && flags.Contains('u');

        // Reset lastIndex when global.
        if (global)
            interp.SetProperty(recv, "lastIndex", 0.0);

        // Collect all match results so we can replace bottom-up.
        var matches = new List<object>();
        while (true)
        {
            var result = RegExpExec(interp, recv, s);
            if (result is null) break;
            matches.Add(result);
            if (!global) break;

            var matchStr = ToStr(interp, interp.GetProperty(result, "0"));
            if (matchStr.Length == 0)
            {
                var thisIndex = ToLengthAsInt(interp.GetProperty(recv, "lastIndex"));
                int nextIndex = AdvanceStringIndex(s, thisIndex, fullUnicode);
                interp.SetProperty(recv, "lastIndex", (double)nextIndex);
            }
        }

        if (matches.Count == 0) return s;

        // Build the result string with replacements.
        bool isCallable = replaceValue is ISharpTSCallable;
        string replaceStr = isCallable ? "" : ToStr(interp, replaceValue);
        var sb = new System.Text.StringBuilder();
        int nextSourcePosition = 0;
        foreach (var match in matches)
        {
            var matchStr = ToStr(interp, interp.GetProperty(match, "0"));
            int position = ToLengthAsInt(interp.GetProperty(match, "index"));
            position = Math.Clamp(position, 0, s.Length);

            string replacement;
            if (isCallable)
            {
                // Build args: [matched, ...captures, position, string].
                var callArgs = new List<object?> { matchStr };
                // The match is a SharpTSArray with capture groups in [1..length-1].
                if (match is SharpTSArray arr)
                {
                    for (int i = 1; i < arr.Length; i++)
                        callArgs.Add(arr[i]);
                }
                callArgs.Add((double)position);
                callArgs.Add(s);
                var fnResult = FunctionBuiltIns.CallWithThis(
                    interp, (ISharpTSCallable)replaceValue!, null, callArgs);
                replacement = ToStr(interp, fnResult);
            }
            else
            {
                replacement = replaceStr;
            }

            // Append the un-modified slice + replacement.
            if (position >= nextSourcePosition)
            {
                sb.Append(s, nextSourcePosition, position - nextSourcePosition);
                sb.Append(replacement);
                nextSourcePosition = position + matchStr.Length;
            }
        }
        if (nextSourcePosition < s.Length)
            sb.Append(s, nextSourcePosition, s.Length - nextSourcePosition);
        return sb.ToString();
    }

    /// <summary>
    /// ECMA-262 §22.2.5.13 RegExp.prototype [@@split]. Constructs a
    /// sticky-flagged splitter via SpeciesConstructor, then iterates
    /// matches via that splitter (so user-installed Symbol.species or
    /// constructor.exec participates).
    /// </summary>
    private static object? SymbolSplitImpl(Interpreter interp, object? recv, List<object?> args)
    {
        RequireObject(recv, "[Symbol.split]");
        var s = ToStr(interp, args.Count > 0 ? args[0] : null);
        var limitArg = args.Count > 1 ? args[1] : null;

        // §22.2.5.13 step 4: C = SpeciesConstructor(rx, %RegExp%).
        var speciesCtor = SpeciesConstructor(interp, recv);

        // Step 5: flags = ToString(? Get(rx, "flags")).
        var flags = ToStr(interp, interp.GetProperty(recv, "flags"));
        bool fullUnicode = flags.Contains('u');

        // Step 7-8: newFlags = "y" in flags ? flags : flags+"y".
        string newFlags = flags.Contains('y') ? flags : flags + "y";

        // Step 9: splitter = ? Construct(C, « rx, newFlags »).
        // When the user didn't override constructor / species, fall back to
        // %RegExp% — synthesize a fresh sticky-flagged regex from recv's
        // source. The matching loop relies on sticky semantics (match must
        // start at exactly lastIndex) to scan strictly forward.
        object? splitter;
        if (speciesCtor != null)
        {
            splitter = interp.Construct(speciesCtor, [recv, newFlags]);
        }
        else if (recv is SharpTSRegExp rxInstance)
        {
            splitter = new SharpTSRegExp(rxInstance.Source, newFlags);
        }
        else
        {
            // Non-RegExp `this` without species ctor — nothing to construct.
            // Fall through with recv; the matching loop still routes through
            // RegExpExec for any user-installed exec on it.
            splitter = recv;
        }

        // Step 12: lim = ToUint32(limit).
        long limit = limitArg switch
        {
            null or SharpTSUndefined => uint.MaxValue,
            double d => double.IsNaN(d) ? 0 : (long)((uint)d),
            _ => 0,
        };
        if (limit == 0) return new SharpTSArray(new List<object?>());

        var arr = new List<object?>();
        if (s.Length == 0)
        {
            // Empty input string: if splitter matches empty → return []; else [""].
            interp.SetProperty(splitter, "lastIndex", 0.0);
            var result = RegExpExec(interp, splitter, s);
            if (result is null) arr.Add("");
            return new SharpTSArray(arr);
        }

        int p = 0;     // position in S where the next non-matched segment starts
        int q = 0;     // current scan position

        while (q < s.Length)
        {
            interp.SetProperty(splitter, "lastIndex", (double)q);
            var z = RegExpExec(interp, splitter, s);
            if (z is null)
            {
                q = AdvanceStringIndex(s, q, fullUnicode);
                continue;
            }

            // Read post-match lastIndex. With sticky support in SharpTSRegExp
            // (`y` flag preserved + Exec verifies match position == LastIndex),
            // a successful exec advances lastIndex by the match length. For
            // non-sticky receivers (user-installed exec on a plain object),
            // the user is responsible for advancing lastIndex.
            int e = ToLengthAsInt(interp.GetProperty(splitter, "lastIndex"));
            e = Math.Min(e, s.Length);
            if (e == p)
            {
                q = AdvanceStringIndex(s, q, fullUnicode);
                continue;
            }

            // Add matched segment to output.
            arr.Add(s.Substring(p, q - p));
            if (arr.Count >= limit) return new SharpTSArray(arr);

            // Add capture groups.
            if (z is SharpTSArray zArr)
            {
                for (int i = 1; i < zArr.Length; i++)
                {
                    arr.Add(zArr[i]);
                    if (arr.Count >= limit) return new SharpTSArray(arr);
                }
            }

            p = e;
            q = e;
        }

        arr.Add(s.Substring(p));
        return new SharpTSArray(arr);
    }

    /// <summary>
    /// ECMA-262 §10.2.5 SpeciesConstructor(O, defaultConstructor) — looks
    /// up the species constructor for <paramref name="O"/>. Returns null
    /// when the user didn't override (the caller should fall back to the
    /// default %RegExp%; we represent "use default" as null and let the
    /// caller decide whether to construct a fresh regex via the built-in
    /// factory or skip construction entirely).
    /// </summary>
    private static object? SpeciesConstructor(Interpreter interp, object? O)
    {
        // Step 2: Let C be ? Get(O, "constructor").
        var c = interp.GetProperty(O, "constructor");

        // Step 3: If C is undefined, return defaultConstructor (we signal
        // "use default" with null).
        if (c is null or SharpTSUndefined) return null;

        // Step 4: If Type(C) is not Object, throw TypeError.
        if (c is bool or double or string)
            throw new ThrowException(new SharpTSTypeError(
                "constructor must be an object"));

        // Step 5: Let S be ? Get(C, @@species).
        // For symbol-keyed lookup we go through the symbol-dict mechanism on
        // the constructor object; SharpTSObject / Function / etc. expose
        // GetBySymbol or accept defineProperty for symbol keys.
        var species = c switch
        {
            SharpTSObject sObj => sObj.GetBySymbol(SharpTSSymbol.Species),
            SharpTSInstance inst => inst.GetBySymbol(SharpTSSymbol.Species),
            SharpTSFunction fn => GetSymbolPropertyFromCallable(fn, SharpTSSymbol.Species),
            SharpTSArrowFunction arr => GetSymbolPropertyFromCallable(arr, SharpTSSymbol.Species),
            _ => null
        };

        // Step 6-7: undefined / null → return defaultConstructor.
        if (species is null or SharpTSUndefined) return null;

        // Step 8: IsConstructor(S) → return S. We don't have IsConstructor
        // implemented, so accept any callable (matches most test262 patterns
        // — they install plain functions as species).
        if (species is ISharpTSCallable) return species;

        throw new ThrowException(new SharpTSTypeError("species is not a constructor"));
    }

    /// <summary>
    /// Reads a symbol-keyed property from a callable that supports user
    /// property assignment (Object.defineProperty path). SharpTSFunction has
    /// per-instance symbol storage; arrow functions follow the same pattern.
    /// </summary>
    private static object? GetSymbolPropertyFromCallable(object obj, SharpTSSymbol symbol)
    {
        // Both SharpTSFunction and SharpTSArrowFunction expose user property
        // storage via TryGetProperty; symbols are stored alongside string
        // properties for our purposes here. Real spec storage would route
        // through dedicated symbol slots, but the test262 species patterns
        // set the property via `fn[Symbol.species] = ...` which our
        // SetIndex path already routes through symbol storage on the
        // function instance.
        if (obj is SharpTSFunction fn && fn.TryGetSymbolProperty(symbol, out var v))
            return v;
        if (obj is SharpTSArrowFunction arr && arr.TryGetSymbolProperty(symbol, out var v2))
            return v2;
        return null;
    }

    /// <summary>
    /// ECMA-262 §22.2.5.8 RegExp.prototype [@@matchAll]. Simplified — returns
    /// a $Array of match results. Doesn't yet honor SpeciesConstructor
    /// (callers' tests cover that separately); reads flags via Get for
    /// user-getter propagation.
    /// </summary>
    private static object? SymbolMatchAllImpl(Interpreter interp, object? recv, List<object?> args)
    {
        RequireObject(recv, "[Symbol.matchAll]");
        var s = ToStr(interp, args.Count > 0 ? args[0] : null);

        var flags = ToStr(interp, interp.GetProperty(recv, "flags"));
        bool fullUnicode = flags.Contains('u');

        // Reset lastIndex when global; for non-global, the per-spec sequence
        // would species-construct a global splitter, but our simplified
        // implementation just iterates from current lastIndex (or 0 if none).
        if (flags.Contains('g'))
            interp.SetProperty(recv, "lastIndex", 0.0);

        var results = new List<object?>();
        while (true)
        {
            var match = RegExpExec(interp, recv, s);
            if (match is null) break;
            results.Add(match);

            // Non-global RegExpExec doesn't advance lastIndex on its own —
            // exit after the first match to avoid infinite loop.
            if (!flags.Contains('g')) break;

            var matchStr = ToStr(interp, interp.GetProperty(match, "0"));
            if (matchStr.Length == 0)
            {
                var thisIndex = ToLengthAsInt(interp.GetProperty(recv, "lastIndex"));
                int nextIndex = AdvanceStringIndex(s, thisIndex, fullUnicode);
                interp.SetProperty(recv, "lastIndex", (double)nextIndex);
            }
        }
        return new SharpTSArray(results);
    }

    /// <summary>
    /// ECMA-262 §22.2.5.2.3 AdvanceStringIndex(S, index, unicode) — when
    /// unicode + the code unit at index is a high surrogate paired with the
    /// next being a low surrogate, advance by 2; otherwise by 1.
    /// </summary>
    private static int AdvanceStringIndex(string s, int index, bool unicode)
    {
        if (!unicode || index + 1 >= s.Length) return index + 1;
        int first = s[index];
        if (first < 0xD800 || first > 0xDBFF) return index + 1;
        int second = s[index + 1];
        if (second < 0xDC00 || second > 0xDFFF) return index + 1;
        return index + 2;
    }

    /// <summary>
    /// ECMA-262 §7.2.10 SameValue(x, y) — numeric SameValue (handles +0/-0
    /// and NaN) plus reference equality for objects / strings / etc.
    /// </summary>
    private static bool SameValue(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a is double da && b is double db)
        {
            if (double.IsNaN(da) && double.IsNaN(db)) return true;
            return da.Equals(db); // distinguishes +0 / -0
        }
        return Equals(a, b);
    }

    /// <summary>
    /// True iff <paramref name="value"/> is numerically zero (covers +0, -0,
    /// and the int/long forms used by lastIndex storage).
    /// </summary>
    private static bool IsZero(object? value) => value switch
    {
        double d => d == 0,
        int i => i == 0,
        long l => l == 0,
        _ => false,
    };
}
