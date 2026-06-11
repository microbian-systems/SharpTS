using System.Reflection.Emit;
using SharpTS.Parsing;
using static SharpTS.Parsing.Expr;

namespace SharpTS.Compilation;

public partial class CompilationContext
{
    // ============================================
    // Closure Support
    // ============================================

    // Closure analyzer for detecting captured variables
    public ClosureAnalyzer? ClosureAnalyzer { get; set; }

    // Arrow function methods (arrow node -> method info)
    public Dictionary<ArrowFunction, MethodBuilder> ArrowMethods { get; set; } = [];

    // Module-scope const → literal-arrow bindings. Iterator-helper fast paths
    // look up `Expr.Variable` callbacks here so `const sq = x => x*x; arr.map(sq)`
    // gets the same direct-delegate dispatch as the inline-arrow form.
    public Dictionary<string, ArrowFunction> ConstArrowBindings { get; set; } = [];

    // Display classes for capturing closures (arrow node -> type builder)
    public Dictionary<ArrowFunction, TypeBuilder> DisplayClasses { get; set; } = [];

    // Display class fields (arrow node -> field mapping)
    public Dictionary<ArrowFunction, Dictionary<string, FieldBuilder>> DisplayClassFields { get; set; } = [];

    // Display class constructors (arrow node -> constructor)
    public Dictionary<ArrowFunction, ConstructorBuilder> DisplayClassConstructors { get; set; } = [];

    // For capturing closures: current display class instance local
    public LocalBuilder? DisplayClassLocal { get; set; }

    // For capturing closures: field mapping (variable name -> field)
    public Dictionary<string, FieldBuilder>? CapturedFields { get; set; }

    // ============================================
    // Entry-Point Display Class (for captured top-level variables)
    // ============================================

    /// <summary>
    /// Display class type for entry-point captured variables.
    /// When top-level variables are captured by closures, they're stored here
    /// instead of static fields, so modifications in closures are visible to outer code.
    /// </summary>
    public TypeBuilder? EntryPointDisplayClass { get; set; }

    /// <summary>
    /// Constructor for the entry-point display class.
    /// </summary>
    public ConstructorBuilder? EntryPointDisplayClassCtor { get; set; }

    /// <summary>
    /// Fields in the entry-point display class (variable name -> field).
    /// </summary>
    public Dictionary<string, FieldBuilder>? EntryPointDisplayClassFields { get; set; }

    /// <summary>
    /// Local variable holding the entry-point display class instance.
    /// Used in entry point methods for direct access.
    /// </summary>
    public LocalBuilder? EntryPointDisplayClassLocal { get; set; }

    /// <summary>
    /// Static field on $Program that holds the entry-point display class instance.
    /// Used by module init methods to access captured top-level variables.
    /// </summary>
    public FieldBuilder? EntryPointDisplayClassStaticField { get; set; }

    /// <summary>
    /// Set of top-level variable names that are captured by closures.
    /// These use the entry-point display class instead of static fields.
    /// </summary>
    public HashSet<string>? CapturedTopLevelVars { get; set; }

    /// <summary>
    /// Maps arrow functions to their $entryPointDC field (if they capture top-level vars).
    /// Used when creating capturing arrows to populate the reference to the entry-point display class.
    /// </summary>
    public Dictionary<ArrowFunction, FieldBuilder>? ArrowEntryPointDCFields { get; set; }

    /// <summary>
    /// When inside an arrow body, this is the field that holds the reference to the entry-point display class.
    /// Used for accessing captured top-level variables through the $entryPointDC field.
    /// </summary>
    public FieldBuilder? CurrentArrowEntryPointDCField { get; set; }

    // ============================================
    // Function-Level Display Class (for captured function-local variables)
    // ============================================

    /// <summary>
    /// Local variable holding the current function's display class instance.
    /// Used when function-local variables are captured by inner closures.
    /// </summary>
    public LocalBuilder? FunctionDisplayClassLocal { get; set; }

    /// <summary>
    /// Fields in the current function's display class (variable name -> field).
    /// These are local variables that are captured by inner arrow functions.
    /// </summary>
    public Dictionary<string, FieldBuilder>? FunctionDisplayClassFields { get; set; }

    /// <summary>
    /// Set of variable names that are stored in the function display class.
    /// Used to redirect local variable access to display class fields.
    /// </summary>
    public HashSet<string>? CapturedFunctionLocals { get; set; }

    /// <summary>
    /// Maps arrow functions to their $functionDC field (if they capture function-level vars).
    /// Used when creating capturing arrows to populate the reference to the function display class.
    /// </summary>
    public Dictionary<ArrowFunction, FieldBuilder>? ArrowFunctionDCFields { get; set; }

    /// <summary>
    /// When inside an arrow body, this is the field that holds the reference to the function display class.
    /// Used for accessing captured function-level variables through the $functionDC field.
    /// </summary>
    public FieldBuilder? CurrentArrowFunctionDCField { get; set; }

    // ============================================
    // Arrow Scope Display Class (for arrow-local vars captured by nested arrows)
    // ============================================

    /// <summary>
    /// Local variable holding the current arrow's scope display class instance.
    /// Used when arrow-local variables are captured by inner closures.
    /// </summary>
    public LocalBuilder? ArrowScopeDisplayClassLocal { get; set; }

    /// <summary>
    /// Fields in the current arrow's scope display class (variable name -> field).
    /// These are local variables that are captured by inner arrow functions.
    /// </summary>
    public Dictionary<string, FieldBuilder>? ArrowScopeDisplayClassFields { get; set; }

    /// <summary>
    /// Set of variable names that are stored in the arrow scope display class.
    /// Used to redirect local variable access to display class fields.
    /// </summary>
    public HashSet<string>? CapturedArrowLocals { get; set; }

    /// <summary>
    /// Maps arrow functions to their $arrowDC field (if they capture arrow-scope vars).
    /// Used when creating capturing arrows to populate the reference to the arrow scope display class.
    /// </summary>
    public Dictionary<ArrowFunction, FieldBuilder>? ArrowScopeDCFields { get; set; }

    /// <summary>
    /// When inside a nested arrow body, this is the field that holds the reference to the parent arrow scope display class.
    /// Used for accessing captured arrow-level variables through the $arrowDC field.
    /// </summary>
    public FieldBuilder? CurrentArrowScopeDCField { get; set; }

    /// <summary>
    /// Field map of the PARENT arrow's scope display class (variable name -> field),
    /// accessible via <see cref="CurrentArrowScopeDCField"/>. Set independently of
    /// <see cref="ArrowScopeDisplayClassFields"/> (which holds the CURRENT arrow's
    /// own DC fields) so that arrows which both own a DC and chain through a
    /// parent's DC can resolve captured variables from either scope.
    /// </summary>
    public Dictionary<string, FieldBuilder>? ParentArrowScopeDisplayClassFields { get; set; }

    /// <summary>
    /// Set of variable names that are stored in the PARENT arrow's scope display
    /// class, reachable through <see cref="CurrentArrowScopeDCField"/>.
    /// </summary>
    public HashSet<string>? ParentArrowCapturedLocals { get; set; }

    /// <summary>
    /// A captured variable's live access path when it lives on an ancestor
    /// arrow's scope DC referenced by an EXTRA (non-primary) reference field:
    /// load <see cref="RefField"/> off the current display class (arg0), then
    /// load/store <see cref="VarField"/> on that scope DC instance.
    /// </summary>
    public readonly record struct ExtraScopeBinding(FieldBuilder RefField, FieldBuilder VarField);

    /// <summary>
    /// Per-name live bindings for captured variables whose source scope DC is
    /// referenced by an extra reference field on the current closure's display
    /// class. Populated from the analyzer's capture-source record, so entries
    /// are shadow-correct for THIS closure. Checked by LocalVariableResolver
    /// after the primary parent-DC path.
    /// </summary>
    public Dictionary<string, ExtraScopeBinding>? ExtraArrowScopeBindings { get; set; }

    /// <summary>
    /// The current closure's own EXTRA ancestor-scope reference fields, keyed
    /// by source arrow. Used to chain references into closures created inside
    /// this body (type-matched against the new closure's reference fields).
    /// </summary>
    public Dictionary<ArrowFunction, FieldBuilder>? CurrentArrowScopeDCExtraFields { get; set; }

    /// <summary>
    /// Global map of every arrow's EXTRA ancestor-scope reference fields
    /// (closure arrow → source arrow → field). The creation-site emitter
    /// populates these alongside the primary $arrowDC field.
    /// </summary>
    public Dictionary<ArrowFunction, Dictionary<ArrowFunction, FieldBuilder>>? ArrowScopeDCExtraFieldsByArrow { get; set; }

    // ============================================
    // Inner Function Support
    // ============================================

    /// <summary>
    /// Maps inner Stmt.Function nodes to their compiled methods.
    /// </summary>
    public Dictionary<Stmt.Function, MethodBuilder>? InnerFunctionMethods { get; set; }

    /// <summary>
    /// Maps inner Stmt.Function nodes to their display class types (capturing only).
    /// </summary>
    public Dictionary<Stmt.Function, TypeBuilder>? InnerFunctionDisplayClasses { get; set; }

    /// <summary>
    /// Maps inner Stmt.Function nodes to their display class field mappings.
    /// </summary>
    public Dictionary<Stmt.Function, Dictionary<string, FieldBuilder>>? InnerFunctionDCFields { get; set; }

    /// <summary>
    /// Maps inner Stmt.Function nodes to their display class constructors.
    /// </summary>
    public Dictionary<Stmt.Function, ConstructorBuilder>? InnerFunctionDCCtors { get; set; }

    /// <summary>
    /// Maps inner Stmt.Function nodes to their $entryPointDC fields.
    /// </summary>
    public Dictionary<Stmt.Function, FieldBuilder>? InnerFunctionEntryPointDCFields { get; set; }

    /// <summary>
    /// Maps inner Stmt.Function nodes to their $functionDC fields.
    /// </summary>
    public Dictionary<Stmt.Function, FieldBuilder>? InnerFunctionFunctionDCFields { get; set; }

    /// <summary>
    /// Maps inner function names to their compiled methods for the current scope.
    /// Used for direct calls and variable references within inner function bodies.
    /// </summary>
    public Dictionary<string, MethodBuilder>? InnerFunctionMethodsByName { get; set; }

    /// <summary>
    /// Maps inner function names to their display class types (for capturing inner functions).
    /// Used together with InnerFunctionMethodsByName for proper call dispatch.
    /// </summary>
    public Dictionary<string, TypeBuilder>? InnerFunctionDisplayClassesByName { get; set; }
}
