namespace SharpTS.Tests.InterpreterTests.BuiltInModules;

/// <summary>
/// Crypto module tests have been migrated to SharedTests/BuiltInModules/CryptoModuleTests.cs
/// to run in both interpreter and compiled modes.
///
/// Migrated tests:
/// - Crypto_CreateHash_Md5, Sha1, Sha256, Sha512
/// - Crypto_CreateHash_UpdateMultiple, EmptyInput, Base64Encoding
/// - Crypto_RandomBytes_ReturnsCorrectLength, ValuesInRange, NotAllZeros
/// - Crypto_RandomFillSync_FillsEntireBuffer, WithOffset, WithOffsetAndSize, ReturnsBuffer
/// - Crypto_RandomUUID_Format, IsUnique, ContainsOnlyValidChars
/// - Crypto_RandomInt_InRange, MinMax, ReturnsInteger
/// - Crypto_CreateHmac_Sha256, AllAlgorithms, UpdateMultiple, Base64Encoding, DifferentKeys, MethodChaining
/// </summary>
public class CryptoModuleTests
{
}
