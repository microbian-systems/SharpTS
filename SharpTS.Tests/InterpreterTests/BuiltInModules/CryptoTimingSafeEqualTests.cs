namespace SharpTS.Tests.InterpreterTests.BuiltInModules;

/// <summary>
/// Crypto timingSafeEqual tests have been migrated to
/// SharedTests/BuiltInModules/CryptoTimingSafeEqualTests.cs
/// to run in both interpreter and compiled modes.
///
/// Migrated tests:
/// - Basic: EqualBuffers, DifferentBuffers, EmptyBuffers, SingleByte Equal/NotEqual
/// - Length mismatch: DifferentLengths, EmptyVsNonEmpty (both throw)
/// - Crypto use cases: HashComparison, HmacComparison, DifferentHashes
/// - Return type: ReturnsBoolean
/// - Large buffers: Equal, OneByteDifferent
/// - String inputs: Equal, NotEqual, DifferentLengths, MixedBufferAndString
/// - Parity tests: now redundant (SharedTests runs both modes automatically)
/// </summary>
public class CryptoTimingSafeEqualTests
{
}
