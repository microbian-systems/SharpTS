namespace SharpTS.Tests.InterpreterTests.BuiltInModules;

/// <summary>
/// Crypto KDF tests have been migrated to SharedTests/BuiltInModules/CryptoKDFTests.cs
/// to run in both interpreter and compiled modes.
///
/// Migrated tests:
/// - PBKDF2: ReturnsBuffer, Sha256/Sha1/Sha512/Sha384 KnownVectors, DifferentIterations,
///   DifferentSalts, BufferPassword, BufferSalt, UnsupportedAlgorithmThrows
/// - Scrypt: ReturnsBuffer, KnownVector, DefaultParameters, DifferentSalts, DifferentPasswords,
///   DifferentCostParameter, BufferPassword, BufferSalt, CostAlias, BlockSizeAlias,
///   ParallelizationAlias, Deterministic, WithOptions
/// - Parity tests: now redundant (SharedTests runs both modes automatically)
/// </summary>
public class CryptoKDFTests
{
}
