namespace SharpTS.Tests.CompilerTests;

/// <summary>
/// All tests migrated to SharedTests/ModuleAugmentationTests.cs
/// to run in both interpreter and compiled modes.
///
/// Migrated tests:
/// - DeclareModule_ParsesCorrectly
/// - DeclareGlobal_ParsesCorrectly
/// - DeclareGlobal_DefinesNewInterface
/// - DeclareGlobal_WithExport_DefinesInterface
/// - AmbientModule_IsTypeOnly
/// - AmbientModule_MultipleDeclarations
/// - ModuleAugmentation_AddsNewInterface
/// - DeclareModule_WithTypeAlias
/// - DeclareModule_Interpreter_IsNoOp (now covered by shared DeclareModule_ParsesCorrectly)
/// - DeclareGlobal_Interpreter_IsNoOp (now covered by shared DeclareGlobal_ParsesCorrectly)
/// </summary>
public class ModuleAugmentationTests
{
}
