using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using SharpTS.Benchmarks.Infrastructure;

namespace SharpTS.Benchmarks.Benchmarks;

/// <summary>
/// Iterator-helper benchmarks for the issue #90 hot path. Measures the
/// per-element overhead of routing element loads through
/// <c>$Runtime.LoadArrayLikeElement</c> (extra Ldsfld + Brfalse per element)
/// vs. the prior direct <c>list[i]</c> callvirt. Eager path only —
/// receivers here are real <c>List&lt;object&gt;</c> instances so the
/// thread-static check falls through immediately and we measure the
/// thread-static read cost itself.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class ArrayHelpersBenchmarks
{
    private Assembly _tsAssembly = null!;
    private MethodInfo _tsMap = null!;
    private MethodInfo _tsFilter = null!;
    private MethodInfo _tsReduce = null!;
    private MethodInfo _tsForEach = null!;
    private MethodInfo _tsEvery = null!;
    private MethodInfo _tsFind = null!;

    private List<object> _arr = null!;

    [Params(100, 10_000, 1_000_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var assembly = typeof(ArrayHelpersBenchmarks).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "SharpTS.Benchmarks.TypeScriptSources.ArrayHelpers.ts")
            ?? throw new InvalidOperationException("Could not find embedded resource ArrayHelpers.ts");
        using var reader = new StreamReader(stream);
        var tsSource = reader.ReadToEnd();

        var dllPath = CompilationCache.GetOrCompile(tsSource, "ArrayHelpers");
        _tsAssembly = BenchmarkHarness.LoadCompiledAssembly(dllPath, "arrayhelpers");

        _tsMap = BenchmarkHarness.GetCompiledMethod(_tsAssembly, "arrMap");
        _tsFilter = BenchmarkHarness.GetCompiledMethod(_tsAssembly, "arrFilter");
        _tsReduce = BenchmarkHarness.GetCompiledMethod(_tsAssembly, "arrReduce");
        _tsForEach = BenchmarkHarness.GetCompiledMethod(_tsAssembly, "arrForEach");
        _tsEvery = BenchmarkHarness.GetCompiledMethod(_tsAssembly, "arrEvery");
        _tsFind = BenchmarkHarness.GetCompiledMethod(_tsAssembly, "arrFind");

        // Build the input array as a plain List<object> with boxed doubles —
        // this is exactly the shape compiled `Array<number>` produces.
        _arr = new List<object>(N);
        for (int i = 0; i < N; i++) _arr.Add((double)i);
    }

    [Benchmark]
    [BenchmarkCategory("Map")]
    public object? SharpTS_Map() => BenchmarkHarness.InvokeCompiled(_tsMap, _arr);

    [Benchmark]
    [BenchmarkCategory("Filter")]
    public object? SharpTS_Filter() => BenchmarkHarness.InvokeCompiled(_tsFilter, _arr);

    [Benchmark]
    [BenchmarkCategory("Reduce")]
    public object? SharpTS_Reduce() => BenchmarkHarness.InvokeCompiled(_tsReduce, _arr);

    [Benchmark]
    [BenchmarkCategory("ForEach")]
    public object? SharpTS_ForEach() => BenchmarkHarness.InvokeCompiled(_tsForEach, _arr);

    [Benchmark]
    [BenchmarkCategory("Every")]
    public object? SharpTS_Every() => BenchmarkHarness.InvokeCompiled(_tsEvery, _arr);

    [Benchmark]
    [BenchmarkCategory("Find")]
    public object? SharpTS_Find() => BenchmarkHarness.InvokeCompiled(_tsFind, _arr);
}
