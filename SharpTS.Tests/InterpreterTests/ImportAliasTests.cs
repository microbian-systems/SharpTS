// =============================================================================
// MIGRATION NOTICE
// =============================================================================
// All import alias tests have been migrated to SharedTests/ImportAliasTests.cs
// to run against both interpreter and compiler execution modes.
//
// The shared tests cover:
// - Basic function and variable aliases
// - Class aliases (with instantiation)
// - Nested namespace paths
// - Interface aliases (type-only)
// - Enum aliases
// - Import aliases inside namespaces
// - Exported import aliases
// - Multiple aliases
// - Aliasing nested namespaces
// - Generic class aliases
// - Error_InvalidNamespace
// - Error_InvalidMember
// - Error_IntermediateNotNamespace
//
// All 15 tests pass in both interpreter and compiler modes.
//
// See: SharpTS.Tests/SharedTests/ImportAliasTests.cs
// =============================================================================

namespace SharpTS.Tests.InterpreterTests;

public class ImportAliasTests
{
}
