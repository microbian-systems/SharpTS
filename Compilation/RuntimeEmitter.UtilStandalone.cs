using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace SharpTS.Compilation;

/// <summary>
/// Emits self-contained util module helper methods into $Runtime.
/// These methods do not require SharpTS.dll at runtime.
/// </summary>
public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits all self-contained util helper methods.
    /// Must define all method signatures first, then emit bodies (for recursive calls).
    /// </summary>
    private void EmitUtilStandaloneMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Phase 1: Define all method signatures
        DefineUtilHelperSignatures(typeBuilder, runtime);
        DefineUtilDeepEqualSignature(typeBuilder, runtime);
        DefineUtilParseArgsSignatures(typeBuilder, runtime);

        // Phase 2: Emit all method bodies (can now reference each other)
        EmitUtilInspectValueBody(runtime);
        EmitUtilInspectArrayBody(runtime);
        EmitUtilInspectObjectBody(runtime);
        EmitUtilInspectBody(runtime);
        EmitUtilFormatBody(runtime);
        EmitUtilIsDeepStrictEqualBody(runtime);
        EmitUtilDeepEqualImplBody(runtime);

        // Phase 3: Emit parseArgs helper methods
        EmitUtilParseArgsGetBoolOptionBody(runtime);
        EmitUtilParseArgsGetArgsArrayBody(runtime);
        EmitUtilParseArgsGetOptionsDefBody(runtime);
        EmitUtilParseLongOptionBody(runtime);
        EmitUtilParseShortOptionsBody(runtime);
        EmitUtilParseArgsBody(runtime);
    }

    private void DefineUtilHelperSignatures(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Note: These signatures are already defined in DefineUtilInspectSignatures (called earlier)
        // This method is kept for documentation/future use but the signatures are pre-defined
        // so that ConsoleDir can reference them before EmitUtilMethods is called.

        // UtilInspectValue, UtilInspectArray, UtilInspectObject are defined in DefineUtilInspectSignatures
        // UtilInspect signature already defined in EmitUtilMethods
        // UtilFormat signature already defined in EmitUtilMethods
        // UtilIsDeepStrictEqual signature already defined in EmitUtilMethods
        // UtilParseArgs signature already defined in EmitUtilMethods
    }
}
