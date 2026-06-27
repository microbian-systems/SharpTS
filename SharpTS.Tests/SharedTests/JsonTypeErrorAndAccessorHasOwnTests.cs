using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regressions surfaced by the deterministic interpreted Test262 baseline
/// (#964 follow-up).
///
/// Two independent interpreter bugs:
///   1. JSON.stringify detected circular structures and BigInt values but threw
///      a bare string ("TypeError: …") via ThrowException, so guest `catch`
///      received a string and `e instanceof TypeError` was false — failing every
///      Test262 `assert.throws(TypeError, …)` for these cases. Fixed by throwing
///      a real SharpTSTypeError (Runtime/BuiltIns/JSONBuiltIns.cs), matching the
///      pattern already used elsewhere (Interpreter.Properties, ArrayStaticBuiltIns).
///   2. SharpTSObject.HasProperty ignored setter-only accessors, so a property
///      defined via Object.defineProperty(o, p, {set}) was invisible to
///      hasOwnProperty / `in`. Fixed by also consulting the setter table.
///
/// Compiled mode already threw proper TypeErrors and tracked accessors, so each
/// case is asserted in BOTH modes to pin the now-converged behavior. The guest
/// objects are typed `any` because these exercise dynamic-JS patterns (expando
/// assignment, toJSON) that the TestHarness type-checker would otherwise reject.
/// </summary>
public class JsonTypeErrorAndAccessorHasOwnTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void JsonStringify_CircularArray_ThrowsRealTypeError(ExecutionMode mode)
    {
        var source = @"
            var a: any[] = [];
            a.push(a);
            try { JSON.stringify(a); console.log('no-throw'); }
            catch (e) { console.log(e instanceof TypeError); }
        ";
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void JsonStringify_CircularViaToJson_ThrowsRealTypeError(ExecutionMode mode)
    {
        var source = @"
            var obj: any = {};
            var circular: any = { prop: obj };
            obj.toJSON = function () { return circular; };
            try { JSON.stringify(circular); console.log('no-throw'); }
            catch (e) { console.log(e instanceof TypeError); }
        ";
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void JsonStringify_BigInt_ThrowsRealTypeError(ExecutionMode mode)
    {
        var source = @"
            try { JSON.stringify(0n); console.log('no-throw'); }
            catch (e) { console.log(e instanceof TypeError); }
        ";
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void HasOwnProperty_SetterOnlyAccessor_ReturnsTrue(ExecutionMode mode)
    {
        var source = @"
            var o: any = {};
            var data = 'd';
            Object.defineProperty(o, 'p', { set: function (v: any) { data = v; } });
            o.p = 'ov';
            console.log(o.hasOwnProperty('p') + ',' + data);
        ";
        // The accessor is an own property (hasOwnProperty true) and its setter
        // runs on assignment (data updated).
        Assert.Equal("true,ov\n", TestHarness.Run(source, mode));
    }

    // Interpreted-only: the HasProperty fix also makes the `in` operator see a
    // setter-only accessor. Compiled mode has a separate object/property model
    // whose `in` operator does NOT yet consult setter-only accessors (even though
    // compiled hasOwnProperty does) — a distinct, pre-existing compiled-mode gap
    // left as a follow-up, out of scope for these interpreter regressions.
    [Fact]
    public void InOperator_SetterOnlyAccessor_ReturnsTrue_Interpreted()
    {
        var source = @"
            var o: any = {};
            Object.defineProperty(o, 'p', { set: function (v: any) {} });
            console.log('p' in o);
        ";
        Assert.Equal("true\n", TestHarness.Run(source, ExecutionMode.Interpreted));
    }
}
