using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.CompilerTests.BuiltInModules;

/// <summary>
/// All tests migrated to SharedTests/BuiltInModules/OsModuleTests.cs
/// to run against both interpreter and compiler.
/// </summary>
public class OsModuleTests
{
    // Migrated to SharedTests/BuiltInModules/OsModuleTests.cs:
    // - Os_Platform_ReturnsValidPlatform
    // - Os_Arch_ReturnsValidArchitecture
    // - Os_Hostname_ReturnsNonEmpty
    // - Os_Homedir_ReturnsNonEmpty
    // - Os_Tmpdir_ReturnsNonEmpty
    // - Os_Type_ReturnsValidType
    // - Os_Release_ReturnsNonEmpty
    // - Os_Cpus_ReturnsNonEmptyArray
    // - Os_Totalmem_ReturnsPositiveNumber
    // - Os_Freemem_ReturnsPositiveNumber
    // - Os_EOL_ReturnsValidLineEnding
    // - Os_UserInfo_ReturnsValidObject
    // - Os_Freemem_IsLessThanTotalmem
    // - Os_Freemem_IsReasonableAmount
    // - Os_Totalmem_IsReasonableAmount
    // - Os_Memory_ValuesAreRealistic
    // - Os_Cpus_HaveValidProperties
    // - Os_Homedir_IsAbsolutePath
    // - Os_Tmpdir_IsAbsolutePath
    // - Os_Platform_MatchesType
    // - Os_Loadavg_ReturnsArrayOfThreeNumbers
    // - Os_Loadavg_WindowsReturnsZeros
    // - Os_NetworkInterfaces_ReturnsObject
    // - Os_NetworkInterfaces_IsValidObject
}
