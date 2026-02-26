namespace SharpTS.Tests.InterpreterTests;

/// <summary>
/// All tests migrated to SharedTests/DecoratorTests.cs to run against both interpreter and compiler.
/// Error cases are also in SharedTests (they test parse/type-check errors, not execution mode).
/// </summary>
public class DecoratorTests
{
    // Migrated to SharedTests/DecoratorTests.cs:
    // - LegacyClassDecorator_Simple
    // - LegacyClassDecorator_Factory
    // - LegacyMethodDecorator_Simple
    // - LegacyFieldDecorator_Simple
    // - LegacyMultipleDecorators_RightToLeft
    // - LegacyParameterDecorator_Simple
    // - Stage3ClassDecorator_Simple
    // - Stage3MethodDecorator_Simple
    // - Stage3FieldDecorator_Simple
    // - Stage3Decorator_ContextStatic
    // - ReflectMetadata_DefineAndGet
    // - ReflectMetadata_PropertyKey
    // - ReflectMetadata_HasMetadata
    // - ReflectMetadata_GetKeys
    // - ReflectMetadata_DeleteMetadata
    // - ReflectMetadata_DecoratorFactory
    // - DecoratorWithoutFlag_ThrowsError
    // - Stage3ParameterDecorator_ThrowsError
}
