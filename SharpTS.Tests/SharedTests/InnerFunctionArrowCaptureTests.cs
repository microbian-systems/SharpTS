using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #307: inner function declarations nested inside
/// arrow functions / function expressions must capture enclosing locals by
/// live reference, not by value-snapshot taken at hoist time. Function
/// declarations hoist to the top of the enclosing body — before the
/// statements that assign the captured variables run — so snapshots read
/// null/stale. Compiled mode routes these captures through a live
/// $arrowScopeDC reference to the enclosing arrow's scope display class
/// (threaded through intermediate scopes, with extra per-source references
/// when captures span multiple ancestor arrow scopes). Root cause of the
/// lodash init failure (#302).
/// </summary>
public class InnerFunctionArrowCaptureTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FunctionDecl_InArrow_ReadsConstAssignedAfterHoist(ExecutionMode mode)
    {
        // Minimal repro from #307: bgt is hoisted before `const Obj = 42` runs.
        var source = """
            const ric = () => {
              const Obj = 42;
              function bgt() { return Obj; }
              return bgt;
            };
            console.log(ric()());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FunctionDecl_GrandparentArrowCapture_ThroughIntermediateArrow(ExecutionMode mode)
    {
        var source = """
            const outer = () => {
              const g = "grand";
              const mid = () => {
                function leaf() { return g; }
                return leaf;
              };
              return mid();
            };
            console.log(outer()());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("grand\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FunctionDecl_GrandparentArrowCapture_ThroughIntermediateFunctionDecl(ExecutionMode mode)
    {
        // The intermediate scope is a function DECLARATION: the live reference
        // must pass through its display class ($arrowScopeDC pass-through).
        var source = """
            const outer = () => {
              const g = "grand2";
              function mid() {
                function leaf() { return g; }
                return leaf;
              }
              return mid();
            };
            console.log(outer()());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("grand2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FunctionDecl_InArrow_MutationsVisibleBothWays(ExecutionMode mode)
    {
        // Writes from the arrow body must be visible inside the function
        // declaration, and the function's writes must be visible outside.
        var source = """
            const outer = () => {
              let counter = 0;
              function bump() { counter = counter + 1; return counter; }
              counter = 10;
              bump();
              bump();
              return counter;
            };
            console.log(outer());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("12\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FunctionDecls_InFunctionExpression_PeerReferences(ExecutionMode mode)
    {
        // lodash idiom: hoisted peers referencing each other before their
        // textual position, inside a function-expression wrapper.
        var source = """
            const outer = function() {
              function getValue(o: any, k: string) { return o == null ? undefined : o[k]; }
              function getNative(o: any, k: string) { const v = getValue(o, k); return v; }
              var nativeCreate = getNative({create: 99}, 'create');
              return nativeCreate;
            };
            console.log(outer());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("99\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FunctionDecl_HoistedBeforeVarAssignment_CalledAfter(ExecutionMode mode)
    {
        var source = """
            const outer = function() {
              var dep: any;
              function reader() { return dep; }
              dep = "assigned-later";
              return reader();
            };
            console.log(outer());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("assigned-later\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Closure_CapturesFromTwoAncestorArrowScopes(ExecutionMode mode)
    {
        // lodash shortOut shape: the returned closure captures `tag` from the
        // OUTER wrapper and `nativeNow` from the inner function expression —
        // two distinct ancestor arrow scopes, both assigned after the points
        // where snapshots would have been taken. Requires the extra
        // (multi-source) scope DC references.
        var source = """
            const result = (function() {
              var tag: any;
              var run = (function run(context: any) {
                var nativeNow: any;
                function shortOut(func: any) {
                  return function() {
                    return func(nativeNow()) + "/" + tag;
                  };
                }
                var wrapped = shortOut(function(x: any) { return "v" + x; });
                nativeNow = function() { return 7; };
                return wrapped;
              });
              tag = "T";
              return run(null)();
            })();
            console.log(result);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("v7/T\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void CapturedParameter_ReassignedInBody_ReadsStayCurrent(ExecutionMode mode)
    {
        // lodash runInContext shape: a captured parameter is reassigned at the
        // top of the body; later reads in the SAME body must see the new value
        // (stores go to the scope DC — the arg slot must stay in sync).
        var source = """
            const run = function(context: any) {
              context = context == null ? { Date: "real" } : context;
              var d = context.Date;
              function reader() { return context.Date; }
              return d + "/" + reader();
            };
            console.log(run(null));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("real/real\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ValueForm_ObjectCall_Coerces(ExecutionMode mode)
    {
        // lodash overArg(Object.keys, Object): `Object` held as a value and
        // invoked as a function (ToObject). Objects pass through unchanged.
        var source = """
            var transform: any = Object;
            var keysFn: any = Object.keys;
            function overArg(func: any, t: any) { return function(arg: any) { return func(t(arg)); }; }
            var nativeKeys = overArg(keysFn, transform);
            console.log(nativeKeys({a: 1, b: 2}).length);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ValueForm_ObjectCreate_SingleArg(ExecutionMode mode)
    {
        // Value-form Object.create(proto) under-application: the padded props
        // argument must read as ABSENT/undefined, not as JS null (which throws
        // per ECMA-262 §20.1.2.2 step 3).
        var source = """
            var oc: any = Object.create;
            var p = { x: 1 };
            var o = oc(p);
            console.log(o.x);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ValueForm_DateNow_AsValue(ExecutionMode mode)
    {
        // lodash: `var nativeNow = Date.now;` then nativeNow() — value-form
        // static member access on the Date constructor.
        var source = """
            var d0 = new Date();
            var nativeNow: any = Date.now;
            console.log(typeof nativeNow);
            console.log(nativeNow() > 0);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("function\ntrue\n", output);
    }
}
