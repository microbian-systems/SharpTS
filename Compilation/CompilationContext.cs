using System.Reflection.Emit;
using SharpTS.Compilation.Emitters;
using SharpTS.Compilation.Emitters.Modules;
using SharpTS.Compilation.Registries;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

/// <summary>
/// Represents the type currently on top of the IL evaluation stack.
/// Used for unboxed numeric optimization to avoid unnecessary boxing/unboxing.
/// </summary>
public enum StackType
{
    /// <summary>Object reference - could be any boxed type or reference type.</summary>
    Unknown,
    /// <summary>Native double (float64) - unboxed numeric value.</summary>
    Double,
    /// <summary>Native bool (int32 as 0/1) - unboxed boolean value.</summary>
    Boolean,
    /// <summary>String reference.</summary>
    String,
    /// <summary>Null reference.</summary>
    Null
}

/// <summary>
/// Entry in the hoisted array cache: a typed local variable and its descriptor.
/// </summary>
public record struct HoistedArrayEntry(LocalBuilder TypedLocal, ArrayElementsDescriptor Descriptor);

/// <summary>
/// Holds compilation state passed between ILCompiler and ILEmitter.
/// </summary>
/// <remarks>
/// Central state container for IL compilation. Provides access to the current
/// <see cref="ILGenerator"/>, <see cref="TypeMapper"/>, <see cref="LocalsManager"/>,
/// and various lookup tables for functions, classes, static members, closures,
/// and enums. Also tracks parameters, loop labels for break/continue, and
/// display class state for closure capture. Passed to <see cref="ILEmitter"/> methods.
/// </remarks>
/// <seealso cref="ILCompiler"/>
/// <seealso cref="ILEmitter"/>
/// <seealso cref="LocalsManager"/>
public partial class CompilationContext
{
    // ============================================
    // Core Compilation Infrastructure
    // ============================================

    public ILGenerator IL { get; }
    public TypeMapper TypeMapper { get; }
    public LocalsManager Locals { get; }

    /// <summary>
    /// Validated IL builder that wraps the ILGenerator with compile-time checks.
    /// Use this for new code to catch label, stack, and exception block errors early.
    /// </summary>
    public ValidatedILBuilder ILBuilder { get; private set; }

    /// <summary>
    /// Type provider for resolving .NET types (runtime or reference assembly mode).
    /// Use this instead of typeof() for type resolution to support --ref-asm compilation.
    /// </summary>
    public TypeProvider Types { get; }

    // Emitted runtime types and methods (for standalone DLLs)
    public EmittedRuntime? Runtime { get; set; }

    // Type emitter registry for type-first method dispatch
    public TypeEmitterRegistry? TypeEmitterRegistry { get; set; }

    // Built-in module emitter registry for fs, path, os, etc.
    public BuiltInModuleEmitterRegistry? BuiltInModuleEmitterRegistry { get; set; }

    // Built-in module namespace variables (variable name -> module name)
    // Tracks which local variables are built-in module namespaces for direct dispatch
    public Dictionary<string, string>? BuiltInModuleNamespaces { get; set; }

    // Built-in module method bindings (variable name -> (module name, method name))
    // Tracks which local variables are bound to built-in module methods for direct dispatch
    // Example: import { readFile } from 'fs/promises' -> readFile -> ("fs/promises", "readFile")
    public Dictionary<string, (string ModuleName, string MethodName)>? BuiltInModuleMethodBindings { get; set; }

    // All imported names from any module (builtin, primitive, stdlib TS, or user).
    // Call handlers for globally-intercepted names (TimerHandler, FetchHandler, etc.)
    // check this set to avoid shadowing imports. Stdlib TS modules like
    // 'timers' re-export setTimeout/setInterval as TS functions that must
    // win over the global handler.
    public HashSet<string>? ImportedNames { get; set; }

    // ============================================
    // Registry Services
    // ============================================

    /// <summary>
    /// Registry for class-related compilation state lookups.
    /// Provides centralized methods for resolving class names, constructors,
    /// instance/static members, and inheritance chains.
    /// </summary>
    public ClassRegistry? ClassRegistry { get; set; }

    // ============================================
    // Enum Support
    // ============================================

    // Enum support: enum name -> member name -> value (double or string)
    public Dictionary<string, Dictionary<string, object>>? EnumMembers { get; set; }

    // Enum reverse mapping: enum name -> value -> member name (only numeric values)
    public Dictionary<string, Dictionary<double, string>>? EnumReverse { get; set; }

    // Enum kinds: enum name -> kind
    public Dictionary<string, EnumKind>? EnumKinds { get; set; }

    // ============================================
    // Generic Type Parameters
    // ============================================

    // Current scope's generic type parameters (name -> GenericTypeParameterBuilder or Type)
    public Dictionary<string, Type> GenericTypeParameters { get; set; } = [];

    // ============================================
    // Miscellaneous State
    // ============================================

    /// <summary>
    /// The return type of the current method being compiled.
    /// Used for typed return optimization to avoid unnecessary boxing.
    /// When null, defaults to object (boxed return).
    /// </summary>
    public Type? CurrentMethodReturnType { get; set; }

    /// <summary>
    /// Whether the current compilation context is in JavaScript strict mode.
    /// Affects property assignment behavior on frozen/sealed objects.
    /// </summary>
    public bool IsStrictMode { get; set; }

    /// <summary>
    /// True when emitting code inside a static constructor (class initializer).
    /// In this context, 'this' refers to the class type, not an instance.
    /// </summary>
    public bool IsStaticConstructorContext { get; set; }

    // Namespace support: namespace path -> static field
    public Dictionary<string, FieldBuilder>? NamespaceFields { get; set; }

    // Top-level variables captured by async functions (stored as static fields)
    public Dictionary<string, FieldBuilder>? TopLevelStaticVars { get; set; }

    // Type information from static analysis
    public TypeMap? TypeMap { get; set; }

    // Dead code analysis results
    public DeadCodeInfo? DeadCode { get; set; }

    // ============================================
    // Parameter Tracking
    // ============================================

    // Parameter tracking (name -> arg index)
    private readonly Dictionary<string, int> _parameters = [];
    private readonly Dictionary<string, Type> _parameterTypes = [];

    // ============================================
    // Loop and Exception Block Control
    // ============================================

    // Loop control labels (with optional label name for labeled statements)
    public Stack<(Label BreakLabel, Label ContinueLabel, string? LabelName)> LoopLabels { get; } = new();

    /// <summary>
    /// Label name awaiting attachment to the next loop's own break/continue targets.
    /// Set by <c>EmitLabeledStatement</c> when the labeled statement is a loop, and consumed
    /// by that loop's <c>EnterLoop</c> (here or in an emitter override). This lets
    /// <c>continue &lt;label&gt;</c> branch to the loop's real continue point — a for-loop's
    /// increment, a while's condition — instead of a point ahead of the loop's initializer,
    /// which would re-run it forever (#558).
    /// </summary>
    public string? PendingLoopLabel { get; set; }

    /// <summary>
    /// Returns the pending labeled-loop name and clears it, so it attaches to exactly one loop.
    /// </summary>
    public string? TakePendingLoopLabel()
    {
        var label = PendingLoopLabel;
        PendingLoopLabel = null;
        return label;
    }

    // Hoisted array type caches: stack of per-loop dictionaries mapping
    // variable name → (typed local, descriptor) for arrays whose isinst
    // check has been hoisted to the loop preamble.
    public Stack<Dictionary<string, HoistedArrayEntry>> HoistedArrayCaches { get; } = new();

    /// <summary>
    /// Looks up a hoisted array cache entry for the given variable name,
    /// searching from innermost to outermost loop scope.
    /// </summary>
    public HoistedArrayEntry? TryGetHoistedArray(string variableName)
    {
        foreach (var cache in HoistedArrayCaches)
        {
            if (cache.TryGetValue(variableName, out var entry))
                return entry;
        }
        return null;
    }

    // Exception block tracking for proper return handling
    public int ExceptionBlockDepth { get; set; } = 0;
    public LocalBuilder? ReturnValueLocal { get; set; }
    public Label ReturnLabel { get; set; }

    // ============================================
    // Constructor and Core Methods
    // ============================================

    public CompilationContext(
        ILGenerator il,
        TypeMapper typeMapper,
        Dictionary<string, MethodBuilder> functions,
        Dictionary<string, TypeBuilder> classes,
        TypeProvider? types = null)
    {
        IL = il;
        TypeMapper = typeMapper;
        Functions = functions;
        Classes = classes;
        Types = types ?? TypeProvider.Runtime;
        Locals = new LocalsManager(il);
        ILBuilder = new ValidatedILBuilder(il, Types);
    }

    /// <summary>
    /// Updates the validated IL builder for a new ILGenerator (when switching methods).
    /// Call this when the IL property changes to a new method's generator.
    /// </summary>
    public void UpdateILBuilder(ILGenerator newIL)
    {
        ILBuilder = new ValidatedILBuilder(newIL, Types);
    }

    public void DefineParameter(string name, int argIndex, Type? paramType = null)
    {
        _parameters[name] = argIndex;
        if (paramType != null)
        {
            _parameterTypes[name] = paramType;
        }
    }

    public bool TryGetParameter(string name, out int argIndex)
    {
        return _parameters.TryGetValue(name, out argIndex);
    }

    public bool TryGetParameterType(string name, out Type? paramType)
    {
        if (_parameterTypes.TryGetValue(name, out var type))
        {
            paramType = type;
            return true;
        }
        paramType = null;
        return false;
    }

    /// <summary>
    /// With a boxed object on the stack destined for a Starg into
    /// <paramref name="paramName"/>'s arg slot, converts it to the slot's
    /// declared type when the parameter is typed: Unbox_Any for value types,
    /// castclass for reference types. No-op for untyped (object) slots.
    /// Captured-parameter dual-writes need this — storing the boxed object
    /// straight into a double/string slot fails IL verification
    /// (StackUnexpected family, see #284).
    /// </summary>
    public void EmitConvertForParamSlot(ILGenerator il, string paramName)
    {
        if (!_parameterTypes.TryGetValue(paramName, out var pt) || pt == Types.Object)
            return;
        if (pt.IsValueType)
            il.Emit(OpCodes.Unbox_Any, pt);
        else
            il.Emit(OpCodes.Castclass, pt);
    }

    public void ClearParameters()
    {
        _parameters.Clear();
        _parameterTypes.Clear();
    }

    public void EnterLoop(Label breakLabel, Label continueLabel, string? labelName = null)
    {
        // An unlabeled EnterLoop call adopts any label parked by an enclosing labeled
        // statement, so the loop's own continue/break targets carry the label (#558).
        LoopLabels.Push((breakLabel, continueLabel, labelName ?? TakePendingLoopLabel()));
    }

    public void ExitLoop()
    {
        LoopLabels.Pop();
    }

    public (Label BreakLabel, Label ContinueLabel, string? LabelName)? CurrentLoop =>
        LoopLabels.Count > 0 ? LoopLabels.Peek() : null;

    /// <summary>
    /// Find a loop label by name (for labeled break/continue).
    /// </summary>
    public (Label BreakLabel, Label ContinueLabel, string? LabelName)? FindLabeledLoop(string labelName)
    {
        foreach (var entry in LoopLabels)
        {
            if (entry.LabelName == labelName)
                return entry;
        }
        return null;
    }
}
