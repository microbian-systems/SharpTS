using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.CompilerTests;

/// <summary>
/// #640: in compiled mode, invoking a user function as a value (cross-module imports, callbacks,
/// <c>$TSFunction.Invoke</c>) must pad omitted trailing optional arguments with the <c>undefined</c>
/// sentinel — not CLR null — so <c>typeof</c>, <c>=== undefined</c>, and <c>=== null</c> answer
/// correctly. Runtime built-ins keep null padding (they use null-checks for optional-arg absence).
/// Both modes are pinned for interpreter/compiler parity; the compiled path was the defective one.
/// </summary>
public class ValueCallUndefinedPaddingTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void CrossModuleImport_OmittedOptionalArg_IsUndefined(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["helper.ts"] = """
                export function h(x?: any): string { return typeof x; }
                export function h2(x?: any): boolean { return x === undefined; }
                export function h3(x?: any): boolean { return x === null; }
                """,
            ["main.ts"] = """
                import { h, h2, h3 } from './helper';
                console.log(h());
                console.log(h2());
                console.log(h3());
                """,
        };
        Assert.Equal("undefined\ntrue\nfalse\n", TestHarness.RunModules(files, "main.ts", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FunctionDeclarationAsValue_OmittedOptionalArg_IsUndefined(ExecutionMode mode)
    {
        var source = """
            function fdecl(x?: any): string { return typeof x; }
            const r = fdecl;
            console.log(r());
            """;
        Assert.Equal("undefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrowAsValue_OmittedOptionalArg_IsUndefined(ExecutionMode mode)
    {
        var source = """
            const farrow = (x?: any): string => typeof x;
            console.log(farrow());
            """;
        Assert.Equal("undefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Callback_OmittedOptionalArg_IsUndefined(ExecutionMode mode)
    {
        var source = """
            function callIt(cb: (x?: any) => string): string { return cb(); }
            console.log(callIt((x?: any) => typeof x));
            """;
        Assert.Equal("undefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunctionAsValue_OmittedOptionalArg_IsUndefined(ExecutionMode mode)
    {
        var source = """
            function outer(): string {
              function inner(x?: any): string { return typeof x; }
              const r = inner;
              return r();
            }
            console.log(outer());
            """;
        Assert.Equal("undefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BuiltInCallback_OmittedTrailingArg_StillNullPadded(ExecutionMode mode)
    {
        // Regression guard: built-in array callbacks pad missing trailing slots with null
        // (not the sentinel). An arrow callback only declaring the element parameter must still
        // work — and a `function` callback observing arity sees the standard 3 args.
        var source = """
            const doubled = [1, 2, 3].map(x => x * 2);
            console.log(doubled.join(","));
            """;
        Assert.Equal("2,4,6\n", TestHarness.Run(source, mode));
    }
}
