using System;
using System.Collections.Generic;
using System.Linq;
using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Differential (metamorphic) parity harness: runs each snippet through BOTH the
/// interpreter and the compiler and asserts they produce identical output. The two
/// modes are each other's oracle — no hand-authored expected value is needed, so this
/// scales parity-checking far past the hand-written dual-mode tests (a snippet only
/// gets a dual-mode test if someone both wrote it AND knew the right answer; that gap
/// is exactly how e.g. console.log(0.1+0.2) diverged silently).
///
/// The corpus targets the historically divergence-prone areas (numbers, operators,
/// coercion, strings, JSON, collections). It is a green regression GATE: every snippet
/// here currently agrees across modes. Snippets the harness found to diverge are kept in
/// <see cref="ParityCorpus.KnownDivergences"/> and pinned by
/// <see cref="KnownDivergence_StillDiverges"/>, so the gate stays green while each
/// divergence is tracked (and a fix trips the pin, prompting promotion to the corpus).
/// </summary>
public class DifferentialParityTests
{
    public static IEnumerable<object[]> CorpusNames =>
        ParityCorpus.Snippets.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void InterpreterAndCompiledAgree(string name)
    {
        var source = ParityCorpus.Snippets[name];
        var interp = Capture(() => TestHarness.RunInterpreted(source));
        var compiled = Capture(() => TestHarness.RunCompiled(source));
        Assert.True(interp == compiled,
            $"interp/compiled divergence for '{name}':\n--- interpreted ---\n{interp}\n--- compiled ---\n{compiled}\n--- source ---\n{source}");
    }

    /// <summary>
    /// Runs one mode and returns its stdout, or a normalized error marker if it threw.
    /// Thrown errors are reduced to their CLR type so a snippet that crashes in one mode
    /// shows up as a mismatch against the other mode's real output, without flaking on
    /// the exact (mode-specific) error wording.
    /// </summary>
    private static string Capture(Func<string> run)
    {
        try { return run(); }
        catch (Exception ex) { return $"<<threw:{ex.GetType().Name}>>"; }
    }

    [Fact]
    public void KnownDivergences_StillDiverge()
    {
        // Pins documented interp<->compiled bugs (currently none): when one is fixed this
        // fails, prompting promotion of the snippet into the green corpus. Iterates rather
        // than a [Theory] so an empty set is simply green.
        foreach (var entry in ParityCorpus.KnownDivergences)
        {
            var interp = Capture(() => TestHarness.RunInterpreted(entry.Value.Source));
            var compiled = Capture(() => TestHarness.RunCompiled(entry.Value.Source));
            Assert.True(interp != compiled,
                $"'{entry.Key}' no longer diverges — fix confirmed; move it into the green corpus. Note: {entry.Value.Note}");
        }
    }
}

/// <summary>
/// The curated parity corpus. Each entry is (name -> TypeScript source) and must
/// currently produce identical interpreter/compiled output. Add snippets freely; if a
/// new one diverges, either fix the underlying bug or move it to KnownDivergences with a
/// note. Snippets are intentionally small and deterministic and must terminate without an
/// uncaught error (error-parity is a separate, later concern).
/// </summary>
internal static class ParityCorpus
{
    internal static readonly Dictionary<string, string> Snippets = new()
    {
        // ---- numbers / formatting ----
        ["num-arithmetic"] = "console.log(1 + 2, 10 / 3, 2 ** 10, 7 % 3, -5 % 3, 0.1 + 0.2);",
        ["num-special"] = "console.log(1 / 0, -1 / 0, 0 / 0, Math.sqrt(-1), -0, 1 / -0);",
        ["num-thresholds"] = "console.log(1e20, 1e21, 1e-6, 1e-7, 123456789, 1234567890123456789);",
        ["num-parse"] = "console.log(parseInt('42px'), parseFloat('3.14xyz'), Number('  12  '), Number(''), Number('x'));",
        ["num-methods"] = "console.log((255).toString(16), (255).toString(2), (3.14159).toFixed(2), (1234.5678).toPrecision(6));",
        ["num-int-ops"] = "console.log(Math.floor(3.7), Math.ceil(3.2), Math.round(2.5), Math.round(-2.5), Math.trunc(-3.7), Math.abs(-9));",

        // ---- operators ----
        ["op-comparison"] = "console.log(1 < 2, 'a' < 'b', 2 <= 2, 3 > 1, 'b' > 'a', 1 == 1, 1 === 1, NaN === NaN, NaN !== NaN);",
        ["op-logical"] = "console.log(0 || 'x', 1 && 'y', null ?? 'z', '' || 'fb', 0 ?? 'no', false && 1, true || 1);",
        ["op-bitwise"] = "console.log(5 & 3, 5 | 3, 5 ^ 3, ~5, 1 << 4, -8 >> 1, -8 >>> 28, 0xff & 0x0f);",
        ["op-typeof"] = "console.log(typeof 1, typeof 's', typeof true, typeof undefined, typeof null, typeof {}, typeof [], typeof function(){});",
        ["op-ternary-comma"] = "console.log(true ? 'a' : 'b', false ? 'a' : 'b', (1, 2, 3));",
        ["op-increment"] = "let i = 5; console.log(i++, i, ++i, i--, i, --i);",

        // ---- coercion ----
        ["coerce-concat"] = "console.log('' + 1, '' + true, '' + null, '' + undefined, '' + 1.5);",
        ["coerce-add"] = "console.log(1 + '2', '3' + 4, true + 1, null + 1, undefined + 1, 1 + null, 2 + true);",
        ["coerce-loose-eq"] = "console.log(0 == false, '' == false, '0' == 0, null == undefined, 1 == '1', 'true' == true);",
        ["coerce-truthy"] = "console.log(!!0, !!'', !!null, !!undefined, !!NaN, !!'x', !![], !!{}, !!0.0);",
        // Array -> string coercion uses Array.prototype.toString (join), not the debug format.
        ["coerce-array-string"] = "console.log('' + [1, 2, 3], String([4, 5]), `${[6, 7]}`, [8, 9].toString(), String([]), ['a', 'b'].join('-'));",

        // ---- strings ----
        ["str-methods"] = "console.log('Hello'.toUpperCase(), 'WORLD'.toLowerCase(), '  t  '.trim(), 'a,b,c'.split(',').length, 'abc'.charAt(1), 'abc'.indexOf('b'));",
        ["str-template"] = "const x = 5; console.log(`x=${x}, x*2=${x * 2}, ${x > 3 ? 'big' : 'small'}, ${[1, 2].join('+')}`);",
        ["str-slice"] = "console.log('hello world'.slice(0, 5), 'hello'.substring(1, 3), 'abcdef'.substr(2, 2), 'x'.repeat(4), 'ab'.padStart(5, '0'));",
        ["str-replace"] = "console.log('a-b-c'.replace('-', '+'), 'a-b-c'.replaceAll('-', '+'), 'Hello'.includes('ell'), 'Hello'.startsWith('He'), 'Hello'.endsWith('lo'));",

        // ---- JSON ----
        ["json-stringify"] = "console.log(JSON.stringify({ a: 1, b: [1, 2, 3], c: 'str', d: true, e: null }));",
        ["json-numbers"] = "console.log(JSON.stringify({ a: 0.1 + 0.2, b: 1e21, c: 1e20, d: [NaN, Infinity] }));",
        ["json-roundtrip"] = "console.log(JSON.stringify(JSON.parse('{\"x\":1.5,\"y\":[true,false],\"z\":\"hi\"}')));",
        ["json-pretty"] = "console.log(JSON.stringify({ a: 1, b: { c: 2 } }, null, 2));",

        // ---- arrays ----
        ["arr-hof"] = "console.log([1, 2, 3].map(x => x * 2).join(','), [1, 2, 3, 4].filter(x => x % 2 === 0).join(','), [1, 2, 3].reduce((a, b) => a + b, 0));",
        ["arr-mutate"] = "const a = [3, 1, 2]; a.sort(); a.push(4); console.log(a.join(','), a.reverse().join(','), a.length);",
        ["arr-query"] = "console.log([1, 2, 3].includes(2), [1, 2, 3].indexOf(3), [1, 2, 3].find(x => x > 1), [1, 2, 3].some(x => x > 2), [1, 2, 3].every(x => x > 0));",
        ["arr-spread"] = "console.log([...[1, 2], ...[3, 4]].join('-'), [1, 2, 3].slice(1).join(','));",
        // Array.from over iterables AND array-likes ({length} / indexed).
        ["arr-from"] = "console.log(Array.from({ length: 3 }, (_, i) => i).join(','), Array.from({ length: 2, 0: 'a', 1: 'b' }).join(','), Array.from('hi').join(','), Array.from(new Set([1, 1, 2])).join(','));",

        // ---- objects ----
        ["obj-keys"] = "const o = { a: 1, b: 2, c: 3 }; console.log(Object.keys(o).join(','), Object.values(o).join(','), Object.entries(o).length);",
        ["obj-spread"] = "const a = { x: 1, y: 2 }; const b = { ...a, z: 3 }; console.log(JSON.stringify(b), 'x' in b, b.hasOwnProperty('y'));",
        ["obj-destructure"] = "const { p, q = 10, ...rest } = { p: 5, r: 1, s: 2 }; console.log(p, q, JSON.stringify(rest));",

        // ---- control flow ----
        ["ctrl-loops"] = "let s = 0; for (let i = 0; i < 5; i++) s += i; let t = ''; for (const c of 'abc') t += c.toUpperCase(); console.log(s, t);",
        ["ctrl-switch"] = "function f(n: number) { switch (n) { case 1: return 'one'; case 2: return 'two'; default: return 'many'; } } console.log(f(1), f(2), f(9));",
        ["ctrl-while"] = "let n = 10, steps = 0; while (n > 1) { n = n % 2 === 0 ? n / 2 : 3 * n + 1; steps++; } console.log(steps);",

        // ---- functions / closures ----
        ["fn-closure"] = "function counter() { let c = 0; return () => ++c; } const inc = counter(); console.log(inc(), inc(), inc());",
        ["fn-default-rest"] = "function f(a: number, b = 10, ...rest: number[]) { return a + b + rest.length; } console.log(f(1), f(1, 2), f(1, 2, 3, 4));",
        ["fn-recursion"] = "function fib(n: number): number { return n < 2 ? n : fib(n - 1) + fib(n - 2); } console.log(fib(10), fib(15));",

        // ---- classes ----
        ["class-basic"] = "class A { x: number; constructor(x: number) { this.x = x; } double() { return this.x * 2; } } const a = new A(5); console.log(a.x, a.double());",
        ["class-inherit"] = "class Animal { speak() { return '...'; } } class Dog extends Animal { speak() { return 'woof: ' + super.speak(); } } console.log(new Dog().speak(), new Animal().speak());",
        ["class-static"] = "class C { static count = 0; constructor() { C.count++; } } new C(); new C(); console.log(C.count);",

        // ---- collections ----
        ["map-set"] = "const m = new Map<string, number>([['a', 1], ['b', 2]]); const s = new Set([1, 2, 2, 3]); console.log(m.get('a'), m.size, s.size, [...s].join(','));",
        ["generator"] = "function* g() { yield 1; yield 2; yield 3; } console.log([...g()].join(','), [...g()].reduce((a, b) => a + b, 0));",

        // ---- bigint (#912) ----
        // console.log keeps the "10n" debug form; typeof is "bigint"; arithmetic/
        // comparison/radix-aware toString all agree across modes.
        ["bigint-basics"] = "console.log(typeof 10n, 10n + 20n, 2n ** 10n, 10n > 5n, 100n / 7n, (123n).toString());",
        // === both-bigint; == mixed-numeric (coerces); String()/Number() unwrap the
        // bare numeric form (no "n").
        ["bigint-mixed"] = "console.log(10n === 10n, 10n == 10, String(42n), Number(42n), 5n * 5n);",
        // Language ToString coercion (String(), template, `+`) is the bare numeric form;
        // Boolean()/`!` use ToBoolean(bigint) (0n is falsy).
        ["bigint-coerce"] = "console.log(String(42n), `${42n}`, '' + 42n, Boolean(0n), Boolean(5n), !0n, 0n ? 'y' : 'n');",
        // toString(radix) 2..36, negatives, zero, and arbitrary precision.
        ["bigint-tostring-radix"] = "console.log((255n).toString(16), (255n).toString(2), (255n).toString(8), (-255n).toString(16), (0n).toString(16), (255n).toString(36));",
        // ECMA-262 loose equality: bigint vs number/string/boolean (mathematical compare;
        // string parsed as integer; empty string is 0n; booleans coerce to 0n/1n).
        ["bigint-loose-eq"] = "console.log(10n == '10', 10n == 'abc', 0n == '', 1n == true, 0n == false, 10n == 10.5, 10n != 10);",
    };

    /// <summary>
    /// Snippets the harness found to DIVERGE — real interp↔compiled bugs, kept out of the
    /// green gate and pinned by <c>DifferentialParityTests.KnownDivergence_StillDiverges</c>
    /// so a future fix prompts promotion into <see cref="Snippets"/>. Each value is
    /// (source, note) where the note records which mode is correct.
    /// </summary>
    internal static readonly Dictionary<string, (string Source, string Note)> KnownDivergences = new()
    {
        // (empty) The harness's first two findings — array->string coercion and Array.from
        // on an array-like — were fixed and promoted into Snippets above (coerce-array-string,
        // arr-from). Future divergences that can't be fixed immediately go here.
    };
}
