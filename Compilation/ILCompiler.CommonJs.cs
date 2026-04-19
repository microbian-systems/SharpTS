using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Modules;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// CommonJS module compilation methods for the IL compiler.
/// </summary>
/// <remarks>
/// CJS modules are emitted as `$Module_Name` static classes with a single `$exports` static field
/// instead of per-export fields. The body of the CJS file is compiled into a `$Initialize()`
/// method that creates an empty exports dictionary, runs the file body, and is guarded by
/// `_initStarted` for circular-require correctness.
///
/// `$GetExports()` is the public entry that callers (require/ESM imports) use; it calls
/// `$Initialize()` first, then returns the current `$exports` value.
///
/// At the moment of $Initialize call, `_initStarted` is set BEFORE running the body, so a
/// re-entrant require sees the partially-populated exports object — matching Node semantics.
/// </remarks>
public partial class ILCompiler
{
    /// <summary>
    /// Per-CJS-module init guard fields (kept directly because <see cref="TypeBuilder.GetField(string, BindingFlags)"/>
    /// doesn't work before <c>CreateType()</c>).
    /// </summary>
    private readonly Dictionary<string, (FieldBuilder InitStarted, FieldBuilder Initialized)> _cjsInitGuardFields = [];

    /// <summary>
    /// Defines the .NET type for a CommonJS module: a static class with $exports field,
    /// $GetExports() and $Initialize() methods, plus circular-init guards.
    /// </summary>
    private void DefineCommonJsModuleType(ParsedModule module)
    {
        string moduleTypeName = $"$Module_{CompilationContext.SanitizeModuleName(module.ModuleName)}";
        var moduleType = _moduleBuilder.DefineType(
            moduleTypeName,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract
        );

        _modules.Types[module.Path] = moduleType;

        // The single $exports field — holds the live module.exports value.
        var exportsField = moduleType.DefineField(
            "$exports",
            typeof(object),
            FieldAttributes.Public | FieldAttributes.Static
        );
        _modules.CommonJsExportFields[module.Path] = exportsField;

        // Provide a degenerate ExportFields entry so the rest of the module-emission machinery
        // (which always indexes ExportFields[path]) doesn't throw KeyNotFoundException.
        _modules.ExportFields[module.Path] = new Dictionary<string, FieldBuilder>
        {
            ["$exports"] = exportsField
        };

        // Init guard fields. _initStarted is set BEFORE the body runs (so circular requires see
        // partial state). _initialized is set AFTER the body completes (currently informational).
        var initStartedField = moduleType.DefineField(
            "_initStarted",
            typeof(bool),
            FieldAttributes.Private | FieldAttributes.Static
        );
        var initializedField = moduleType.DefineField(
            "_initialized",
            typeof(bool),
            FieldAttributes.Private | FieldAttributes.Static
        );
        _cjsInitGuardFields[module.Path] = (initStartedField, initializedField);

        // $Initialize: idempotent body executor with init-started guard.
        var initMethod = moduleType.DefineMethod(
            "$Initialize",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            Type.EmptyTypes
        );
        _modules.InitMethods[module.Path] = initMethod;

        // $GetExports: calls $Initialize, returns $exports.
        var getExportsMethod = moduleType.DefineMethod(
            "$GetExports",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(object),
            Type.EmptyTypes
        );
        _modules.CommonJsGetExportsMethods[module.Path] = getExportsMethod;

        var getExportsIl = getExportsMethod.GetILGenerator();
        getExportsIl.Emit(OpCodes.Call, initMethod);
        getExportsIl.Emit(OpCodes.Ldsfld, exportsField);
        getExportsIl.Emit(OpCodes.Ret);

        // $GetNamespace: same as $GetExports for CJS modules. Provided so the dynamic-import
        // module registry treats CJS files like ESM files (returns the module's exports object).
        var getNamespaceMethod = moduleType.DefineMethod(
            "$GetNamespace",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(object),
            Type.EmptyTypes
        );
        _moduleGetNamespaceMethods[module.Path] = getNamespaceMethod;

        var getNamespaceIl = getNamespaceMethod.GetILGenerator();
        getNamespaceIl.Emit(OpCodes.Call, initMethod);
        getNamespaceIl.Emit(OpCodes.Ldsfld, exportsField);
        getNamespaceIl.Emit(OpCodes.Ret);

        // Note: the $Initialize body is emitted later in EmitCommonJsModuleInit during Phase 9.
        // We only define the field/method shells here so other modules can reference them.
    }

    /// <summary>
    /// Emits the body of a CommonJS module's $Initialize method.
    /// </summary>
    private void EmitCommonJsModuleInit(ParsedModule module)
    {
        var moduleType = _modules.Types[module.Path];
        var initMethod = _modules.InitMethods[module.Path];
        var exportsField = _modules.CommonJsExportFields[module.Path];
        var (initStartedField, initializedField) = _cjsInitGuardFields[module.Path];

        var il = initMethod.GetILGenerator();

        // Guard: if (_initStarted) return;
        var skipLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, initStartedField);
        il.Emit(OpCodes.Brtrue, skipLabel);

        // _initStarted = true;
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stsfld, initStartedField);

        // $exports = new Dictionary<string, object?>();
        var dictType = typeof(Dictionary<string, object?>);
        var dictCtor = dictType.GetConstructor(Type.EmptyTypes)!;
        il.Emit(OpCodes.Newobj, dictCtor);
        il.Emit(OpCodes.Stsfld, exportsField);

        // Build the compilation context for this module's body.
        // Set _modules.CurrentPath first so BuildTopLevelStaticVarsForModule /
        // BuildEntryPointDisplayClassFieldsForModule scope to this module —
        // otherwise they'd fall through to the SingleFile bucket and miss
        // this module's captured-top-level-var fields. (EmitModuleInit for
        // ESM does the same dance.)
        var savedPath = _modules.CurrentPath;
        _modules.CurrentPath = module.Path;
        var ctx = CreateCompilationContext(il);
        _modules.CurrentPath = savedPath;
        ctx.CurrentModulePath = module.Path;
        ctx.ModuleExportFields = _modules.ExportFields;
        ctx.ModuleTypes = _modules.Types;
        ctx.ModuleInitMethods = _modules.InitMethods;
        ctx.ModuleImportFields = _modules.ImportFields;
        ctx.ModuleResolver = _modules.Resolver;
        ctx.CommonJsExportFields = _modules.CommonJsExportFields;
        ctx.CommonJsGetExportsMethods = _modules.CommonJsGetExportsMethods;

        // CRITICAL: this is what tells the ILEmitter to lower module.exports/exports/require
        // patterns for this body.
        ctx.CurrentCjsExportsField = exportsField;

        var emitter = new ILEmitter(ctx);

        foreach (var stmt in module.Statements)
        {
            // Skip class/function/interface/type-alias/enum declarations — compiled separately.
            if (stmt is Stmt.Class or Stmt.Function or Stmt.Interface or Stmt.TypeAlias or Stmt.Enum)
            {
                continue;
            }

            if (stmt is Stmt.Expression exprStmt)
            {
                EmitExpressionWithAsyncWait(il, emitter, exprStmt);
            }
            else
            {
                emitter.EmitStatement(stmt);
            }
        }

        // _initialized = true;
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stsfld, initializedField);

        il.MarkLabel(skipLabel);
        il.Emit(OpCodes.Ret);
    }
}
