using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Diagnostic helper: inspects an ILGenerator for labels that a branch references but that were never marked.
/// Unmarked, branched labels cause "Label N has not been marked" during
/// <see cref="PersistedAssemblyBuilder.GenerateMetadata"/>, and that error surfaces without any method
/// context. This helper runs immediately after a method body is emitted, so we can raise a precise,
/// actionable error naming the offending method.
/// Defined-but-unused labels do NOT trigger the error (the metadata writer only validates branch targets),
/// so this validator mirrors that: it only flags labels that are both branched to and unmarked.
/// </summary>
internal static class ILLabelValidator
{
    private static FieldInfo? _labelTableField;
    private static FieldInfo? _positionField;
    private static FieldInfo? _metaLabelField;
    private static FieldInfo? _cfBuilderField;
    private static FieldInfo? _branchesField;
    private static FieldInfo? _branchLabelField;

    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPTS_VALIDATE_LABELS") == "1";

    public static void Validate(ILGenerator il, string methodContext)
    {
        if (!Enabled) return;

        var unmarked = FindUnmarkedBranchedLabelIndices(il);
        if (unmarked.Count == 0) return;

        throw new InvalidOperationException(
            $"Unmarked IL label(s) in '{methodContext}': indices [{string.Join(",", unmarked)}]. " +
            "A branch references a label that was never marked with MarkLabel.");
    }

    /// <summary>
    /// Sweep all methods in the given TypeBuilders and validate any whose body was produced via
    /// an <see cref="ILGenerator"/>. This is a safety-net after targeted per-emit validation — if
    /// a method body emission path forgets to invoke <see cref="Validate"/> explicitly, this sweep
    /// still catches unmarked-and-branched labels before <see cref="PersistedAssemblyBuilder.GenerateMetadata"/>
    /// surfaces the same error without method context.
    /// </summary>
    /// <summary>
    /// Reflects into <see cref="ModuleBuilder"/> to enumerate every <see cref="TypeBuilder"/> it holds,
    /// including state-machine types and display classes not tracked by the caller.
    /// </summary>
    public static IEnumerable<TypeBuilder> AllTypesFromModule(ModuleBuilder mb)
    {
        var field = mb.GetType().GetField("_typeDefinitions", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(mb) is not IEnumerable list) yield break;
        foreach (var t in list)
        {
            if (t is TypeBuilder tb) yield return tb;
        }
    }

    public static void SweepAllTypes(IEnumerable<TypeBuilder> types)
    {
        if (!Enabled) return;

        foreach (var tb in types.Distinct())
        {
            if (tb is null) continue;
            // DefinedMethods on a TypeBuilder returns MethodBuilder instances for methods defined on it.
            foreach (var mb in EnumerateMethodBuilders(tb))
            {
                // Only methods that actually have a body (i.e. not abstract/pinvoke) have an IL stream.
                if ((mb.Attributes & MethodAttributes.Abstract) != 0) continue;
                if ((mb.Attributes & MethodAttributes.PinvokeImpl) != 0) continue;
                if ((mb.GetMethodImplementationFlags() & MethodImplAttributes.Runtime) != 0) continue;

                ILGenerator il;
                try { il = mb.GetILGenerator(); }
                catch { continue; }

                var unmarked = FindUnmarkedBranchedLabelIndices(il);
                if (unmarked.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Unmarked IL label(s) in '{tb.FullName}::{mb.Name}': indices [{string.Join(",", unmarked)}]. " +
                        "A branch references a label that was never marked with MarkLabel.");
                }
            }
        }
    }

    private static FieldInfo? _methodDefinitionsField;
    private static FieldInfo? _constructorDefinitionsField;

    private static IEnumerable<MethodBuilder> EnumerateMethodBuilders(TypeBuilder tb)
    {
        // Before CreateType() completes, GetMethods() throws NotSupportedException on
        // TypeBuilderImpl. Reach into the internal _methodDefinitions list directly so the
        // pre-finalization sweep can inspect the very ILGenerators whose state will be
        // consumed and cleared by CreateType.
        _methodDefinitionsField ??= tb.GetType().GetField(
            "_methodDefinitions", BindingFlags.NonPublic | BindingFlags.Instance);
        if (_methodDefinitionsField?.GetValue(tb) is IEnumerable list)
        {
            foreach (var m in list)
            {
                if (m is MethodBuilder mb) yield return mb;
            }
        }
    }

    private static IEnumerable<ConstructorBuilder> EnumerateConstructorBuilders(TypeBuilder tb)
    {
        _constructorDefinitionsField ??= tb.GetType().GetField(
            "_constructorDefinitions", BindingFlags.NonPublic | BindingFlags.Instance);
        if (_constructorDefinitionsField?.GetValue(tb) is IEnumerable list)
        {
            foreach (var c in list)
            {
                if (c is ConstructorBuilder cb) yield return cb;
            }
        }
    }

    /// <summary>
    /// Sweep constructors separately — ConstructorBuilder is a sibling of MethodBuilder, not a subclass.
    /// </summary>
    public static void SweepConstructors(IEnumerable<TypeBuilder> types)
    {
        if (!Enabled) return;
        foreach (var tb in types.Distinct())
        {
            if (tb is null) continue;
            foreach (var cb in EnumerateConstructorBuilders(tb))
            {
                ILGenerator il;
                try { il = cb.GetILGenerator(); }
                catch { continue; }
                var unmarked = FindUnmarkedBranchedLabelIndices(il);
                if (unmarked.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Unmarked IL label(s) in '{tb.FullName}::.ctor': indices [{string.Join(",", unmarked)}]. " +
                        "A branch references a label that was never marked with MarkLabel.");
                }
            }
        }
    }

    private static List<int> FindUnmarkedBranchedLabelIndices(ILGenerator il)
    {
        var result = new List<int>();
        _labelTableField ??= il.GetType().GetField("_labelTable", BindingFlags.NonPublic | BindingFlags.Instance);
        if (_labelTableField is null) return result;
        if (_labelTableField.GetValue(il) is not IDictionary table) return result;

        // Collect branched-to label Ids (LabelHandle stores an Int32 Id in <Id>k__BackingField).
        var branchedLabelIds = new HashSet<int>();
        _cfBuilderField ??= il.GetType().GetField("_cfBuilder", BindingFlags.NonPublic | BindingFlags.Instance);
        var cf = _cfBuilderField?.GetValue(il);
        if (cf is not null)
        {
            _branchesField ??= cf.GetType().GetField("_branches", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_branchesField?.GetValue(cf) is IEnumerable branches)
            {
                foreach (var b in branches)
                {
                    if (b is null) continue;
                    _branchLabelField ??= b.GetType().GetField("Label",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var lbl = _branchLabelField?.GetValue(b);
                    if (lbl is null) continue;
                    var idVal = GetLabelHandleId(lbl);
                    if (idVal.HasValue) branchedLabelIds.Add(idVal.Value);
                }
            }
        }

        int idx = 0;
        foreach (DictionaryEntry entry in table)
        {
            var info = entry.Value;
            if (info is null) { idx++; continue; }

            _positionField ??= info.GetType().GetField("_position", BindingFlags.NonPublic | BindingFlags.Instance);
            _metaLabelField ??= info.GetType().GetField("_metaLabel", BindingFlags.NonPublic | BindingFlags.Instance);

            if (_positionField?.GetValue(info) is int pos && pos < 0)
            {
                var handle = _metaLabelField?.GetValue(info);
                var handleId = handle is not null ? GetLabelHandleId(handle) : null;
                if (handleId.HasValue && branchedLabelIds.Contains(handleId.Value))
                {
                    result.Add(idx);
                }
            }
            idx++;
        }
        return result;
    }

    private static FieldInfo? _labelHandleIdField;
    private static int? GetLabelHandleId(object handle)
    {
        _labelHandleIdField ??= handle.GetType().GetField("<Id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        if (_labelHandleIdField?.GetValue(handle) is int id) return id;
        return null;
    }
}
