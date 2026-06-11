using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for class expressions (const C = class { ... }). Runs against both interpreter and compiler.
/// </summary>
public class ClassExpressionTests
{
    #region Anonymous Class Expressions

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AnonymousClassExpression_Basic(ExecutionMode mode)
    {
        var source = """
            const MyClass = class {
                x: number = 42;
            };
            let obj = new MyClass();
            console.log(obj.x);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AnonymousClassExpression_WithMethod(ExecutionMode mode)
    {
        var source = """
            const Counter = class {
                count: number = 0;
                increment(): number {
                    this.count = this.count + 1;
                    return this.count;
                }
            };
            let c = new Counter();
            c.increment();
            c.increment();
            console.log(c.count);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AnonymousClassExpression_WithConstructor(ExecutionMode mode)
    {
        var source = """
            const Point = class {
                x: number;
                y: number;
                constructor(x: number, y: number) {
                    this.x = x;
                    this.y = y;
                }
            };
            let p = new Point(10, 20);
            console.log(p.x);
            console.log(p.y);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n20\n", output);
    }

    #endregion

    #region Named Class Expressions

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamedClassExpression_Basic(ExecutionMode mode)
    {
        var source = """
            const Node = class Node {
                value: number;
                constructor(v: number) {
                    this.value = v;
                }
            };
            let n = new Node(99);
            console.log(n.value);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("99\n", output);
    }

    #endregion

    #region Inheritance

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassExpression_WithInheritance(ExecutionMode mode)
    {
        var source = """
            const Base = class {
                getValue(): number { return 10; }
            };
            const Derived = class extends Base {
                getValue(): number { return 20; }
            };
            let b = new Base();
            let d = new Derived();
            console.log(b.getValue());
            console.log(d.getValue());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n20\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassExpression_SuperCall(ExecutionMode mode)
    {
        var source = """
            const Animal = class {
                name: string;
                constructor(name: string) {
                    this.name = name;
                }
            };
            const Dog = class extends Animal {
                constructor(name: string) {
                    super(name);
                }
                bark(): string { return this.name + " barks"; }
            };
            let d = new Dog("Rex");
            console.log(d.bark());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("Rex barks\n", output);
    }

    #endregion

    #region Arrays and Variables

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassExpression_MultipleClassesInArray(ExecutionMode mode)
    {
        // Note: new <expression>() syntax isn't supported yet.
        // This test verifies class expressions can be stored and retrieved from arrays.
        var source = """
            const Class1 = class { value: number = 1; };
            const Class2 = class { value: number = 2; };
            let classes = [Class1, Class2];
            let obj1 = new Class1();
            let obj2 = new Class2();
            console.log(obj1.value);
            console.log(obj2.value);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassExpression_AssignedToVariable(ExecutionMode mode)
    {
        // Class expressions can be assigned to variables and instantiated
        var source = """
            const MyClass = class {
                value: number = 123;
            };
            let obj = new MyClass();
            console.log(obj.value);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("123\n", output);
    }

    #endregion

    #region Static Members

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassExpression_StaticField(ExecutionMode mode)
    {
        var source = """
            const MyClass = class {
                static count: number = 0;
            };
            console.log(MyClass.count);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassExpression_StaticMethod(ExecutionMode mode)
    {
        var source = """
            const Factory = class {
                static create(): number {
                    return 42;
                }
            };
            console.log(Factory.create());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n", output);
    }

    #endregion

    #region Accessors (get / set)

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassExpression_Getter(ExecutionMode mode)
    {
        // Regression for #283: a named getter on a class expression returned undefined
        // in compiled mode because the class-expr GetProperty body never dispatched accessors.
        var source = """
            const Widget = class {
                constructor(public id: number) {}
                get label(): string { return "w" + this.id; }
                greet(): string { return "hi"; }
            };
            const w = new Widget(3);
            console.log(w.id);
            console.log(w.greet());
            console.log(w.label);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("3\nhi\nw3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassExpression_Getter_DynamicAccess(ExecutionMode mode)
    {
        // Dynamic (bracket) access also routes through the class-expr GetProperty body (#283).
        var source = """
            const Widget = class {
                constructor(public id: number) {}
                get label(): string { return "w" + this.id; }
            };
            const w = new Widget(7);
            console.log((w as any)["label"]);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("w7\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassExpression_Setter(ExecutionMode mode)
    {
        // Symmetric gap (#283): the class-expr SetProperty body never dispatched setters,
        // so writes landed in _fields while reads kept hitting the getter.
        var source = """
            const Counter = class {
                private _n: number = 0;
                get n(): number { return this._n; }
                set n(v: number) { this._n = v * 2; }
            };
            const c = new Counter();
            c.n = 21;
            console.log(c.n);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassExpression_Setter_DynamicAccess(ExecutionMode mode)
    {
        // Dynamic (bracket) assignment must invoke the setter in both modes. Compiled mode is
        // covered by #283's SetProperty dispatch; the interpreter's bracket path is fixed by #290.
        var source = """
            const Counter = class {
                private _n: number = 0;
                get n(): number { return this._n; }
                set n(v: number) { this._n = v * 2; }
            };
            const c = new Counter();
            (c as any)["n"] = 5;
            console.log((c as any)["n"]);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n", output);
    }

    #endregion
}
