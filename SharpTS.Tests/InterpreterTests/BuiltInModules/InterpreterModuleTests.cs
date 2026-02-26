using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.InterpreterTests.BuiltInModules;

/// <summary>
/// All tests migrated to SharedTests/BuiltInModules/ to run against both interpreter and compiler:
/// - Path tests → SharedTests/BuiltInModules/PathModuleTests.cs
/// - OS tests → SharedTests/BuiltInModules/OsModuleTests.cs
/// - FS tests → SharedTests/BuiltInModules/FsModuleTests.cs
/// - Querystring tests → SharedTests/BuiltInModules/QuerystringModuleTests.cs
/// - Assert tests → SharedTests/BuiltInModules/AssertModuleTests.cs
/// - URL tests → SharedTests/BuiltInModules/UrlModuleTests.cs
/// - Process tests → SharedTests/BuiltInModules/ProcessGlobalTests.cs
/// </summary>
public class InterpreterModuleTests
{
    // PATH MODULE TESTS - Migrated to SharedTests/BuiltInModules/PathModuleTests.cs:
    // - Path_Join_CombinesPaths
    // - Path_Basename_ReturnsFilename
    // - Path_Dirname_ReturnsDirectory
    // - Path_Extname_ReturnsExtension
    // - Path_IsAbsolute_ChecksPathType
    // - Path_Sep_ReturnsPathSeparator
    // - Path_Parse_ReturnsPathComponents

    // OS MODULE TESTS - Migrated to SharedTests/BuiltInModules/OsModuleTests.cs:
    // - Os_Platform_ReturnsValidPlatform
    // - Os_Arch_ReturnsValidArchitecture
    // - Os_Hostname_ReturnsNonEmpty
    // - Os_Homedir_ReturnsPath
    // - Os_Tmpdir_ReturnsPath
    // - Os_EOL_ReturnsNewline
    // - Os_Cpus_ReturnsArray
    // - Os_Totalmem_ReturnsPositiveNumber
    // - Os_UserInfo_ReturnsObject
    // - Os_Loadavg_ReturnsArray
    // - Os_NetworkInterfaces_ReturnsObject

    // FS MODULE TESTS - Migrated to SharedTests/BuiltInModules/FsModuleTests.cs:
    // - Fs_ExistsSync_ChecksFileExistence
    // - Fs_WriteFileSync_And_ReadFileSync
    // - Fs_AppendFileSync_AppendsContent
    // - Fs_MkdirSync_And_RmdirSync
    // - Fs_ReaddirSync_ListsEntries
    // - Fs_StatSync_ReturnsFileInfo
    // - Fs_StatSync_DetectsDirectory
    // - Fs_RenameSync_MovesFile
    // - Fs_CopyFileSync_CopiesFile
    // - MixedModuleImports_WorkTogether

    // QUERYSTRING MODULE TESTS - Migrated to SharedTests/BuiltInModules/QuerystringModuleTests.cs:
    // - Querystring_Parse_ParsesSimpleString
    // - Querystring_Parse_HandlesUrlEncoding
    // - Querystring_Stringify_CreatesQueryString
    // - Querystring_Escape_EncodesString
    // - Querystring_Unescape_DecodesString

    // ASSERT MODULE TESTS - Migrated to SharedTests/BuiltInModules/AssertModuleTests.cs:
    // - Assert_Ok_PassesForTruthyValues
    // - Assert_Ok_ThrowsForFalsy
    // - Assert_StrictEqual_PassesForEqualValues
    // - Assert_StrictEqual_ThrowsForUnequalValues
    // - Assert_DeepStrictEqual_PassesForEqualObjects
    // - Assert_Fail_AlwaysThrows

    // URL MODULE TESTS - Migrated to SharedTests/BuiltInModules/UrlModuleTests.cs:
    // - Url_Parse_ParsesFullUrl
    // - Url_Format_CreatesUrlString
    // - Url_Resolve_ResolvesRelativeUrl

    // PROCESS ENHANCEMENT TESTS - Migrated to SharedTests/BuiltInModules/ProcessGlobalTests.cs:
    // - Process_Hrtime_ReturnsArray
    // - Process_Uptime_ReturnsPositiveNumber
    // - Process_MemoryUsage_ReturnsObject
}
