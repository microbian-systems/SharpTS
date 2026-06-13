using SharpTS.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SharpTS.Tests.CompilerTests;

/// <summary>
/// Tests that verify compiled assemblies produce valid IL.
/// These tests catch IL generation bugs at test time rather than runtime.
/// </summary>
public class ILVerificationTests
{
    [Fact]
    public void BasicArithmetic_PassesILVerification()
    {
        var source = """
            let x = 10 + 5;
            console.log(x);
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("15\n", output);
    }

    [Fact]
    public void ClassWithMethods_PassesILVerification()
    {
        var source = """
            class Calculator {
                add(a: number, b: number): number {
                    return a + b;
                }
            }
            let calc = new Calculator();
            console.log(calc.add(3, 4));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("7\n", output);
    }

    [Fact]
    public void AsyncAwait_PassesILVerification()
    {
        var source = """
            async function getValue(): Promise<number> {
                return 42;
            }

            async function main() {
                let result = await getValue();
                console.log(result);
            }

            main();
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("42\n", output);
    }

    [Fact]
    public void Closures_PassesILVerification()
    {
        var source = """
            function makeCounter(): () => number {
                let count = 0;
                return () => {
                    count = count + 1;
                    return count;
                };
            }

            let counter = makeCounter();
            console.log(counter());
            console.log(counter());
            """;

        // For now, just verify IL without running (runtime has issues with rewritten assemblies)
        var errors = TestHarness.CompileAndVerifyOnly(source);

        Assert.Empty(errors);
    }

    [Fact]
    public void Inheritance_PassesILVerification()
    {
        var source = """
            class Animal {
                speak(): string {
                    return "...";
                }
            }

            class Dog extends Animal {
                speak(): string {
                    return "Woof!";
                }
            }

            let dog = new Dog();
            console.log(dog.speak());
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("Woof!\n", output);
    }

    [Fact]
    public void DerivedConstructorWithSuper_PassesILVerification()
    {
        // A subclass with an EXPLICIT constructor calling super(...) emitted a
        // base-ctor `call` (Object..ctor) before super() chained the real parent
        // ctor, so the verifier saw a base-ctor call on an already-initialized /
        // wrong-base `this` — CallCtor. The program ran correctly; the IL was
        // merely unverifiable. Now the Object..ctor is skipped when super() will
        // chain the real base. Covers no-arg and arg'd bases, parameter
        // properties, multi-level chains, and generic bases. (#287)
        var source = """
            class A0 { constructor() {} }
            class B0 extends A0 { constructor() { super(); } }
            class A1 { constructor(public x: number) {} }
            class B1 extends A1 { y: number; constructor() { super(5); this.y = 9; } }
            class C1 extends B1 { constructor() { super(); } }
            class Box<T> { constructor(public v: T) {} }
            class IntBox extends Box<number> { constructor() { super(42); } }
            console.log(new B0() instanceof A0);
            const b1 = new B1();
            console.log(b1.x, b1.y);
            const c1 = new C1();
            console.log(c1.x, c1.y);
            console.log(new IntBox().v);
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("true\n5 9\n5 9\n42\n", output);
    }

    [Fact]
    public void ClassExpressionExtendsClassExpression_PassesILVerification()
    {
        // A class expression extending ANOTHER class expression resolved its
        // superclass by the generated $ClassExpr_N name instead of the source
        // variable name, so the parent type was never set — the child silently
        // extended System.Object while super() still chained the real parent
        // ctor, tripping CallCtor / ThisUninitReturn, and `instanceof` against
        // the parent returned false. With the parent type now set, the IL
        // verifies and instanceof is correct. (#296)
        var source = """
            const Animal = class {
                constructor(public name: string) {}
                speak(): string { return this.name + " makes a sound"; }
            };
            const Dog = class extends Animal {
                constructor(name: string) { super(name); }
                speak(): string { return this.name + " barks"; }
            };
            const a = new Animal("Generic");
            const d = new Dog("Fido");
            console.log(a.speak());
            console.log(d.speak(), d instanceof Animal);
            """;

        // Verify-only: the reference-assembly rewrite in CompileVerifyAndRun
        // mangles the ldtoken-derived MethodInfo used by class-expression method
        // dispatch (BadImageFormatException at run time). Runtime behavior of
        // class-expression inheritance is covered by the shared-mode tests in
        // ClassExpressionTests, which run the real compiled output.
        var errors = TestHarness.CompileAndVerifyOnly(source);

        Assert.Empty(errors);
    }

    [Fact]
    public void ClassExpressionInheritedMethodDispatch_PassesILVerification()
    {
        // The compiled per-class GetProperty helper only dispatched a class's
        // OWN members, so a method inherited from a base class resolved to
        // undefined under the (always dynamic) class-expression dispatch and the
        // call threw. GetProperty now delegates to the base class. Three-level
        // chain exercises recursive delegation: Puppy inherits Dog.speak. (#297)
        var source = """
            const Animal = class {
                constructor(public name: string) {}
                speak(): string { return this.name + " makes a sound"; }
            };
            const Dog = class extends Animal {
                constructor(name: string) { super(name); }
                speak(): string { return this.name + " barks"; }
            };
            const Puppy = class extends Dog {
                constructor() { super("Rex"); }
            };
            const p = new Puppy();
            console.log(p.speak(), p.name, p instanceof Dog, p instanceof Animal);
            """;

        // Verify-only (see ClassExpressionExtendsClassExpression note). Runtime
        // inherited-dispatch behavior is asserted by the shared-mode test
        // ClassExpressionTests.ClassExpression_MultiLevelInheritedMethod.
        var errors = TestHarness.CompileAndVerifyOnly(source);

        Assert.Empty(errors);
    }

    [Fact]
    public void Generators_PassesILVerification()
    {
        var source = """
            function* range(start: number, end: number) {
                for (let i = start; i < end; i = i + 1) {
                    yield i;
                }
            }

            for (let n of range(1, 4)) {
                console.log(n);
            }
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("1\n2\n3\n", output);
    }

    // Regression test for issue #189: CLI `--compile … --verify` output is NOT
    // rewritten to reference-assembly facades (it binds to System.Private.CoreLib),
    // and verifying it against the ref-assembly universe produced thousands of
    // false StackUnexpected/ThrowOrCatchOnlyExceptionType errors.
    [Fact]
    public void CoreLibBoundOutput_PassesILVerification()
    {
        var source = """
            let arr = [1, 2, 3];
            let doubled = arr.map(x => x * 2);
            console.log(doubled);
            """;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sharpts_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dllPath = Path.Combine(tempDir, "test.dll");

            var lexer = new SharpTS.Parsing.Lexer(source);
            var tokens = lexer.ScanTokens();
            var parser = new SharpTS.Parsing.Parser(tokens);
            var statements = parser.ParseOrThrow();

            var checker = new SharpTS.TypeSystem.TypeChecker();
            var typeMap = checker.Check(statements);

            var deadCodeAnalyzer = new SharpTS.Compilation.DeadCodeAnalyzer(typeMap);
            var deadCodeInfo = deadCodeAnalyzer.Analyze(statements);

            // useReferenceAssemblies: false — same shape as plain CLI --compile output
            var compiler = new SharpTS.Compilation.ILCompiler("test", preserveConstEnums: false, useReferenceAssemblies: false, sdkPath: null);
            compiler.Compile(statements, typeMap, deadCodeInfo);
            compiler.Save(dllPath);

            var errors = TestHarness.VerifyIL(dllPath);

            Assert.Empty(errors);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore cleanup errors */ }
        }
    }

    [Fact]
    public void FunctionReturningStringConcat_PassesILVerification()
    {
        // A `: string` function whose body produces a runtime-helper result (string concat
        // or member get) previously left an `object` on the stack where the verifier expected
        // a string, raising StackUnexpected even though it ran correctly. (#275)
        var source = """
            function cat(a: string): string { return "hi " + a; }
            function viaLocal(a: string): string { let r = "x" + a; return r; }
            function withNumber(a: number): string { return "val=" + a; }
            interface Named { name: string; }
            function pick(n: Named): string { return n.name; }
            console.log(cat("z"));
            console.log(viaLocal("y"));
            console.log(withNumber(42));
            console.log(pick({ name: "w" }));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("hi z\nxy\nval=42\nw\n", output);
    }

    [Fact]
    public void FunctionReturningTypedCollection_PassesILVerification()
    {
        // A function declared `: number[]` (or Map/Set, or async Promise<T[]>) maps its return
        // slot to List<T>/Dictionary<,>/HashSet<>, but the runtime value is a dynamic
        // $Array/TSMap/TSSet carried as object — not CLR-assignable to the declared collection.
        // That left an object on the stack where the verifier expected the collection, raising
        // StackUnexpected even though it ran correctly. The return type now falls back to object. (#278)
        var source = """
            function nums(): number[] { return [1, 2, 3]; }
            function strs(): string[] { return ["a", "b"]; }
            function mkMap(): Map<string, number> { const m = new Map<string, number>(); m.set("x", 1); return m; }
            function mkSet(): Set<number> { const s = new Set<number>(); s.add(5); return s; }
            class Box { getNums(): number[] { return [7, 8]; } }
            console.log(nums().length);
            console.log(strs().join(","));
            console.log(mkMap().get("x"));
            console.log(mkSet().has(5));
            console.log(new Box().getNums().length);
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("3\na,b\n1\ntrue\n2\n", output);
    }

    [Fact]
    public void ClassGetPropertyWithTypedGetter_PassesILVerification()
    {
        // The compiler-generated GetProperty dispatch helper invokes a typed getter (e.g. the
        // auto getter for a `public x: number` parameter property, or an explicit `get`) and
        // returned its value-type result into GetProperty's object slot without boxing — the
        // verifier reported StackUnexpected even though it ran. Generic-class getters returning a
        // type parameter had the same gap. The getter result is now boxed. (#279)
        var source = """
            class Foo { constructor(public x: number) {} }
            class Bar {
                private _v: number = 10;
                get doubled(): number { return this._v * 2; }
                get label(): string { return "bar"; }
                get flag(): boolean { return true; }
            }
            class Box<T> {
                constructor(public item: T) {}
                get value(): T { return this.item; }
            }
            function mkFoo(): Foo { return new Foo(7); }
            const b = new Bar();
            console.log(mkFoo().x);
            console.log(b.doubled);
            console.log(b.label);
            console.log(b.flag);
            console.log(new Box<number>(99).value);
            console.log(new Box<string>("hi").value);
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("7\n20\nbar\ntrue\n99\nhi\n", output);
    }

    [Fact]
    public void GenericClassExpression_PassesILVerification()
    {
        // A generic class EXPRESSION emits a real open .NET generic type. Its $IHasFields
        // bodies (get_Fields / GetProperty / SetProperty) widened a generic-parameter-typed
        // backing field (`__Value : T`) into the object-typed dispatch slots without boxing,
        // producing unverifiable IL (StackUnexpected). Generic-parameter fields now live in the
        // `_fields` dictionary, matching generic class declarations, and the open generic is
        // closed (via inference / explicit type args) before Newobj at the `new` site. (#291)
        var source = """
            const Box = class<T> {
                constructor(private value: T) {}
                get contents(): T { return this.value; }
            };
            console.log(new Box("hello").contents);
            console.log(new Box<number>(42).contents);
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("hello\n42\n", output);
    }

    [Fact]
    public void TryCatchFinally_PassesILVerification()
    {
        var source = """
            function test(): string {
                try {
                    throw "test error";
                } catch (e) {
                    return "caught";
                } finally {
                    console.log("finally");
                }
            }

            console.log(test());
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("finally\ncaught\n", output);
    }

    // #318: a `string`-inferred function body can legitimately return `$Undefined`
    // (the checker infers `string` for `cond ? "x" : undefined`). A narrow `string`
    // return slot cannot carry that sentinel — the prior #275 castclass crashed at
    // runtime with InvalidCastException. ResolveReturnType now maps string->object.
    [Fact]
    public void InferredStringReturn_WithUndefinedBranch_BlockBody_DoesNotCrash()
    {
        var source = """
            function pick(n: any) {
                return n > 2 ? "big" : undefined;
            }
            console.log(pick(3));
            console.log(pick(1));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("big\nundefined\n", output);
    }

    // #318: expression-body arrow whose `string` return loads a captured variable
    // through a display class leaves a statically-object value against the slot,
    // which previously failed IL verification (StackUnexpected). No castclass is
    // safe here (it would crash on $Undefined), so the slot is dropped to object.
    // Verified separately from run: reference-assembly compilation (used by
    // CompileVerifyAndRun) trips a distinct, pre-existing $TSFunction.ctor reflection
    // bug for this captured-var arrow shape — see #343.
    [Fact]
    public void InferredStringReturn_CapturedVarArrow_PassesILVerification()
    {
        var source = """
            const outer = () => {
                let v = "a";
                const setB = () => { v = v + "b"; };
                const readV = () => v;
                setB();
                return readV();
            };
            console.log(outer());
            """;

        var errors = TestHarness.CompileAndVerifyOnly(source);
        Assert.Empty(errors);

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("ab\n", output);
    }

    // #318 guard: an undefined string-keyed group must stay `undefined` (not be
    // coerced to null or "undefined"), which an isinst/Convert.ToString coercion
    // at the return site would have corrupted — observable through Map keys.
    [Fact]
    public void InferredStringReturn_UndefinedMapKey_PreservedDoesNotCoerce()
    {
        var source = """
            const items = [1, 2, 3, 4];
            const g = Map.groupBy(items, (n: any) => n > 2 ? "big" : undefined);
            console.log(g.get("big"));
            console.log(g.get(undefined));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("[3, 4]\n[1, 2]\n", output);
    }

    // #318 guard: a genuinely string-typed function (no undefined in its value
    // domain) still verifies and returns the right string after the slot change.
    [Fact]
    public void TypedStringReturn_StillWorks()
    {
        var source = """
            function greet(name: string): string {
                return "hello " + name;
            }
            const up = (s: string): string => s.toUpperCase();
            console.log(greet("world"));
            console.log(up("abc"));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("hello world\nABC\n", output);
    }

    // #344: a `: number` function whose body returns `undefined as any` would, with an
    // unboxed `double` return slot, coerce the `undefined` sentinel to `NaN`. The checker
    // flags the undefined-reachable return so the compiler widens the slot to object,
    // matching the interpreter (`undefined`).
    [Fact]
    public void TypedNumberReturn_UndefinedAsAny_StaysUndefined()
    {
        var source = """
            function f(n: any): number {
                if (n > 2) return 42;
                return undefined as any;
            }
            console.log(f(3));
            console.log(f(1));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("42\nundefined\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #344: the boolean analogue — an unboxed `bool` slot would coerce `undefined` to `false`.
    [Fact]
    public void TypedBooleanReturn_UndefinedAsAny_StaysUndefined()
    {
        var source = """
            function g(n: any): boolean {
                if (n > 2) return true;
                return undefined as any;
            }
            console.log(g(3));
            console.log(g(1));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("true\nundefined\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #344: an expression-bodied arrow with a `: number` return whose ternary hides the
    // `undefined` in a branch (`42 | any` collapses to `42` at the top level). The value-flow
    // check recurses into ternary branches so the slot is still widened.
    [Fact]
    public void TypedNumberArrow_TernaryUndefinedBranch_StaysUndefined()
    {
        var source = """
            const af = (n: any): number => (n > 2 ? 42 : (undefined as any));
            console.log(af(3));
            console.log(af(1));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("42\nundefined\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #344 guard: a genuinely numeric/boolean function (no undefined in its value domain)
    // keeps its sound unboxed slot — it must still verify and compute correctly. `area(2)+1`
    // consumes the return numerically, exercising the unboxed double path.
    [Fact]
    public void TypedNumericReturn_SoundBody_StillWorks()
    {
        var source = """
            function add(a: number, b: number): number { return a + b; }
            function area(r: number): number { return r * r * 3; }
            function isBig(n: number): boolean { return n > 10; }
            console.log(add(2, 3));
            console.log(area(2) + 1);
            console.log(isBig(5));
            console.log(isBig(20));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("5\n13\nfalse\ntrue\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #367: a `: number` LOCAL unsoundly assigned `undefined as any` and then returned. The
    // return expression is statically `number`, so the #344 detection (which keys on the return
    // value's own type) cannot see it. The taint pass object-slots the local so the unboxed
    // double slot does not coerce the sentinel to NaN, matching the interpreter (`undefined`).
    [Fact]
    public void TypedNumberLocal_HoldingUndefined_ReturnedStaysUndefined()
    {
        var source = """
            function h(n: any): number {
                let x: number = undefined as any;
                if (n > 2) x = 42;
                return x;
            }
            console.log(h(1));
            console.log(h(5));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("undefined\n42\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #367: taint reaches the returned local transitively (`z = y` where `y` holds the sentinel).
    // The numeric-typed initializer of `z` would otherwise give it an unboxed double slot that
    // coerces `undefined` to NaN at the store, before the return is even reached.
    [Fact]
    public void TypedNumberLocal_TransitiveTaint_StaysUndefined()
    {
        var source = """
            function trans(): number {
                let y: number = undefined as any;
                let z: number = y;
                return z;
            }
            console.log(trans());
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("undefined\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #367: the taint assignment is lexically AFTER the return that observes it, reachable only
    // via a loop back-edge. The whole-body taint pass is order-independent, so the earlier return
    // is still flagged and the local object-slotted.
    [Fact]
    public void TypedNumberLocal_LoopBackEdgeTaint_StaysUndefined()
    {
        var source = """
            function loop(n: number): number {
                let x: number = 0;
                for (let i = 0; i < n; i++) {
                    if (i > 0) return x;
                    x = undefined as any;
                }
                return 7;
            }
            console.log(loop(2));
            console.log(loop(0));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("undefined\n7\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #367: the same local-slot corruption inside a class method and getter (their `return`
    // already uses an object slot, but the unboxed local would coerce the sentinel first).
    [Fact]
    public void TypedNumberLocal_InMethodAndGetter_StaysUndefined()
    {
        var source = """
            class C {
                m(): number { let x: number = 0; x = undefined as any; return x; }
                get g(): number { let y: number = 1; y = undefined as any; return y; }
            }
            const c = new C();
            console.log(c.m());
            console.log(c.g);
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("undefined\nundefined\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #367 guard: a `: number` local that never receives an `any`/`undefined` value keeps its
    // sound unboxed double slot and computes correctly — the taint pass must not over-widen.
    [Fact]
    public void TypedNumberLocal_SoundBody_StillUsesNumericValue()
    {
        var source = """
            function clean(n: number): number {
                let x: number = 5;
                if (n > 2) x = 42;
                return x + 1;
            }
            console.log(clean(1));
            console.log(clean(10));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("6\n43\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #372 case 1: an inferred-return (no annotation) function whose returned local holds the
    // undefined sentinel. The return type is inferred as `number`, so it is double-slotted just like
    // an explicit `: number` — but the #367 taint pass was gated on a declared number/boolean return
    // and never ran here. The pass now runs regardless of return type, so the local and the inferred
    // return slot are both widened to object and the sentinel survives.
    [Fact]
    public void TypedNumberLocal_InferredReturn_StaysUndefined()
    {
        var source = """
            function inf(n: any) { let x: number = 0; x = undefined as any; return x; }
            console.log(inf(1));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("undefined\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #372 case 2: a `: number` local holding the undefined sentinel is observed by something other
    // than a return (here `console.log`) inside a void function. The corruption happens at the local
    // store, independent of any return, so object-slotting the local must not be gated on the return.
    [Fact]
    public void TypedNumberLocal_ObservedInVoidFunction_StaysUndefined()
    {
        var source = """
            function obs(): void { let x: number = 0; x = undefined as any; console.log(x); }
            obs();
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("undefined\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #372 case 3: a `: number` PARAMETER reassigned the undefined sentinel. A parameter has no local
    // declaration to mark; it is matched by name in the taint pass and its arg slot widened to object.
    // The same shape for a `: boolean` parameter widens the bool arg slot.
    [Fact]
    public void TypedNumberParam_ReassignedUndefined_StaysUndefined()
    {
        var source = """
            function p(x: number): number { x = undefined as any; return x; }
            function q(b: boolean): boolean { b = undefined as any; return b; }
            console.log(p(1));
            console.log(q(true));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("undefined\nundefined\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #372: the same tainted-parameter shape inside an instance method, a static method, and a
    // constructor — each resolves parameter slots through a different resolver, all of which must
    // widen a flagged number/boolean parameter to object.
    [Fact]
    public void TypedNumberParam_ReassignedUndefined_InMethodStaticAndCtor_StaysUndefined()
    {
        var source = """
            class C {
                m(x: number): number { x = undefined as any; return x; }
                static s(x: number): number { x = undefined as any; return x; }
                constructor(n: number) { n = undefined as any; this.r = n; }
                r: any;
            }
            const c = new C(5);
            console.log(c.m(1));
            console.log(C.s(2));
            console.log(c.r);
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("undefined\nundefined\nundefined\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #372 (pre-existing param-assignment bug, discovered while fixing #372 and filed as a follow-up):
    // reassigning a `: number` parameter with a SOUND numeric value would box the value and `Starg`
    // it into the unboxed double arg slot, failing IL verification (StackUnexpected) and reading back
    // garbage. The assignment now converts the value to the parameter slot's declared type first.
    [Fact]
    public void TypedNumberParam_SoundReassignment_ComputesCorrectly()
    {
        var source = """
            function a(x: number): number { x = 99; return x; }
            function c(x: number): number { x = x + 1; return x * 2; }
            console.log(a(10));
            console.log(c(10));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("99\n22\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #372 guard: a `: number` parameter that never receives an `any`/`undefined` value keeps its
    // sound unboxed double arg slot — the taint pass must not over-widen parameters either.
    [Fact]
    public void TypedNumberParam_SoundBody_StillUsesNumericValue()
    {
        var source = """
            function scale(x: number): number { let y: number = x * 2; return y + 1; }
            console.log(scale(10));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("21\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #402: the same StackUnexpected corruption applied to `: boolean` params, which compile to an
    // unboxed `bool` arg slot. Reassigning one (literal or computed) must Unbox_Any the boxed value
    // back to bool before Starg.
    [Fact]
    public void TypedBooleanParam_SoundReassignment_ComputesCorrectly()
    {
        var source = """
            function a(x: boolean): boolean { x = false; return x; }
            function c(x: boolean): boolean { x = !x; return x; }
            console.log(a(true));
            console.log(c(true));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("false\nfalse\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }

    // #402: a `: string` param compiles to a `string` arg slot (a reference type), so the conversion
    // before Starg is a `castclass` rather than `Unbox_Any`. Reassigning one must not box-and-Starg
    // an `object` into the `string` slot.
    [Fact]
    public void TypedStringParam_SoundReassignment_ComputesCorrectly()
    {
        var source = """
            function a(x: string): string { x = "hi"; return x; }
            function c(x: string): string { x = x + "!"; return x; }
            console.log(a("a"));
            console.log(c("a"));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("hi\na!\n", output);
        Assert.Equal(output, TestHarness.RunInterpreted(source));
    }
}
