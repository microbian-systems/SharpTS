using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class CompilationContext
{
    // ============================================
    // Function Compilation State
    // ============================================

    public Dictionary<string, MethodBuilder> Functions { get; }

    /// <summary>
    /// The $Program type where top-level functions are defined.
    /// Used for GetMethodFromHandle to properly resolve MethodBuilder tokens in persisted assemblies.
    /// </summary>
    public TypeBuilder? ProgramType { get; set; }

    // Rest parameter info: function name -> (restParamIndex, regularParamCount)
    // If a function has a rest param, restParamIndex is its index, regularParamCount is non-rest param count
    public Dictionary<string, (int RestParamIndex, int RegularParamCount)>? FunctionRestParams { get; set; }

    // Function overloads for default parameters: function name -> list of overload methods
    public Dictionary<string, List<MethodBuilder>>? FunctionOverloads { get; set; }

    // Method overloads for default parameters: class name -> method name -> list of overload methods
    public Dictionary<string, Dictionary<string, List<MethodBuilder>>>? MethodOverloads { get; set; }

    // Track generic params per function for instantiation
    public Dictionary<string, GenericTypeParameterBuilder[]>? FunctionGenericParams { get; set; }

    // Track which functions are generic definitions
    public Dictionary<string, bool>? IsGenericFunction { get; set; }

    /// <summary>
    /// Qualified names of functions whose body references JS <c>arguments</c> (directly,
    /// or through a nested arrow — arrows inherit their enclosing non-arrow's binding).
    /// Direct call sites to any of these must publish the caller's raw arg array to the
    /// <c>$TSFunction._currentArguments</c> thread-static before emitting <c>OpCodes.Call</c>,
    /// so the prologue can materialize <c>arguments</c> including values past the declared
    /// arity (lodash overRest pattern; #64). Indirect calls through <c>$TSFunction.Invoke</c>
    /// publish automatically — this set only matters for in-module direct dispatch.
    /// </summary>
    public HashSet<string>? FunctionsCapturingArguments { get; set; }

    /// <summary>
    /// Resolves a simple function name to its qualified name for lookup in the Functions dictionary.
    /// </summary>
    public string ResolveFunctionName(string simpleFunctionName)
    {
        if (FunctionToModule != null && FunctionToModule.TryGetValue(simpleFunctionName, out var modulePath))
        {
            string sanitizedModule = SanitizeModuleName(Path.GetFileNameWithoutExtension(modulePath));
            return $"$M_{sanitizedModule}_{simpleFunctionName}";
        }
        return simpleFunctionName;
    }

    /// <summary>
    /// Gets the qualified function name for the current module context.
    /// </summary>
    public string GetQualifiedFunctionName(string simpleFunctionName)
    {
        if (CurrentModulePath == null)
            return simpleFunctionName;

        string sanitizedModule = SanitizeModuleName(Path.GetFileNameWithoutExtension(CurrentModulePath));
        return $"$M_{sanitizedModule}_{simpleFunctionName}";
    }
}
