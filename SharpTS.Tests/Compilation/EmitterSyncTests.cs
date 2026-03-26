using System.Reflection;
using SharpTS.Compilation;
using Xunit;
using Xunit.Abstractions;

namespace SharpTS.Tests.Compilation;

/// <summary>
/// Validates that state machine emitters don't accidentally override base class methods
/// without justification. When a state machine emitter overrides a virtual method from
/// StatementEmitterBase or ExpressionEmitterBase, it must be for a genuine reason
/// (await/yield handling, return semantics, variable capture, etc.).
///
/// This test catches the most common drift pattern: copy-pasting a method from the base
/// into a state machine emitter "just in case" — which then silently falls out of sync
/// as the base evolves.
/// </summary>
public class EmitterSyncTests
{
    private readonly ITestOutputHelper _output;

    public EmitterSyncTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Methods that state machine emitters are allowed to override, with justification.
    /// Any override not in this allowlist will cause the test to fail, forcing the developer
    /// to either add justification or remove the unnecessary override.
    /// </summary>
    private static readonly Dictionary<Type, HashSet<string>> AllowedOverrides = new()
    {
        [typeof(AsyncMoveNextEmitter)] = new()
        {
            // --- Infrastructure (abstract property/field implementations) ---
            "get_IL",               // Abstract: provides ILGenerator for this emitter
            "get_Ctx",              // Abstract: provides CompilationContext
            "get_Types",            // Abstract: provides TypeProvider
            "get_Resolver",         // Abstract: provides IVariableResolver
            "GetThisField",         // Abstract: provides hoisted 'this' field
            "GetHoistedVariableField", // Hoisted variable fields on state machine
            "DeclareLoopVariable",  // Loop variable hoisting
            "EmitStoreLoopVariable", // Loop variable hoisting
            "SharpTS.Compilation.Emitters.IEmitterContext.get_IL", // IEmitterContext impl
            "SharpTS.Compilation.Emitters.IEmitterContext.SetStackUnknown", // IEmitterContext impl
            "SharpTS.Compilation.Emitters.IEmitterContext.SetStackType",   // IEmitterContext impl
            // --- Genuinely different behavior ---
            "EmitReturn",           // Async return: store result + leave to SetResult label
            "EmitTryCatch",         // Await-aware exception handling with flag-based tracking
            "EmitForOf",            // for-await-of protocol dispatch
            "EmitLabeledStatement", // Labeled continue for for loops (base doesn't handle correctly)
            "EmitAwait",            // Core: suspend/resume state machine
            "EmitArrowFunction",    // Display class in state machine context
            "EmitExpressionAsDouble", // Literal optimization
            "EmitCallPrivate",      // Private method calls in async context
            "EmitGetPrivate",       // Private field access in async context
            "EmitSetPrivate",       // Private field assignment in async context
            "EmitSet",              // Property set with await-safe temp storage
            "EmitNullishCoalescing", // Nullish coalescing with await-safe evaluation
            "EmitTemplateLiteral",  // Template literals with await-safe temp storage
            "EmitTaggedTemplateLiteral", // Tagged template literals in async context
        },
        [typeof(AsyncArrowMoveNextEmitter)] = new()
        {
            // --- Infrastructure ---
            "get_IL",
            "get_Ctx",
            "get_Types",
            "get_Resolver",
            "DeclareLoopVariable",
            "EmitStoreLoopVariable",
            "SharpTS.Compilation.Emitters.IEmitterContext.get_IL",           // IEmitterContext impl
            "SharpTS.Compilation.Emitters.IEmitterContext.SetStackUnknown", // IEmitterContext impl
            "SharpTS.Compilation.Emitters.IEmitterContext.SetStackType",   // IEmitterContext impl
            "EmitLiteral",          // Literal optimization for capture context
            // --- Genuinely different behavior ---
            "EmitReturn",           // Async arrow return: store result + SetResult
            "EmitTryCatch",         // Await-aware exception handling
            "EmitVarDeclaration",   // Capture indirection for outer variables
            "EmitVariable",         // Capture indirection
            "EmitAssign",           // Capture indirection
            "EmitStoreVariable",    // Capture indirection
            "EmitThis",             // Outer state machine capture
            "EmitSuper",            // This field indirection
            "EmitAwait",            // Core: suspend/resume state machine
            "EmitArrowFunction",    // Nested async arrows in state machine
            "EmitExpressionAsDouble", // Literal optimization
        },
        [typeof(GeneratorMoveNextEmitter)] = new()
        {
            // --- Infrastructure ---
            "get_IL",
            "get_Ctx",
            "get_Types",
            "get_Resolver",
            "GetThisField",
            "GetHoistedVariableField",
            "DeclareLoopVariable",
            "EmitStoreLoopVariable",
            // --- Genuinely different behavior ---
            "EmitReturn",           // Generator return: set state -2, return false
            "EmitTryCatch",         // Generator exception handling
            "EmitForOf",            // Hoisted enumerator for yield across loop boundaries
            "EmitYield",            // Core: yield value + suspend
            "EmitSuper",            // This field indirection
            "EmitDynamicImport",    // Dynamic import fallback
        },
        [typeof(AsyncGeneratorMoveNextEmitter)] = new()
        {
            // --- Infrastructure ---
            "get_IL",
            "get_Ctx",
            "get_Types",
            "get_Resolver",
            "GetThisField",
            "GetHoistedVariableField",
            "DeclareLoopVariable",
            "EmitStoreLoopVariable",
            // --- Genuinely different behavior ---
            "EmitReturn",           // Async generator return: store in CurrentField + state -2
            "EmitTryCatch",         // Suspension-aware exception handling (flag-based)
            "EmitForOf",            // for-await-of + hoisted enumerator for yield/await in loops
            "EmitBranchToLabel",    // Leave instead of Br in exception blocks
            "EmitYield",            // Core: yield value + suspend
            "EmitAwait",            // Core: suspend/resume state machine
            "EmitArrowFunction",    // Display class in async generator context
            "EmitSuper",            // This field indirection
        },
    };

    [Fact]
    public void StateMachineEmitters_OnlyOverrideAllowedMethods()
    {
        var baseTypes = new[] { typeof(ExpressionEmitterBase), typeof(StatementEmitterBase) };

        // Collect all virtual/abstract methods from the base classes
        var baseMethods = new HashSet<string>();
        foreach (var baseType in baseTypes)
        {
            foreach (var method in baseType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.IsVirtual && method.DeclaringType == baseType)
                    baseMethods.Add(method.Name);
            }
        }

        var failures = new List<string>();

        foreach (var (emitterType, allowedMethods) in AllowedOverrides)
        {
            var overriddenMethods = emitterType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.IsVirtual && baseMethods.Contains(m.Name))
                .Select(m => m.Name)
                .ToHashSet();

            // Check for overrides that aren't in the allowlist
            var unexpectedOverrides = overriddenMethods.Except(allowedMethods).ToList();
            foreach (var method in unexpectedOverrides)
            {
                failures.Add($"{emitterType.Name} overrides '{method}' without justification. " +
                    $"Either add it to AllowedOverrides with a comment explaining why, " +
                    $"or remove the override and use the base class implementation.");
            }

            // Log allowed overrides for visibility
            foreach (var method in overriddenMethods.Intersect(allowedMethods))
            {
                _output.WriteLine($"  OK: {emitterType.Name}.{method} (allowed override)");
            }
        }

        if (failures.Count > 0)
        {
            _output.WriteLine("\nUnexpected overrides found:");
            foreach (var failure in failures)
                _output.WriteLine($"  FAIL: {failure}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void AllowedOverrides_AreActuallyOverridden()
    {
        // Ensure the allowlist doesn't contain stale entries for methods that were
        // already removed (cleaned up). This prevents the allowlist from growing stale.
        var baseTypes = new[] { typeof(ExpressionEmitterBase), typeof(StatementEmitterBase) };
        var baseMethods = new HashSet<string>();
        foreach (var baseType in baseTypes)
        {
            foreach (var method in baseType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.IsVirtual && method.DeclaringType == baseType)
                    baseMethods.Add(method.Name);
            }
        }

        var staleEntries = new List<string>();

        foreach (var (emitterType, allowedMethods) in AllowedOverrides)
        {
            var actualOverrides = emitterType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.IsVirtual && baseMethods.Contains(m.Name))
                .Select(m => m.Name)
                .ToHashSet();

            var stale = allowedMethods.Except(actualOverrides).ToList();
            foreach (var method in stale)
            {
                staleEntries.Add($"{emitterType.Name} allowlist contains '{method}' but it is not actually overridden. Remove it from AllowedOverrides.");
            }
        }

        if (staleEntries.Count > 0)
        {
            _output.WriteLine("\nStale allowlist entries:");
            foreach (var entry in staleEntries)
                _output.WriteLine($"  STALE: {entry}");
        }

        Assert.Empty(staleEntries);
    }
}
