using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.CompilerTests.BuiltInModules;

/// <summary>
/// All tests migrated to SharedTests/BuiltInModules/ProcessGlobalTests.cs
/// to run against both interpreter and compiler.
/// </summary>
public class ProcessGlobalTests
{
    // Migrated to SharedTests/BuiltInModules/ProcessGlobalTests.cs:
    // - Process_Platform_ReturnsValidPlatform
    // - Process_Arch_ReturnsValidArchitecture
    // - Process_Pid_ReturnsPositiveNumber
    // - Process_Version_ReturnsVersionString
    // - Process_Cwd_ReturnsCurrentDirectory
    // - Process_Env_ReturnsEnvObject
    // - Process_Env_ContainsPathVariable
    // - Process_Argv_ReturnsArray
    // - Process_Argv_ContainsElements
    // - Process_MultipleProperties_WorkTogether
    // - Process_Hrtime_ReturnsArray
    // - Process_Hrtime_ReturnsPositiveSeconds
    // - Process_Hrtime_ReturnsValidNanoseconds
    // - Process_Hrtime_WithPrevious_ReturnsDiff
    // - Process_Uptime_ReturnsPositiveNumber
    // - Process_Uptime_IsSmallForNewProcess
    // - Process_MemoryUsage_ReturnsObject
    // - Process_MemoryUsage_HasRss
    // - Process_MemoryUsage_HasHeapTotal
    // - Process_MemoryUsage_HasHeapUsed
    // - Process_MemoryUsage_HeapUsedLessThanTotal
    // - Process_AllEnhancements_WorkTogether
    // - Process_Cwd_IsAbsolutePath
    // - Process_Argv_FirstElementIsPath
    // - Process_Hrtime_MeasuresRealTime
    // - Process_Uptime_IncreasesOverTime
    // - Process_MemoryUsage_AllPropertiesPresent
    // - Process_Env_HasCommonVariables
    // - Process_Platform_MatchesOs
    // - Process_Arch_MatchesOs
}
