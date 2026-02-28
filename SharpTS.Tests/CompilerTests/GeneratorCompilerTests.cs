namespace SharpTS.Tests.CompilerTests;

/// <summary>
/// Generator compiler tests have been migrated to SharedTests/GeneratorTests.cs
/// to run in both interpreter and compiled modes.
///
/// Migrated tests:
/// - Iterator Protocol (.next()): BasicYield, EmptyGenerator, IteratorResult, MultipleInstances
/// - Yield* with Collections: YieldStarString, YieldStarMap, YieldStarSet
/// - Interpreter Parity tests: now redundant (SharedTests runs both modes automatically)
/// </summary>
public class GeneratorCompilerTests
{
}
