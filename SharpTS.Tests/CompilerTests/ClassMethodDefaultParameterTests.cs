using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.CompilerTests;

/// <summary>
/// #705: in compiled mode, default parameters on class-declaration members were never applied —
/// the body emitters skipped the runtime entry prologue entirely, and value-type-defaulted params
/// kept an unboxed slot that cannot hold the <c>undefined</c> sentinel. This pins the fixes for the
/// <b>non-virtual</b> members from the issue's repros — constructors, private methods, and free
/// functions — whose value-type defaults are now widened to an object slot so an omitted or explicit
/// <c>undefined</c> argument fires the default (omit → default, explicit undefined → default, present
/// → used). Reference-type defaults on (virtual) instance / static methods also fire now, override-safely
/// (no slot change). Value-type defaults on virtual instance/static methods remain a tracked follow-up
/// (widening their slot would break override matching), and are intentionally not asserted here.
/// All run in both modes to pin interpreter/compiler parity.
/// </summary>
public class ClassMethodDefaultParameterTests
{
    // ---- Constructors (parameter properties, value-type + reference) -------

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Constructor_ParameterProperty_NumericDefault_Omitted(ExecutionMode mode)
    {
        var source = """
            class D { constructor(public name: string, public age: number = 99) {} }
            console.log(new D("Bob").age);
            """;
        Assert.Equal("99\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Constructor_ParameterProperty_NumericDefault_ExplicitUndefined(ExecutionMode mode)
    {
        var source = """
            class D { constructor(public name: string, public age: number = 99) {} }
            console.log(new D("Bob", undefined).age);
            """;
        Assert.Equal("99\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Constructor_ParameterProperty_NumericDefault_Present(ExecutionMode mode)
    {
        var source = """
            class D { constructor(public name: string, public age: number = 99) {} }
            console.log(new D("Bob", 7).age);
            """;
        Assert.Equal("7\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Constructor_StringDefault_OmittedArg(ExecutionMode mode)
    {
        var source = """
            class D { constructor(public name: string = "anon") {} }
            console.log(new D().name);
            """;
        Assert.Equal("anon\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Constructor_BooleanDefault_ExplicitUndefined(ExecutionMode mode)
    {
        var source = """
            class D { active: boolean; constructor(on: boolean = true) { this.active = on; } }
            console.log(new D(undefined).active);
            console.log(new D(false).active);
            """;
        Assert.Equal("true\nfalse\n", TestHarness.Run(source, mode));
    }

    // ---- Private methods (instance + static, value-type + reference) -------

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PrivateMethod_NumericDefault_OmittedArg(ExecutionMode mode)
    {
        var source = """
            class C {
              #add(a: number, b: number = 10): number { return a + b; }
              run(): number { return this.#add(5); }
            }
            console.log(new C().run());
            """;
        Assert.Equal("15\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PrivateMethod_NumericDefault_ExplicitUndefined(ExecutionMode mode)
    {
        var source = """
            class C {
              #add(a: number, b: number = 10): number { return a + b; }
              run(): number { return this.#add(5, undefined); }
            }
            console.log(new C().run());
            """;
        Assert.Equal("15\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PrivateMethod_MultipleDefaults_AllOmitted(ExecutionMode mode)
    {
        var source = """
            class C {
              #sum(a: number = 1, b: number = 2, c: number = 3): number { return a + b + c; }
              run(): number { return this.#sum(); }
            }
            console.log(new C().run());
            """;
        Assert.Equal("6\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PrivateMethod_PartialApplication_RemainingDefaults(ExecutionMode mode)
    {
        var source = """
            class C {
              #sum(a: number, b: number = 2, c: number = 3): number { return a + b + c; }
              run(): number { return this.#sum(10); }
            }
            console.log(new C().run());
            """;
        Assert.Equal("15\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StaticPrivateMethod_NumericDefault_OmittedArg(ExecutionMode mode)
    {
        var source = """
            class C {
              static #add(a: number, b: number = 10): number { return a + b; }
              static run(): number { return C.#add(5); }
            }
            console.log(C.run());
            """;
        Assert.Equal("15\n", TestHarness.Run(source, mode));
    }

    // ---- Free functions (value-type default + explicit undefined, #705 (a)) ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FreeFunction_NumericDefault_ExplicitUndefined(ExecutionMode mode)
    {
        var source = """
            function p(a: string, b: number = 2): number { return a.length + b; }
            console.log(p("x", undefined));
            """;
        Assert.Equal("3\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FreeFunction_NumericDefault_OmittedArg(ExecutionMode mode)
    {
        var source = """
            function p(a: string, b: number = 2): number { return a.length + b; }
            console.log(p("x"));
            """;
        Assert.Equal("3\n", TestHarness.Run(source, mode));
    }

    // ---- Reference-type defaults on (virtual) instance & static methods ----
    // These fire without changing the slot type, so override matching is preserved.

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InstanceMethod_StringDefault_OmittedArg(ExecutionMode mode)
    {
        var source = """
            class C { greet(name: string = "World"): string { return "Hi " + name; } }
            console.log(new C().greet());
            console.log(new C().greet("Bob"));
            """;
        Assert.Equal("Hi World\nHi Bob\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StaticMethod_StringDefault_OmittedArg(ExecutionMode mode)
    {
        var source = """
            class C { static greet(name: string = "World"): string { return "Hi " + name; } }
            console.log(C.greet());
            """;
        Assert.Equal("Hi World\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InstanceMethod_ArgPresent_DefaultNotApplied(ExecutionMode mode)
    {
        // Argument present: no default-firing needed; value-type slot stays fast and correct.
        var source = """
            class C { add(a: number, b: number = 10): number { return a + b; } }
            console.log(new C().add(5, 20));
            """;
        Assert.Equal("25\n", TestHarness.Run(source, mode));
    }

    // ---- Override safety: widening must NOT change a virtual method's signature ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Override_DerivedAddsDefault_StillOverrides(ExecutionMode mode)
    {
        // Regression guard: a derived override that adds a default to a value-type param the base
        // declares as required must still override (a base-typed call dispatches to the derived
        // method). Widening the derived param to object would silently break this. (#705)
        var source = """
            class B { m(x: number): number { return x; } }
            class D extends B { m(x: number = 5): number { return x * 2; } }
            const b: B = new D();
            console.log(b.m(3));
            """;
        Assert.Equal("6\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Override_BothDefault_DispatchesToDerived(ExecutionMode mode)
    {
        var source = """
            class B { greet(n: string = "B"): string { return "B:" + n; } }
            class D extends B { greet(n: string = "D"): string { return "D:" + n; } }
            const b: B = new D();
            console.log(b.greet());
            """;
        Assert.Equal("D:D\n", TestHarness.Run(source, mode));
    }

    // ---- IL verification guard ---------------------------------------------

    [Fact]
    public void NonVirtualValueTypeDefaults_ProduceVerifiableIL()
    {
        // Widening defaulted value-type params to object (free fn / private / ctor) and padding
        // private calls must emit verifiable IL — no `ldarg; brfalse` on a double/bool slot, no
        // unbalanced private-call stack.
        var source = """
            class C {
              #p(a: number, b: number = 7): number { return a + b; }
              run(): number { return this.#p(1); }
            }
            class D { constructor(public age: number = 99) {} }
            function f(x: number, y: number = 3): number { return x + y; }
            console.log(new C().run() + new D().age + f(1));
            """;
        var errors = TestHarness.CompileAndVerifyOnly(source);
        Assert.Empty(errors);
    }
}
