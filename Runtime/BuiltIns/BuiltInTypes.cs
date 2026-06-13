using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Provides type signatures for built-in JavaScript types (strings, arrays, errors, etc.).
/// Used by the TypeChecker to validate method calls and property access on built-in types.
/// </summary>
/// <remarks>
/// For Error types, this class provides type information while <see cref="ErrorBuiltIns"/>
/// provides the runtime implementation. These must be kept in sync.
/// </remarks>
public static class BuiltInTypes
{
    private static readonly TypeInfo NumberType = new TypeInfo.Primitive(TokenType.TYPE_NUMBER);
    private static readonly TypeInfo StringType = new TypeInfo.String();
    private static readonly TypeInfo BooleanType = new TypeInfo.Primitive(TokenType.TYPE_BOOLEAN);
    private static readonly TypeInfo VoidType = new TypeInfo.Void();
    private static readonly TypeInfo AnyType = new TypeInfo.Any();
    private static readonly TypeInfo BigIntType = new TypeInfo.BigInt();

    /// <summary>
    /// Type signatures for static methods on the String namespace
    /// </summary>
    public static TypeInfo? GetStringStaticMethodType(string name)
    {
        return name switch
        {
            "fromCharCode" => new TypeInfo.Function(
                [new TypeInfo.Array(NumberType)],  // rest parameter for char codes
                StringType,
                RequiredParams: 0,
                HasRestParam: true),
            "fromCodePoint" => new TypeInfo.Function(
                [new TypeInfo.Array(NumberType)],  // rest parameter for code points
                StringType,
                RequiredParams: 0,
                HasRestParam: true),
            "raw" => new TypeInfo.Function(
                [AnyType, new TypeInfo.Array(AnyType)],  // template strings array + substitutions
                StringType,
                RequiredParams: 1,
                HasRestParam: true),
            _ => null
        };
    }

    public static TypeInfo? GetStringMemberType(string name)
    {
        return name switch
        {
            "length" => NumberType,
            "charAt" => new TypeInfo.Function([NumberType], StringType),
            "substring" => new TypeInfo.Function([NumberType, NumberType], StringType, RequiredParams: 1), // end is optional
            "indexOf" => new TypeInfo.Function([StringType, NumberType], NumberType, RequiredParams: 1),
            "toUpperCase" => new TypeInfo.Function([], StringType),
            "toLowerCase" => new TypeInfo.Function([], StringType),
            "trim" => new TypeInfo.Function([], StringType),
            // replace(searchValue, replaceValue) accepts string | RegExp for the
            // pattern and string | (match, ...groups) => string for the value.
            // Use Any for both to cover every JS overload the interpreter and
            // compiler already support at runtime.
            "replace" => new TypeInfo.Function([AnyType, AnyType], StringType),
            // split(separator?, limit?) — both args optional. With no args
            // returns [self] (per ECMA-262 §22.1.3.21 ToString(undefined) = "" but
            // separator=undefined produces a single-element array).
            "split" => new TypeInfo.Function([AnyType, NumberType], new TypeInfo.Array(StringType), RequiredParams: 0),
            // ECMA-262 §22.1.3.{7,20,6} String.prototype.{includes,startsWith,
            // endsWith}(searchString, position?). position defaults to 0
            // (includes/startsWith) or length (endsWith).
            "includes" => new TypeInfo.Function([StringType, NumberType], BooleanType, RequiredParams: 1),
            "startsWith" => new TypeInfo.Function([StringType, NumberType], BooleanType, RequiredParams: 1),
            "endsWith" => new TypeInfo.Function([StringType, NumberType], BooleanType, RequiredParams: 1),
            "slice" => new TypeInfo.Function([NumberType, NumberType], StringType, RequiredParams: 1), // end is optional
            "substr" => new TypeInfo.Function([NumberType, NumberType], StringType, RequiredParams: 1), // length is optional (legacy, Annex B)
            "repeat" => new TypeInfo.Function([NumberType], StringType),
            "padStart" => new TypeInfo.Function([NumberType, StringType], StringType, RequiredParams: 1), // padString is optional
            "padEnd" => new TypeInfo.Function([NumberType, StringType], StringType, RequiredParams: 1), // padString is optional
            "charCodeAt" => new TypeInfo.Function([NumberType], NumberType),
            "codePointAt" => new TypeInfo.Function([NumberType], NumberType),
            "concat" => new TypeInfo.Function([new TypeInfo.Array(StringType)], StringType, RequiredParams: 0, HasRestParam: true), // variadic - takes 0 or more string arguments
            "lastIndexOf" => new TypeInfo.Function([StringType, NumberType], NumberType, RequiredParams: 1),
            "trimStart" => new TypeInfo.Function([], StringType),
            "trimEnd" => new TypeInfo.Function([], StringType),
            // replaceAll accepts string | RegExp for the pattern.
            "replaceAll" => new TypeInfo.Function([AnyType, AnyType], StringType),
            "at" => new TypeInfo.Function([NumberType], StringType), // returns string | undefined in TS
            "normalize" => new TypeInfo.Function([StringType], StringType, RequiredParams: 0),
            "localeCompare" => new TypeInfo.Function([StringType], NumberType, RequiredParams: 0),
            "toString" => new TypeInfo.Function([], StringType), // primitive wrapper method
            "match" => new TypeInfo.Function([AnyType], AnyType),
            "matchAll" => new TypeInfo.Function([AnyType], new TypeInfo.Array(AnyType)),
            "search" => new TypeInfo.Function([AnyType], NumberType),
            _ => null
        };
    }

    public static TypeInfo? GetArrayMemberType(string name, TypeInfo elementType)
    {
        return name switch
        {
            "length" => NumberType,
            "push" => new TypeInfo.Function([new TypeInfo.Array(elementType)], NumberType, RequiredParams: 1, HasRestParam: true), // variadic: push(...items: T[])
            "pop" => new TypeInfo.Function([], elementType),
            "shift" => new TypeInfo.Function([], elementType),
            "unshift" => new TypeInfo.Function([new TypeInfo.Array(elementType)], NumberType, RequiredParams: 0, HasRestParam: true), // variadic: unshift(...items: T[])
            "slice" => new TypeInfo.Function([NumberType, NumberType], new TypeInfo.Array(elementType), RequiredParams: 0), // start/end are optional
            // ECMA-262: callbackfn[, thisArg] — thisArg is optional Any
            "map" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType, new TypeInfo.Array(elementType)], AnyType, RequiredParams: 1), AnyType],
                new TypeInfo.Array(AnyType), RequiredParams: 1),
            "filter" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType, new TypeInfo.Array(elementType)], BooleanType, RequiredParams: 1), AnyType],
                new TypeInfo.Array(elementType), RequiredParams: 1),
            "forEach" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType, new TypeInfo.Array(elementType)], VoidType, RequiredParams: 1), AnyType],
                VoidType, RequiredParams: 1),
            "find" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType, new TypeInfo.Array(elementType)], BooleanType, RequiredParams: 1), AnyType],
                elementType, RequiredParams: 1),
            "findIndex" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType, new TypeInfo.Array(elementType)], BooleanType, RequiredParams: 1), AnyType],
                NumberType, RequiredParams: 1),
            "some" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType, new TypeInfo.Array(elementType)], BooleanType, RequiredParams: 1), AnyType],
                BooleanType, RequiredParams: 1),
            "every" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType, new TypeInfo.Array(elementType)], BooleanType, RequiredParams: 1), AnyType],
                BooleanType, RequiredParams: 1),
            "reduce" => new TypeInfo.Function(
                [new TypeInfo.Function([AnyType, elementType, NumberType, new TypeInfo.Array(elementType)], AnyType, RequiredParams: 2), AnyType],
                AnyType, RequiredParams: 1), // initialValue is optional
            "reduceRight" => new TypeInfo.Function(
                [new TypeInfo.Function([AnyType, elementType, NumberType, new TypeInfo.Array(elementType)], AnyType, RequiredParams: 2), AnyType],
                AnyType, RequiredParams: 1), // initialValue is optional
            "includes" => new TypeInfo.Function([elementType], BooleanType),
            // ECMA-262 23.1.3.17 / 23.1.3.18: fromIndex is coerced via
            // ToIntegerOrInfinity — accepts objects (with valueOf/toString),
            // strings ("Infinity"), booleans, etc. Signature widened to Any so
            // the type checker doesn't reject ahead of the spec coercion.
            "indexOf" => new TypeInfo.Function([elementType, AnyType], NumberType, RequiredParams: 1),
            "lastIndexOf" => new TypeInfo.Function([elementType, AnyType], NumberType, RequiredParams: 1),
            // ECMA-262 23.1.3.16: separator is optional; undefined is treated
            // as ","; every other value is coerced via ToString. Widen the
            // static signature to `Any` so the type checker accepts both
            // `arr.join()` and `arr.join(undefined)`/`arr.join(null)` without
            // rejecting them ahead of the spec coercion.
            "join" => new TypeInfo.Function([AnyType], StringType, RequiredParams: 0),
            // ECMA-262 23.1.3.1: concat accepts arbitrary args (arrays or values),
            // each spread or appended; widen to AnyType rest param.
            "concat" => new TypeInfo.Function(
                [new TypeInfo.Array(AnyType)],
                new TypeInfo.Array(elementType),
                RequiredParams: 0,
                HasRestParam: true),
            "reverse" => new TypeInfo.Function([], new TypeInfo.Array(elementType)),
            "flat" => new TypeInfo.Function([NumberType], new TypeInfo.Array(AnyType), RequiredParams: 0), // depth is optional, defaults to 1
            "flatMap" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType, new TypeInfo.Array(elementType)], AnyType, RequiredParams: 1), AnyType],
                new TypeInfo.Array(AnyType), RequiredParams: 1),
            "sort" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, elementType], NumberType)],
                new TypeInfo.Array(elementType),
                RequiredParams: 0),
            "toSorted" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, elementType], NumberType)],
                new TypeInfo.Array(elementType),
                RequiredParams: 0),
            "splice" => new TypeInfo.Function(
                [NumberType, NumberType, new TypeInfo.Array(elementType)],
                new TypeInfo.Array(elementType),
                RequiredParams: 0,
                HasRestParam: true),
            "toSpliced" => new TypeInfo.Function(
                [NumberType, NumberType, new TypeInfo.Array(elementType)],
                new TypeInfo.Array(elementType),
                RequiredParams: 0,
                HasRestParam: true),
            "findLast" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType], BooleanType), AnyType],
                elementType, RequiredParams: 1),
            "findLastIndex" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType], BooleanType), AnyType],
                NumberType, RequiredParams: 1),
            "toReversed" => new TypeInfo.Function([], new TypeInfo.Array(elementType)),
            "with" => new TypeInfo.Function([NumberType, elementType], new TypeInfo.Array(elementType)),
            "at" => new TypeInfo.Function([NumberType], elementType),
            "fill" => new TypeInfo.Function(
                [elementType, NumberType, NumberType],
                new TypeInfo.Array(elementType),
                RequiredParams: 1),  // value required, start and end optional
            "copyWithin" => new TypeInfo.Function(
                [NumberType, NumberType, NumberType],
                new TypeInfo.Array(elementType),
                RequiredParams: 1),  // target required, start and end optional
            "entries" => new TypeInfo.Function([],
                new TypeInfo.Iterator(TypeInfo.Tuple.FromTypes([NumberType, elementType], 2))),
            "keys" => new TypeInfo.Function([],
                new TypeInfo.Iterator(NumberType)),
            "values" => new TypeInfo.Function([],
                new TypeInfo.Iterator(elementType)),
            // ECMA-262 23.1.3.32 / 23.1.3.33: toString and toLocaleString
            // exist on every Array. Without these, `arr.toString()` is a
            // TypeChecker error even though it works at runtime.
            "toString" => new TypeInfo.Function([], StringType),
            "toLocaleString" => new TypeInfo.Function([], StringType),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for static methods on the Object namespace
    /// </summary>
    public static TypeInfo? GetObjectStaticMethodType(string name)
    {
        return name switch
        {
            "keys" => new TypeInfo.Function([AnyType], new TypeInfo.Array(StringType)),
            "values" => new TypeInfo.Function([AnyType], new TypeInfo.Array(AnyType)),
            "entries" => new TypeInfo.Function([AnyType], new TypeInfo.Array(AnyType)),
            "fromEntries" => new TypeInfo.Function([AnyType], AnyType),
            "hasOwn" => new TypeInfo.Function([AnyType, StringType], BooleanType),
            "is" => new TypeInfo.Function([AnyType, AnyType], BooleanType),
            "assign" => new TypeInfo.Function([AnyType], AnyType),  // target, ...sources
            "freeze" => new TypeInfo.Function([AnyType], AnyType),  // Returns the frozen object
            "seal" => new TypeInfo.Function([AnyType], AnyType),    // Returns the sealed object
            "isFrozen" => new TypeInfo.Function([AnyType], BooleanType),
            "isSealed" => new TypeInfo.Function([AnyType], BooleanType),
            "getOwnPropertyNames" => new TypeInfo.Function([AnyType], new TypeInfo.Array(StringType)),
            "create" => new TypeInfo.Function([AnyType, AnyType], AnyType, RequiredParams: 1),  // proto required, propertiesObject optional
            "groupBy" => new TypeInfo.Function([AnyType, new TypeInfo.Function([AnyType, NumberType], AnyType)], AnyType),
            "defineProperties" => new TypeInfo.Function([AnyType, AnyType], AnyType),
            "getOwnPropertyDescriptors" => new TypeInfo.Function([AnyType], AnyType),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for static methods on the Array namespace
    /// </summary>
    public static TypeInfo? GetArrayStaticMethodType(string name)
    {
        return name switch
        {
            "isArray" => new TypeInfo.Function([AnyType], BooleanType),
            "from" => new TypeInfo.Function(
                [AnyType, new TypeInfo.Function([AnyType, NumberType], AnyType)],
                new TypeInfo.Array(AnyType),
                RequiredParams: 1),  // mapFn is optional
            "of" => new TypeInfo.Function(
                [new TypeInfo.Array(AnyType)],  // rest parameter
                new TypeInfo.Array(AnyType),
                RequiredParams: 0,
                HasRestParam: true),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for static methods on the Map namespace
    /// </summary>
    public static TypeInfo? GetMapStaticMethodType(string name)
    {
        return name switch
        {
            "groupBy" => new TypeInfo.Function([AnyType, new TypeInfo.Function([AnyType, NumberType], AnyType)], AnyType),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for static methods on the JSON namespace
    /// </summary>
    public static TypeInfo? GetJSONStaticMethodType(string name)
    {
        return name switch
        {
            "parse" => new TypeInfo.Function([StringType], AnyType),
            "stringify" => new TypeInfo.Function([AnyType], StringType),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for static members (properties and methods) on the Number namespace
    /// </summary>
    public static TypeInfo? GetNumberStaticMemberType(string name)
    {
        return name switch
        {
            // Static properties (constants)
            "MAX_VALUE" => NumberType,
            "MIN_VALUE" => NumberType,
            "NaN" => NumberType,
            "POSITIVE_INFINITY" => NumberType,
            "NEGATIVE_INFINITY" => NumberType,
            "MAX_SAFE_INTEGER" => NumberType,
            "MIN_SAFE_INTEGER" => NumberType,
            "EPSILON" => NumberType,

            // Static methods
            "parseInt" => new TypeInfo.Function([StringType], NumberType),    // radix is optional
            "parseFloat" => new TypeInfo.Function([StringType], NumberType),
            "isNaN" => new TypeInfo.Function([AnyType], BooleanType),
            "isFinite" => new TypeInfo.Function([AnyType], BooleanType),
            "isInteger" => new TypeInfo.Function([AnyType], BooleanType),
            "isSafeInteger" => new TypeInfo.Function([AnyType], BooleanType),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance methods on number primitives (e.g., (123).toFixed(2))
    /// </summary>
    public static TypeInfo? GetNumberInstanceMemberType(string name)
    {
        return name switch
        {
            "toFixed" => new TypeInfo.Function([], StringType),       // digits is optional
            "toPrecision" => new TypeInfo.Function([], StringType),   // precision is optional
            "toExponential" => new TypeInfo.Function([], StringType), // fractionDigits is optional
            "toString" => new TypeInfo.Function([], StringType),      // radix is optional
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for static methods on the Date namespace
    /// </summary>
    public static TypeInfo? GetDateStaticMemberType(string name)
    {
        return name switch
        {
            "now" => new TypeInfo.Function([], NumberType),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance methods on Date objects
    /// </summary>
    public static TypeInfo? GetDateInstanceMemberType(string name)
    {
        return name switch
        {
            // Getters - all return number
            "getTime" => new TypeInfo.Function([], NumberType),
            "getFullYear" => new TypeInfo.Function([], NumberType),
            "getMonth" => new TypeInfo.Function([], NumberType),
            "getDate" => new TypeInfo.Function([], NumberType),
            "getDay" => new TypeInfo.Function([], NumberType),
            "getHours" => new TypeInfo.Function([], NumberType),
            "getMinutes" => new TypeInfo.Function([], NumberType),
            "getSeconds" => new TypeInfo.Function([], NumberType),
            "getMilliseconds" => new TypeInfo.Function([], NumberType),
            "getTimezoneOffset" => new TypeInfo.Function([], NumberType),

            // UTC getters - all return number
            "getUTCFullYear" => new TypeInfo.Function([], NumberType),
            "getUTCMonth" => new TypeInfo.Function([], NumberType),
            "getUTCDate" => new TypeInfo.Function([], NumberType),
            "getUTCDay" => new TypeInfo.Function([], NumberType),
            "getUTCHours" => new TypeInfo.Function([], NumberType),
            "getUTCMinutes" => new TypeInfo.Function([], NumberType),
            "getUTCSeconds" => new TypeInfo.Function([], NumberType),
            "getUTCMilliseconds" => new TypeInfo.Function([], NumberType),

            // Setters - all return number (the new timestamp). Trailing components
            // are optional per lib.es5 (e.g. setFullYear(year, month?, date?)).
            "setTime" => new TypeInfo.Function([NumberType], NumberType),
            "setFullYear" => new TypeInfo.Function([NumberType, NumberType, NumberType], NumberType, RequiredParams: 1),
            "setMonth" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "setDate" => new TypeInfo.Function([NumberType], NumberType),
            "setHours" => new TypeInfo.Function([NumberType, NumberType, NumberType, NumberType], NumberType, RequiredParams: 1),
            "setMinutes" => new TypeInfo.Function([NumberType, NumberType, NumberType], NumberType, RequiredParams: 1),
            "setSeconds" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "setMilliseconds" => new TypeInfo.Function([NumberType], NumberType),

            // UTC setters - mirror the local setters' optional-trailing-component shape
            "setUTCFullYear" => new TypeInfo.Function([NumberType, NumberType, NumberType], NumberType, RequiredParams: 1),
            "setUTCMonth" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "setUTCDate" => new TypeInfo.Function([NumberType], NumberType),
            "setUTCHours" => new TypeInfo.Function([NumberType, NumberType, NumberType, NumberType], NumberType, RequiredParams: 1),
            "setUTCMinutes" => new TypeInfo.Function([NumberType, NumberType, NumberType], NumberType, RequiredParams: 1),
            "setUTCSeconds" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "setUTCMilliseconds" => new TypeInfo.Function([NumberType], NumberType),

            // Conversion methods
            "toString" => new TypeInfo.Function([], StringType),
            "toISOString" => new TypeInfo.Function([], StringType),
            "toDateString" => new TypeInfo.Function([], StringType),
            "toTimeString" => new TypeInfo.Function([], StringType),
            "toUTCString" => new TypeInfo.Function([], StringType),
            // toLocale* accept optional (locales?, options?) per lib.es2020.date.
            "toLocaleDateString" => new TypeInfo.Function([AnyType, AnyType], StringType, RequiredParams: 0),
            "toLocaleTimeString" => new TypeInfo.Function([AnyType, AnyType], StringType, RequiredParams: 0),
            "toLocaleString" => new TypeInfo.Function([AnyType, AnyType], StringType, RequiredParams: 0),
            // lib.es5: `toJSON(key?: any): string` (used by JSON.stringify and matched by
            // `T extends { toJSON(): infer R }`, see #491). Runtime impl in DateBuiltIns.
            "toJSON" => new TypeInfo.Function([], StringType),
            "valueOf" => new TypeInfo.Function([], NumberType),

            // Legacy methods (ECMA-262 Annex B), declared on Date in lib.es5.
            "getYear" => new TypeInfo.Function([], NumberType),
            "setYear" => new TypeInfo.Function([NumberType], NumberType),

            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance members on RegExp objects
    /// </summary>
    public static TypeInfo? GetRegExpMemberType(string name)
    {
        return name switch
        {
            // Properties (read-only except lastIndex)
            "source" => StringType,
            "flags" => StringType,
            "global" => BooleanType,
            "ignoreCase" => BooleanType,
            "multiline" => BooleanType,
            "dotAll" => BooleanType,
            "sticky" => BooleanType,
            "unicode" => BooleanType,
            "unicodeSets" => BooleanType,
            "hasIndices" => BooleanType,
            "lastIndex" => NumberType,

            // Methods. ECMA-262 §22.2.6.{2,8,16} — test/exec coerce the
            // string arg via ToString, so calling without args produces
            // "undefined". The TS signature historically required the arg;
            // tests like S15.10.6.2_A1_T16 (/undefined/.exec()) and A12
            // (new RegExp('(.|\\r|\\n)*','').exec()) rely on the coercion.
            "test" => new TypeInfo.Function([StringType], BooleanType, RequiredParams: 0),
            "exec" => new TypeInfo.Function([StringType], AnyType, RequiredParams: 0),
            "toString" => new TypeInfo.Function([], StringType),

            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance members on Error objects.
    /// All error types share the same structure: name, message, stack properties and toString() method.
    /// </summary>
    /// <param name="name">The member name to look up</param>
    /// <param name="errorTypeName">The specific error type name (e.g., "Error", "TypeError")</param>
    /// <remarks>
    /// Runtime implementation is in <see cref="ErrorBuiltIns.GetMember"/>.
    /// Property mutability is controlled by <see cref="ErrorBuiltIns.CanSetProperty"/>.
    /// Valid error type names are defined in <see cref="ErrorBuiltIns.TypeNames"/>.
    /// </remarks>
    public static TypeInfo? GetErrorMemberType(string name, string errorTypeName)
    {
        return name switch
        {
            // Properties - all string except cause
            "name" => StringType,
            "message" => StringType,
            "stack" => StringType,
            "cause" => new TypeInfo.Any(),

            // Methods
            "toString" => new TypeInfo.Function([], StringType),

            // AggregateError has an additional "errors" property
            "errors" when errorTypeName == "AggregateError" =>
                new TypeInfo.Array(new TypeInfo.Error()),

            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance methods on Map objects
    /// </summary>
    public static TypeInfo? GetMapMemberType(string name, TypeInfo keyType, TypeInfo valueType)
    {
        return name switch
        {
            "size" => NumberType,
            "get" => new TypeInfo.Function([keyType],
                new TypeInfo.Union([valueType, new TypeInfo.Null()])),
            "set" => new TypeInfo.Function([keyType, valueType],
                new TypeInfo.Map(keyType, valueType)),  // Returns this for chaining
            "has" => new TypeInfo.Function([keyType], BooleanType),
            "delete" => new TypeInfo.Function([keyType], BooleanType),
            "clear" => new TypeInfo.Function([], VoidType),
            "keys" => new TypeInfo.Function([],
                new TypeInfo.Iterator(keyType)),
            "values" => new TypeInfo.Function([],
                new TypeInfo.Iterator(valueType)),
            "entries" => new TypeInfo.Function([],
                new TypeInfo.Iterator(TypeInfo.Tuple.FromTypes([keyType, valueType], 2))),
            "forEach" => new TypeInfo.Function(
                [new TypeInfo.Function([valueType, keyType], VoidType)],
                VoidType),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance methods on Set objects
    /// </summary>
    public static TypeInfo? GetSetMemberType(string name, TypeInfo elementType)
    {
        var setType = new TypeInfo.Set(elementType);

        return name switch
        {
            "size" => NumberType,
            "add" => new TypeInfo.Function([elementType], setType),  // Returns this for chaining
            "has" => new TypeInfo.Function([elementType], BooleanType),
            "delete" => new TypeInfo.Function([elementType], BooleanType),
            "clear" => new TypeInfo.Function([], VoidType),
            "keys" => new TypeInfo.Function([],
                new TypeInfo.Iterator(elementType)),  // Same as values() for Set
            "values" => new TypeInfo.Function([],
                new TypeInfo.Iterator(elementType)),
            "entries" => new TypeInfo.Function([],
                new TypeInfo.Iterator(TypeInfo.Tuple.FromTypes([elementType, elementType], 2))),
            "forEach" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, elementType], VoidType)],
                VoidType),

            // ES2025 Set Operations
            "union" => new TypeInfo.Function([setType], setType),
            "intersection" => new TypeInfo.Function([setType], setType),
            "difference" => new TypeInfo.Function([setType], setType),
            "symmetricDifference" => new TypeInfo.Function([setType], setType),
            "isSubsetOf" => new TypeInfo.Function([setType], BooleanType),
            "isSupersetOf" => new TypeInfo.Function([setType], BooleanType),
            "isDisjointFrom" => new TypeInfo.Function([setType], BooleanType),

            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance methods on WeakMap objects.
    /// WeakMap has no size property and no iteration methods.
    /// </summary>
    public static TypeInfo? GetWeakMapMemberType(string name, TypeInfo keyType, TypeInfo valueType)
    {
        return name switch
        {
            "get" => new TypeInfo.Function([keyType],
                new TypeInfo.Union([valueType, new TypeInfo.Null()])),
            "set" => new TypeInfo.Function([keyType, valueType],
                new TypeInfo.WeakMap(keyType, valueType)),  // Returns this for chaining
            "has" => new TypeInfo.Function([keyType], BooleanType),
            "delete" => new TypeInfo.Function([keyType], BooleanType),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance methods on WeakSet objects.
    /// WeakSet has no size property and no iteration methods.
    /// </summary>
    public static TypeInfo? GetWeakSetMemberType(string name, TypeInfo elementType)
    {
        return name switch
        {
            "add" => new TypeInfo.Function([elementType],
                new TypeInfo.WeakSet(elementType)),  // Returns this for chaining
            "has" => new TypeInfo.Function([elementType], BooleanType),
            "delete" => new TypeInfo.Function([elementType], BooleanType),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance methods on WeakRef objects.
    /// WeakRef has only a deref() method.
    /// </summary>
    public static TypeInfo? GetWeakRefMemberType(string name, TypeInfo targetType)
    {
        return name switch
        {
            "deref" => new TypeInfo.Function([],
                new TypeInfo.Union([targetType, new TypeInfo.Undefined()])),
            _ => null
        };
    }

    /// <summary>
    /// FinalizationRegistry has register() and unregister() methods.
    /// </summary>
    public static TypeInfo? GetFinalizationRegistryMemberType(string name, TypeInfo targetType)
    {
        return name switch
        {
            "register" => new TypeInfo.Function([targetType, new TypeInfo.Any(), new TypeInfo.Any()],
                new TypeInfo.Undefined(), RequiredParams: 1),
            "unregister" => new TypeInfo.Function([new TypeInfo.Any()],
                new TypeInfo.Primitive(Parsing.TokenType.TYPE_BOOLEAN)),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for static members on the Symbol namespace
    /// </summary>
    public static TypeInfo? GetSymbolStaticMemberType(string name)
    {
        var symbolType = new TypeInfo.Symbol();

        return name switch
        {
            // Well-known symbols (properties returning symbol)
            "iterator" => symbolType,
            "asyncIterator" => symbolType,
            "toStringTag" => symbolType,
            "hasInstance" => symbolType,
            "isConcatSpreadable" => symbolType,
            "toPrimitive" => symbolType,
            "species" => symbolType,
            "unscopables" => symbolType,

            // Static methods
            "for" => new TypeInfo.Function([StringType], symbolType),
            "keyFor" => new TypeInfo.Function([symbolType],
                new TypeInfo.Union([StringType, new TypeInfo.Null()])),

            _ => null
        };
    }

    /// <summary>
    /// Type signatures for global timer functions (setTimeout, clearTimeout).
    /// </summary>
    public static TypeInfo? GetGlobalTimerFunctionType(string name)
    {
        var timeoutType = new TypeInfo.Timeout();

        return name switch
        {
            // setTimeout(callback: () => void, ms?: number, ...args: any[]): Timeout
            "setTimeout" => new TypeInfo.Function(
                [new TypeInfo.Function([], VoidType), NumberType],
                timeoutType,
                RequiredParams: 1  // callback is required, delay is optional (defaults to 0)
            ),

            // clearTimeout(handle?: Timeout): void
            "clearTimeout" => new TypeInfo.Function(
                [new TypeInfo.Union([timeoutType, new TypeInfo.Null(), new TypeInfo.Undefined()])],
                VoidType,
                RequiredParams: 0  // handle is optional (safe to call with null/undefined)
            ),

            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance members on Timeout objects.
    /// </summary>
    public static TypeInfo? GetTimeoutMemberType(string name)
    {
        var timeoutType = new TypeInfo.Timeout();

        return name switch
        {
            // ref(): Timeout - marks timeout as keeping program alive, returns this
            "ref" => new TypeInfo.Function([], timeoutType),

            // unref(): Timeout - marks timeout as NOT keeping program alive, returns this
            "unref" => new TypeInfo.Function([], timeoutType),

            // hasRef: boolean (property)
            "hasRef" => BooleanType,

            // toString(): string - inherited from Object
            "toString" => new TypeInfo.Function([], StringType),

            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance members on Function objects (bind, call, apply, length, name).
    /// </summary>
    /// <param name="name">The member name to look up</param>
    /// <param name="funcType">The function type being accessed</param>
    public static TypeInfo? GetFunctionMemberType(string name, TypeInfo funcType)
    {
        var returnType = funcType is TypeInfo.Function f ? f.ReturnType : AnyType;

        // For bind(), return a function that accepts any number of args and returns the original return type
        // This is permissive because we can't track bound arguments at compile time
        var boundFunctionType = new TypeInfo.Function(
            [new TypeInfo.Array(AnyType)],  // rest param for any args
            returnType,
            RequiredParams: 0,
            HasRestParam: true);

        return name switch
        {
            "length" => NumberType,
            "name" => StringType,
            "bind" => new TypeInfo.Function(
                [AnyType],               // thisArg, followed by optional bound args
                boundFunctionType,       // Returns a permissive function type
                RequiredParams: 0,
                HasRestParam: true),
            "call" => new TypeInfo.Function(
                [AnyType],               // thisArg, followed by spread args
                returnType,              // Returns the function's return type
                RequiredParams: 0,
                HasRestParam: true),
            "apply" => new TypeInfo.Function(
                [AnyType, new TypeInfo.Union([new TypeInfo.Array(AnyType), new TypeInfo.Null()])],
                returnType,              // Returns the function's return type
                RequiredParams: 0),
            // JS functions are objects — any arbitrary property is legal at
            // runtime (common in CommonJS: `fn.DNS = "..."`). Fall back to
            // Any for unknown names rather than erroring.
            _ => AnyType
        };
    }

    /// <summary>
    /// Type signatures for instance members on Buffer objects.
    /// </summary>
    public static TypeInfo? GetBufferMemberType(string name)
    {
        var bufferType = new TypeInfo.Buffer();

        return name switch
        {
            // Properties
            "length" => NumberType,

            // Methods
            "toString" => new TypeInfo.Function([StringType], StringType, RequiredParams: 0), // encoding optional
            "slice" => new TypeInfo.Function([NumberType, NumberType], bufferType, RequiredParams: 0), // start, end optional
            "copy" => new TypeInfo.Function(
                [bufferType, NumberType, NumberType, NumberType],
                NumberType,
                RequiredParams: 1), // target required, others optional
            "compare" => new TypeInfo.Function([bufferType], NumberType),
            "equals" => new TypeInfo.Function([bufferType], BooleanType),
            "fill" => new TypeInfo.Function(
                [AnyType, NumberType, NumberType, StringType],
                bufferType,
                RequiredParams: 1), // value required, others optional
            "write" => new TypeInfo.Function(
                [StringType, NumberType, NumberType, StringType],
                NumberType,
                RequiredParams: 1), // data required, others optional
            "readUInt8" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "writeUInt8" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "toJSON" => new TypeInfo.Function([], AnyType),

            // Multi-byte reads
            "readInt8" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readUInt16LE" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readUInt16BE" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readUInt32LE" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readUInt32BE" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readInt16LE" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readInt16BE" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readInt32LE" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readInt32BE" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readFloatLE" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readFloatBE" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readDoubleLE" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readDoubleBE" => new TypeInfo.Function([NumberType], NumberType, RequiredParams: 0),
            "readBigInt64LE" => new TypeInfo.Function([NumberType], BigIntType, RequiredParams: 0),
            "readBigInt64BE" => new TypeInfo.Function([NumberType], BigIntType, RequiredParams: 0),
            "readBigUInt64LE" => new TypeInfo.Function([NumberType], BigIntType, RequiredParams: 0),
            "readBigUInt64BE" => new TypeInfo.Function([NumberType], BigIntType, RequiredParams: 0),

            // Multi-byte writes
            "writeInt8" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeUInt16LE" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeUInt16BE" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeUInt32LE" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeUInt32BE" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeInt16LE" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeInt16BE" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeInt32LE" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeInt32BE" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeFloatLE" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeFloatBE" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeDoubleLE" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeDoubleBE" => new TypeInfo.Function([NumberType, NumberType], NumberType, RequiredParams: 1),
            "writeBigInt64LE" => new TypeInfo.Function([BigIntType, NumberType], NumberType, RequiredParams: 1),
            "writeBigInt64BE" => new TypeInfo.Function([BigIntType, NumberType], NumberType, RequiredParams: 1),
            "writeBigUInt64LE" => new TypeInfo.Function([BigIntType, NumberType], NumberType, RequiredParams: 1),
            "writeBigUInt64BE" => new TypeInfo.Function([BigIntType, NumberType], NumberType, RequiredParams: 1),

            // Search methods
            "indexOf" => new TypeInfo.Function([AnyType, NumberType, StringType], NumberType, RequiredParams: 1),
            "includes" => new TypeInfo.Function([AnyType, NumberType, StringType], BooleanType, RequiredParams: 1),

            // Swap methods
            "swap16" => new TypeInfo.Function([], bufferType),
            "swap32" => new TypeInfo.Function([], bufferType),
            "swap64" => new TypeInfo.Function([], bufferType),

            _ => null
        };
    }

    /// <summary>
    /// Type signatures for static methods on the Buffer namespace.
    /// </summary>
    public static TypeInfo? GetBufferStaticMethodType(string name)
    {
        var bufferType = new TypeInfo.Buffer();

        return name switch
        {
            "from" => new TypeInfo.Function(
                [new TypeInfo.Union([StringType, new TypeInfo.Array(NumberType), bufferType]), StringType],
                bufferType,
                RequiredParams: 1), // data required, encoding optional
            "alloc" => new TypeInfo.Function(
                [NumberType, AnyType, StringType],
                bufferType,
                RequiredParams: 1), // size required, fill and encoding optional
            "allocUnsafe" => new TypeInfo.Function([NumberType], bufferType),
            "allocUnsafeSlow" => new TypeInfo.Function([NumberType], bufferType),
            "concat" => new TypeInfo.Function(
                [new TypeInfo.Array(bufferType), NumberType],
                bufferType,
                RequiredParams: 1), // list required, totalLength optional
            "isBuffer" => new TypeInfo.Function([AnyType], BooleanType),
            "byteLength" => new TypeInfo.Function(
                [new TypeInfo.Union([StringType, bufferType]), StringType],
                NumberType,
                RequiredParams: 1), // string required, encoding optional
            "compare" => new TypeInfo.Function([bufferType, bufferType], NumberType),
            "isEncoding" => new TypeInfo.Function([StringType], BooleanType),

            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance members on Promise objects.
    /// </summary>
    public static TypeInfo? GetPromiseMemberType(string name, TypeInfo valueType)
    {
        var promiseAny = new TypeInfo.Promise(AnyType);

        return name switch
        {
            "then" => new TypeInfo.Function(
                [new TypeInfo.Union([new TypeInfo.Function([valueType], AnyType), new TypeInfo.Undefined()]),
                 new TypeInfo.Union([new TypeInfo.Function([AnyType], AnyType), new TypeInfo.Undefined()])],
                promiseAny,
                RequiredParams: 0),
            "catch" => new TypeInfo.Function(
                [new TypeInfo.Function([AnyType], AnyType)],
                promiseAny),
            "finally" => new TypeInfo.Function(
                [new TypeInfo.Function([], new TypeInfo.Union([VoidType, new TypeInfo.Promise(VoidType)]))],
                new TypeInfo.Promise(valueType)),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance members on EventEmitter objects.
    /// </summary>
    public static TypeInfo? GetEventEmitterMemberType(string name)
    {
        var eventEmitterType = new TypeInfo.EventEmitter();
        // Listener type is 'any' because EventEmitter callbacks are inherently untyped
        // and can have any number/type of parameters
        var listenerType = AnyType;

        return name switch
        {
            // Core event methods - all return 'this' for chaining (EventEmitter)
            "on" => new TypeInfo.Function([StringType, listenerType], eventEmitterType),
            "addListener" => new TypeInfo.Function([StringType, listenerType], eventEmitterType),
            "once" => new TypeInfo.Function([StringType, listenerType], eventEmitterType),
            "off" => new TypeInfo.Function([StringType, listenerType], eventEmitterType),
            "removeListener" => new TypeInfo.Function([StringType, listenerType], eventEmitterType),
            "emit" => new TypeInfo.Function(
                [StringType, new TypeInfo.Array(AnyType)],
                BooleanType,
                RequiredParams: 1,
                HasRestParam: true), // eventName required, ...args variadic
            "removeAllListeners" => new TypeInfo.Function([StringType], eventEmitterType, RequiredParams: 0),

            // Listener inspection
            "listeners" => new TypeInfo.Function([StringType], new TypeInfo.Array(listenerType)),
            "rawListeners" => new TypeInfo.Function([StringType], new TypeInfo.Array(listenerType)),
            "listenerCount" => new TypeInfo.Function([StringType], NumberType),
            "eventNames" => new TypeInfo.Function([], new TypeInfo.Array(StringType)),

            // Prepend methods
            "prependListener" => new TypeInfo.Function([StringType, listenerType], eventEmitterType),
            "prependOnceListener" => new TypeInfo.Function([StringType, listenerType], eventEmitterType),

            // Max listeners
            "setMaxListeners" => new TypeInfo.Function([NumberType], eventEmitterType),
            "getMaxListeners" => new TypeInfo.Function([], NumberType),

            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance members on AbortController objects.
    /// </summary>
    public static TypeInfo? GetAbortControllerMemberType(string name)
    {
        return name switch
        {
            "signal" => new TypeInfo.AbortSignal(),
            "abort" => new TypeInfo.Function([AnyType], VoidType, RequiredParams: 0),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance members on AbortSignal objects.
    /// </summary>
    public static TypeInfo? GetAbortSignalMemberType(string name)
    {
        var listenerType = new TypeInfo.Function([AnyType], VoidType);

        return name switch
        {
            "aborted" => BooleanType,
            "reason" => AnyType,
            "onabort" => new TypeInfo.Union([new TypeInfo.Function([], VoidType), new TypeInfo.Null()]),
            "throwIfAborted" => new TypeInfo.Function([], VoidType),
            "addEventListener" => new TypeInfo.Function([StringType, listenerType], VoidType),
            "removeEventListener" => new TypeInfo.Function([StringType, listenerType], VoidType),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for static methods on the AbortSignal namespace.
    /// </summary>
    public static TypeInfo? GetAbortSignalStaticMethodType(string name)
    {
        var signalType = new TypeInfo.AbortSignal();

        return name switch
        {
            "abort" => new TypeInfo.Function([AnyType], signalType, RequiredParams: 0),
            "timeout" => new TypeInfo.Function([NumberType], signalType),
            "any" => new TypeInfo.Function([new TypeInfo.Array(signalType)], signalType),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance members on Headers objects.
    /// </summary>
    public static TypeInfo? GetHeadersMemberType(string name)
    {
        return name switch
        {
            "get" => new TypeInfo.Function([StringType], new TypeInfo.Union([StringType, new TypeInfo.Null()])),
            "set" => new TypeInfo.Function([StringType, StringType], VoidType),
            "has" => new TypeInfo.Function([StringType], BooleanType),
            "delete" => new TypeInfo.Function([StringType], BooleanType),
            "append" => new TypeInfo.Function([StringType, StringType], VoidType),
            "forEach" => new TypeInfo.Function(
                [new TypeInfo.Function([StringType, StringType], VoidType)],
                VoidType),
            "entries" => new TypeInfo.Function([],
                new TypeInfo.Iterator(TypeInfo.Tuple.FromTypes([StringType, StringType], 2))),
            "keys" => new TypeInfo.Function([],
                new TypeInfo.Iterator(StringType)),
            "values" => new TypeInfo.Function([],
                new TypeInfo.Iterator(StringType)),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for static methods on the console namespace.
    /// All console methods are variadic and return void.
    /// </summary>
    public static TypeInfo? GetConsoleStaticMethodType(string name)
    {
        return name switch
        {
            // Phase 1: Output methods (variadic, return void)
            "log" => new TypeInfo.Function([new TypeInfo.Array(AnyType)], VoidType, RequiredParams: 0, HasRestParam: true),
            "info" => new TypeInfo.Function([new TypeInfo.Array(AnyType)], VoidType, RequiredParams: 0, HasRestParam: true),
            "debug" => new TypeInfo.Function([new TypeInfo.Array(AnyType)], VoidType, RequiredParams: 0, HasRestParam: true),
            "error" => new TypeInfo.Function([new TypeInfo.Array(AnyType)], VoidType, RequiredParams: 0, HasRestParam: true),
            "warn" => new TypeInfo.Function([new TypeInfo.Array(AnyType)], VoidType, RequiredParams: 0, HasRestParam: true),

            // Clear (no args)
            "clear" => new TypeInfo.Function([], VoidType),

            // Timing methods
            "time" => new TypeInfo.Function([StringType], VoidType, RequiredParams: 0),
            "timeEnd" => new TypeInfo.Function([StringType], VoidType, RequiredParams: 0),
            "timeLog" => new TypeInfo.Function([StringType, new TypeInfo.Array(AnyType)], VoidType, RequiredParams: 0, HasRestParam: true),

            // Phase 2: Assertion
            "assert" => new TypeInfo.Function([AnyType, new TypeInfo.Array(AnyType)], VoidType, RequiredParams: 0, HasRestParam: true),

            // Counting
            "count" => new TypeInfo.Function([StringType], VoidType, RequiredParams: 0),
            "countReset" => new TypeInfo.Function([StringType], VoidType, RequiredParams: 0),

            // Table (data required, columns optional)
            "table" => new TypeInfo.Function([AnyType, new TypeInfo.Array(StringType)], VoidType, RequiredParams: 1),

            // Dir (object required, options optional)
            "dir" => new TypeInfo.Function([AnyType, AnyType], VoidType, RequiredParams: 1),

            // Grouping
            "group" => new TypeInfo.Function([new TypeInfo.Array(AnyType)], VoidType, RequiredParams: 0, HasRestParam: true),
            "groupCollapsed" => new TypeInfo.Function([new TypeInfo.Array(AnyType)], VoidType, RequiredParams: 0, HasRestParam: true),
            "groupEnd" => new TypeInfo.Function([], VoidType),

            // Trace
            "trace" => new TypeInfo.Function([new TypeInfo.Array(AnyType)], VoidType, RequiredParams: 0, HasRestParam: true),

            _ => null
        };
    }

    /// <summary>
    /// Type signatures for instance members on Iterator objects (ES2025 Iterator Helpers).
    /// </summary>
    public static TypeInfo? GetIteratorMemberType(string name, TypeInfo elementType)
    {
        return name switch
        {
            // next accepts an optional value (ECMA-262 §27.5.1.2: the argument
            // becomes the result of the resumed yield expression).
            "next" => new TypeInfo.Function([AnyType], AnyType, RequiredParams: 0),
            "return" => new TypeInfo.Function([AnyType], AnyType, RequiredParams: 0),
            "throw" => new TypeInfo.Function([AnyType], AnyType, RequiredParams: 0),
            "map" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType], AnyType)],
                new TypeInfo.Iterator(AnyType)),
            "filter" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType], BooleanType)],
                new TypeInfo.Iterator(elementType)),
            "take" => new TypeInfo.Function([NumberType], new TypeInfo.Iterator(elementType)),
            "drop" => new TypeInfo.Function([NumberType], new TypeInfo.Iterator(elementType)),
            "flatMap" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType], AnyType)],
                new TypeInfo.Iterator(AnyType)),
            "reduce" => new TypeInfo.Function(
                [new TypeInfo.Function([AnyType, elementType], AnyType), AnyType],
                AnyType,
                RequiredParams: 1),
            "toArray" => new TypeInfo.Function([], new TypeInfo.Array(elementType)),
            "forEach" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType], VoidType)],
                VoidType),
            "some" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType], BooleanType)],
                BooleanType),
            "every" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType], BooleanType)],
                BooleanType),
            "find" => new TypeInfo.Function(
                [new TypeInfo.Function([elementType, NumberType], BooleanType)],
                new TypeInfo.Union([elementType, new TypeInfo.Undefined()])),
            _ => null
        };
    }

    /// <summary>
    /// Type signatures for static methods on the Iterator namespace.
    /// </summary>
    public static TypeInfo? GetIteratorStaticMethodType(string name)
    {
        return name switch
        {
            "from" => new TypeInfo.Function([AnyType], new TypeInfo.Iterator(AnyType)),
            _ => null
        };
    }
}
