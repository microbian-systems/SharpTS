using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.CompilerTests.BuiltInModules;

/// <summary>
/// All tests migrated to SharedTests/BuiltInModules/AssertModuleTests.cs
/// to run against both interpreter and compiler.
/// </summary>
public class AssertModuleTests
{
    // Migrated to SharedTests/BuiltInModules/AssertModuleTests.cs:
    // - Assert_Ok_PassesForTruthyValues
    // - Assert_Ok_ThrowsForFalsy
    // - Assert_StrictEqual_PassesForEqualValues
    // - Assert_StrictEqual_ThrowsForUnequalValues
    // - Assert_StrictEqual_ThrowsForDifferentTypes
    // - Assert_NotStrictEqual_PassesForUnequalValues
    // - Assert_NotStrictEqual_ThrowsForEqualValues
    // - Assert_DeepStrictEqual_PassesForEqualObjects
    // - Assert_DeepStrictEqual_PassesForEqualArrays
    // - Assert_DeepStrictEqual_ThrowsForDifferentObjects
    // - Assert_DeepStrictEqual_ThrowsForDifferentArrays
    // - Assert_Equal_PassesForLooselyEqualValues
    // - Assert_NotEqual_PassesForDifferentValues
    // - Assert_Fail_AlwaysThrows
    // - Assert_Fail_WithDefaultMessage
    // - Assert_NamespaceImport_Works
    // - Assert_CustomMessage_IsUsed
    // - Assert_MultipleAssertions_InSequence
}
