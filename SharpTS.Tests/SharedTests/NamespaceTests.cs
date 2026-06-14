using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for TypeScript namespace declarations, including nested namespaces,
/// dotted syntax, declaration merging, and namespace members (functions, classes, enums).
/// Runs against both interpreter and compiler where supported.
/// </summary>
public class NamespaceTests
{
    #region Namespace-level variable access from member functions (#567)

    // A function declared in a namespace must be able to read namespace-level var/let/const members.
    // Compiled mode previously stored those only as runtime members of the namespace object, invisible
    // to the member function body, throwing "Undefined variable". The fix also backs each with a static
    // field surfaced to the function's resolver. These pin every variant from the issue.

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceFunction_ReadsNamespaceConst(ExecutionMode mode)
    {
        var code = @"
            namespace N { const val = 7; export function f() { return val; } export const result = f(); }
            console.log(N.result);
        ";
        Assert.Equal("7\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceFunction_ReadsNamespaceLet(ExecutionMode mode)
    {
        var code = @"
            namespace N { let val = 7; export function f() { return val; } export const r = f(); }
            console.log(N.r);
        ";
        Assert.Equal("7\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceFunction_ReadsNamespaceVar(ExecutionMode mode)
    {
        var code = @"
            namespace N { export var val = 7; export function f() { return val; } export const r = f(); }
            console.log(N.r);
        ";
        Assert.Equal("7\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceFunction_ReadsExportedConst_AndExternalAccessStillWorks(ExecutionMode mode)
    {
        var code = @"
            namespace N { export const val = 7; export function f() { return val; } export const r = f(); }
            console.log(N.r + "","" + N.val);
        ";
        Assert.Equal("7,7\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NestedNamespaceFunction_ReadsOuterAndInnerVars(ExecutionMode mode)
    {
        var code = @"
            namespace Out {
                const a = 1;
                export namespace In {
                    const b = 2;
                    export function g() { return a + b; }
                    export const r = g();
                }
            }
            console.log(Out.In.r);
        ";
        Assert.Equal("3\n", TestHarness.Run(code, mode));
    }

    // The original #567 fix only reached the plain-function path. Generators, async
    // functions, and class members emit their bodies through separate paths (state-machine
    // MoveNext methods, class method/accessor/constructor emission) that built their
    // top-level-static-var view without the namespace augmentation, so they could not
    // resolve a namespace var by bare name — silently returning undefined (state machines)
    // or throwing "Undefined variable" (class members). These pin each path.

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceGenerator_ReadsNamespaceVar(ExecutionMode mode)
    {
        var code = @"
            namespace N { const val = 4; export function* g() { yield val; } }
            console.log(N.g().next().value);
        ";
        Assert.Equal("4\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceAsyncFunction_ReadsNamespaceVar(ExecutionMode mode)
    {
        var code = @"
            namespace N { const val = 7; export async function a() { return val; } }
            N.a().then(x => console.log(x));
        ";
        Assert.Equal("7\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceClassMethod_ReadsNamespaceVar(ExecutionMode mode)
    {
        var code = @"
            namespace N { const val = 3; export class C { m() { return val; } } }
            console.log(new N.C().m());
        ";
        Assert.Equal("3\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceClassStaticMethod_ReadsNamespaceVar(ExecutionMode mode)
    {
        var code = @"
            namespace N { const val = 5; export class C { static m() { return val; } } }
            console.log(N.C.m());
        ";
        Assert.Equal("5\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceClassAccessor_ReadsNamespaceVar(ExecutionMode mode)
    {
        var code = @"
            namespace N { const val = 8; export class C { get x() { return val; } } }
            console.log(new N.C().x);
        ";
        Assert.Equal("8\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceClassConstructor_ReadsNamespaceVar(ExecutionMode mode)
    {
        var code = @"
            namespace N { const val = 6; export class C { y: number; constructor() { this.y = val; } } }
            console.log(new N.C().y);
        ";
        Assert.Equal("6\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NestedNamespaceGenerator_ReadsOuterNamespaceVar(ExecutionMode mode)
    {
        var code = @"
            namespace Out {
                const a = 10;
                export namespace In {
                    const b = 5;
                    export function* g() { yield a + b; }
                }
            }
            console.log(Out.In.g().next().value);
        ";
        Assert.Equal("15\n", TestHarness.Run(code, mode));
    }

    #endregion

    #region Mutable exported namespace variable is a live binding (#623)

    // An exported `let`/`var` that a member function mutates must be a live view through
    // external `N.x` access too, matching tsc/Node — not the snapshot taken at declaration.
    // Interpreter: the namespace object exposes a live binding onto the namespace scope slot.
    // Compiled: external `N.x` reads the var's static backing field (the one member functions
    // write), instead of the namespace object's declaration-time member. const members never
    // change, so their external value is unaffected.

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MutableExportedLet_MutationVisibleViaExternalAccess(ExecutionMode mode)
    {
        var code = @"
            namespace N {
                export let count = 0;
                export function inc() { count = count + 1; }
                export function get() { return count; }
            }
            N.inc();
            N.inc();
            console.log(N.get());
            console.log(N.count);
        ";
        Assert.Equal("2\n2\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MutableExportedVar_MutationVisibleViaExternalAccess(ExecutionMode mode)
    {
        var code = @"
            namespace N {
                export var count = 0;
                export function inc() { count++; }
            }
            N.inc();
            N.inc();
            N.inc();
            console.log(N.count);
        ";
        Assert.Equal("3\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MutableExportedLet_ExternalAccessReflectsEachMutation(ExecutionMode mode)
    {
        var code = @"
            namespace N {
                export let v = 1;
                export function set(n: number) { v = n; }
            }
            console.log(N.v);
            N.set(10);
            console.log(N.v);
            N.set(20);
            console.log(N.v);
        ";
        Assert.Equal("1\n10\n20\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ConstExportedMember_ExternalAccessUnaffected(ExecutionMode mode)
    {
        var code = @"
            namespace N {
                export const c = 5;
                export function get() { return c; }
            }
            console.log(N.get());
            console.log(N.c);
        ";
        Assert.Equal("5\n5\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NestedNamespace_MutableExport_LiveViaExternalAccess(ExecutionMode mode)
    {
        var code = @"
            namespace N {
                export namespace M {
                    export let count = 0;
                    export function inc() { count++; }
                }
            }
            N.M.inc();
            N.M.inc();
            console.log(N.M.count);
        ";
        Assert.Equal("2\n", TestHarness.Run(code, mode));
    }

    #endregion

    #region Basic Namespace Features (Both Modes)

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BasicNamespaceWithFunction(ExecutionMode mode)
    {
        var code = @"
            namespace Foo {
                export function bar(): number { return 42; }
            }
            console.log(Foo.bar());
        ";
        Assert.Equal("42\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceWithVariable(ExecutionMode mode)
    {
        var code = @"
            namespace Constants {
                export const PI: number = 3.14159;
            }
            console.log(Constants.PI);
        ";
        Assert.Equal("3.14159\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NestedNamespace(ExecutionMode mode)
    {
        var code = @"
            namespace Outer {
                export namespace Inner {
                    export function greet(): string { return ""Hello""; }
                }
            }
            console.log(Outer.Inner.greet());
        ";
        Assert.Equal("Hello\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DottedNamespaceSyntax(ExecutionMode mode)
    {
        var code = @"
            namespace A.B.C {
                export let value: number = 123;
            }
            console.log(A.B.C.value);
        ";
        Assert.Equal("123\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DeclarationMerging(ExecutionMode mode)
    {
        var code = @"
            namespace Merged {
                export function foo(): number { return 1; }
            }
            namespace Merged {
                export function bar(): number { return 2; }
            }
            console.log(Merged.foo() + Merged.bar());
        ";
        Assert.Equal("3\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceMultipleFunctions(ExecutionMode mode)
    {
        var code = @"
            namespace Utils {
                export function add(a: number, b: number): number { return a + b; }
                export function multiply(a: number, b: number): number { return a * b; }
            }
            console.log(Utils.add(2, 3));
            console.log(Utils.multiply(4, 5));
        ";
        Assert.Equal("5\n20\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DeeplyNestedDottedNamespace(ExecutionMode mode)
    {
        var code = @"
            namespace Company.Product.Feature.SubFeature {
                export const version: string = ""1.0.0"";
            }
            console.log(Company.Product.Feature.SubFeature.version);
        ";
        Assert.Equal("1.0.0\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceMergingWithDottedSyntax(ExecutionMode mode)
    {
        var code = @"
            namespace A.B {
                export let x: number = 10;
            }
            namespace A.B {
                export let y: number = 20;
            }
            console.log(A.B.x + A.B.y);
        ";
        Assert.Equal("30\n", TestHarness.Run(code, mode));
    }

    #endregion

    #region Namespace with Classes (Both Modes)

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceWithClass(ExecutionMode mode)
    {
        var code = @"
            namespace Shapes {
                export class Circle {
                    radius: number;
                    constructor(r: number) {
                        this.radius = r;
                    }
                    area(): number { return 3.14159 * this.radius * this.radius; }
                }
            }
            let c = new Shapes.Circle(2);
            console.log(c.area());
        ";
        // PI * 2 * 2 = 12.56636
        Assert.StartsWith("12.566", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DeepNestedNamespaceClass(ExecutionMode mode)
    {
        var code = @"
            namespace Company.Products.Widgets {
                export class Button {
                    label: string;
                    constructor(l: string) { this.label = l; }
                }
            }
            let btn = new Company.Products.Widgets.Button(""Click me"");
            console.log(btn.label);
        ";
        Assert.Equal("Click me\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceWithGenericClass(ExecutionMode mode)
    {
        var code = @"
            namespace Collections {
                export class Box<T> {
                    value: T;
                    constructor(v: T) { this.value = v; }
                }
            }
            let numBox = new Collections.Box<number>(42);
            console.log(numBox.value);
            let strBox = new Collections.Box<string>(""hello"");
            console.log(strBox.value);
        ";
        Assert.Equal("42\nhello\n", TestHarness.Run(code, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceClassInheritance(ExecutionMode mode)
    {
        var code = @"
            namespace Animals {
                export class Animal {
                    name: string;
                    constructor(n: string) { this.name = n; }
                }
                export class Dog extends Animal {
                    constructor(n: string) { super(n); }
                    bark(): string { return this.name + "" says woof!""; }
                }
            }
            let dog = new Animals.Dog(""Rex"");
            console.log(dog.bark());
        ";
        Assert.Equal("Rex says woof!\n", TestHarness.Run(code, mode));
    }

    #endregion

    #region Namespace with Enums (Both Modes)

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceWithEnum(ExecutionMode mode)
    {
        var code = @"
            namespace Config {
                export enum LogLevel { Debug, Info, Warn, Error }
            }
            console.log(Config.LogLevel.Error);
        ";
        Assert.Equal("3\n", TestHarness.Run(code, mode));
    }

    #endregion
}
