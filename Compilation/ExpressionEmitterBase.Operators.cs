using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// Shared operator emission (LogicalSet, LogicalSetIndex, CompoundSet, CompoundSetIndex,
/// DynamicImport, ImportMeta) previously duplicated across all 4 state machine emitters.
/// ILEmitter keeps its own overrides since it uses ValidatedILBuilder and EmitBoxIfNeeded.
/// </summary>
public abstract partial class ExpressionEmitterBase
{
    /// <summary>
    /// Emits the logical condition check for &&=, ||=, ??= operators.
    /// After this, the stack has the current value and control flows to either
    /// skipLabel (keep current) or falls through (assign new value).
    /// </summary>
    private void EmitLogicalConditionCheck(TokenType operatorType, Label skipLabel)
    {
        switch (operatorType)
        {
            case TokenType.AND_AND_EQUAL:
                IL.Emit(OpCodes.Call, Ctx.Runtime!.IsTruthy);
                IL.Emit(OpCodes.Brfalse, skipLabel);
                break;
            case TokenType.OR_OR_EQUAL:
                IL.Emit(OpCodes.Call, Ctx.Runtime!.IsTruthy);
                IL.Emit(OpCodes.Brtrue, skipLabel);
                break;
            case TokenType.QUESTION_QUESTION_EQUAL:
                var assignLabel = IL.DefineLabel();
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Brfalse, assignLabel);
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Isinst, Ctx.Runtime!.UndefinedType);
                IL.Emit(OpCodes.Brtrue, assignLabel);
                // Not nullish - pop extra value and skip assignment
                IL.Emit(OpCodes.Pop);
                IL.Emit(OpCodes.Br, skipLabel);
                IL.MarkLabel(assignLabel);
                // At assignLabel we have [value, value], pop one to match other cases
                IL.Emit(OpCodes.Pop);
                break;
        }
    }

    /// <summary>
    /// Emits a compound operation (+= -= *= /= %=, etc.) given left and right values on stack.
    /// Delegates to StateMachineEmitHelpers.EmitCompoundOperation.
    /// </summary>
    protected void EmitCompoundOperation(TokenType opType)
    {
        _helpers.EmitCompoundOperation(opType, Ctx.Runtime!.Add);
    }

    /// <summary>
    /// Stores the value on top of the stack into the named variable.
    /// Checks hoisted fields, locals, CapturedTopLevelVars, and TopLevelStaticVars.
    /// AsyncArrowMoveNextEmitter overrides this with its richer variable resolution
    /// (parameters, captured fields, transitive captures).
    /// </summary>
    /// <remarks>
    /// Expects the value to store on top of the evaluation stack.
    /// Consumes the value from the stack.
    /// </remarks>
    protected virtual void EmitStoreVariable(string name)
    {
        var field = GetHoistedVariableField(name);
        if (field != null)
        {
            // Hoisted variable - store via state machine field (this.field = value)
            var temp = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, temp);
            IL.Emit(OpCodes.Ldarg_0);  // Load state machine 'this'
            IL.Emit(OpCodes.Ldloc, temp);
            IL.Emit(OpCodes.Stfld, field);
        }
        else if (Ctx.Locals.TryGetLocal(name, out var local))
        {
            IL.Emit(OpCodes.Stloc, local);
        }
        else if (Ctx.CapturedTopLevelVars?.Contains(name) == true &&
                 Ctx.EntryPointDisplayClassFields?.TryGetValue(name, out var entryPointField) == true &&
                 Ctx.EntryPointDisplayClassStaticField != null)
        {
            var temp = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, temp);
            IL.Emit(OpCodes.Ldsfld, Ctx.EntryPointDisplayClassStaticField);
            IL.Emit(OpCodes.Ldloc, temp);
            IL.Emit(OpCodes.Stfld, entryPointField);
        }
        else if (Ctx.TopLevelStaticVars?.TryGetValue(name, out var topLevelField) == true)
        {
            IL.Emit(OpCodes.Stsfld, topLevelField);
        }
    }

    /// <summary>
    /// variable += value / variable -= value / etc.
    /// Emits value first (safe for await/yield), loads current, applies op, stores result.
    /// </summary>
    protected virtual void EmitCompoundAssign(Expr.CompoundAssign ca)
    {
        string name = ca.Name.Lexeme;

        // Emit value first — it may contain await/yield which clears the stack
        EmitExpression(ca.Value);
        EnsureBoxed();
        var valueTemp = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, valueTemp);

        // Load current variable value
        EmitVariable(new Expr.Variable(ca.Name));
        EnsureBoxed();

        // Load the RHS value back
        IL.Emit(OpCodes.Ldloc, valueTemp);

        // Apply compound operation (stack: [current, value] → [result])
        EmitCompoundOperation(ca.Operator.Type);

        // Dup result for expression value, then store
        IL.Emit(OpCodes.Dup);
        EmitStoreVariable(name);

        SetStackUnknown();
    }

    /// <summary>
    /// variable &&= value / variable ||= value / variable ??= value
    /// </summary>
    protected virtual void EmitLogicalAssign(Expr.LogicalAssign la)
    {
        string name = la.Name.Lexeme;
        var endLabel = IL.DefineLabel();

        // Load current value
        EmitVariable(new Expr.Variable(la.Name));
        EnsureBoxed();
        IL.Emit(OpCodes.Dup);

        // Check condition — may skip to endLabel (keeping current value on stack)
        EmitLogicalConditionCheck(la.Operator.Type, endLabel);

        // Condition says: assign new value
        IL.Emit(OpCodes.Pop); // Pop current value

        EmitExpression(la.Value);
        EnsureBoxed();
        IL.Emit(OpCodes.Dup);
        EmitStoreVariable(name);

        IL.MarkLabel(endLabel);
        SetStackUnknown();
    }

    /// <summary>
    /// ++variable / --variable (returns new value)
    /// </summary>
    protected virtual void EmitPrefixIncrement(Expr.PrefixIncrement pi)
    {
        if (pi.Operand is Expr.Variable v)
        {
            string name = v.Name.Lexeme;
            double delta = pi.Operator.Type == TokenType.PLUS_PLUS ? 1.0 : -1.0;

            // Load current value, convert to double, add delta, box.
            // ConvertToNumber implements ECMA-262 ToNumber (undefined→NaN);
            // Convert.ToDouble throws InvalidCastException on $Undefined (#190).
            EmitVariable(v);
            EnsureBoxed();
            IL.Emit(OpCodes.Call, Ctx.Runtime?.ConvertToNumber ?? Types.ConvertToDoubleFromObject);
            IL.Emit(OpCodes.Ldc_R8, delta);
            IL.Emit(OpCodes.Add);
            IL.Emit(OpCodes.Box, typeof(double));

            // Dup new value (expression result), then store
            IL.Emit(OpCodes.Dup);
            EmitStoreVariable(name);
        }
        SetStackUnknown();
    }

    /// <summary>
    /// variable++ / variable-- (returns original value)
    /// </summary>
    protected virtual void EmitPostfixIncrement(Expr.PostfixIncrement poi)
    {
        if (poi.Operand is Expr.Variable v)
        {
            string name = v.Name.Lexeme;
            double delta = poi.Operator.Type == TokenType.PLUS_PLUS ? 1.0 : -1.0;

            // Load current value (this is the expression result — original value)
            EmitVariable(v);
            EnsureBoxed();
            IL.Emit(OpCodes.Dup);  // Dup original for expression result

            // Convert to double, add delta, box (ToNumber semantics — see EmitPrefixIncrement)
            IL.Emit(OpCodes.Call, Ctx.Runtime?.ConvertToNumber ?? Types.ConvertToDoubleFromObject);
            IL.Emit(OpCodes.Ldc_R8, delta);
            IL.Emit(OpCodes.Add);
            IL.Emit(OpCodes.Box, typeof(double));

            // Store incremented value (original remains on stack)
            EmitStoreVariable(name);
        }
        SetStackUnknown();
    }

    /// <summary>
    /// obj.prop &&= value / obj.prop ||= value / obj.prop ??= value
    /// </summary>
    protected virtual void EmitLogicalSet(Expr.LogicalSet ls)
    {
        var skipLabel = IL.DefineLabel();
        var endLabel = IL.DefineLabel();

        // Store object
        EmitExpression(ls.Object);
        EnsureBoxed();
        var objLocal = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, objLocal);

        // Get current value
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Ldstr, ls.Name.Lexeme);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
        IL.Emit(OpCodes.Dup);

        EmitLogicalConditionCheck(ls.Operator.Type, skipLabel);

        IL.Emit(OpCodes.Pop);
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Ldstr, ls.Name.Lexeme);
        EmitExpression(ls.Value);
        EnsureBoxed();
        var resultLocal = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Dup);
        IL.Emit(OpCodes.Stloc, resultLocal);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.SetProperty);
        IL.Emit(OpCodes.Ldloc, resultLocal);
        IL.Emit(OpCodes.Br, endLabel);

        IL.MarkLabel(skipLabel);
        IL.MarkLabel(endLabel);
        SetStackUnknown();
    }

    /// <summary>
    /// obj[index] &&= value / obj[index] ||= value / obj[index] ??= value
    /// </summary>
    protected virtual void EmitLogicalSetIndex(Expr.LogicalSetIndex lsi)
    {
        var skipLabel = IL.DefineLabel();
        var endLabel = IL.DefineLabel();

        // Store object and index
        EmitExpression(lsi.Object);
        EnsureBoxed();
        var objLocal = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, objLocal);

        EmitExpression(lsi.Index);
        EnsureBoxed();
        var indexLocal = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, indexLocal);

        // Get current value
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Ldloc, indexLocal);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.GetIndex);
        IL.Emit(OpCodes.Dup);

        EmitLogicalConditionCheck(lsi.Operator.Type, skipLabel);

        IL.Emit(OpCodes.Pop);
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Ldloc, indexLocal);
        EmitExpression(lsi.Value);
        EnsureBoxed();
        var resultLocal = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Dup);
        IL.Emit(OpCodes.Stloc, resultLocal);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.SetIndex);
        IL.Emit(OpCodes.Ldloc, resultLocal);
        IL.Emit(OpCodes.Br, endLabel);

        IL.MarkLabel(skipLabel);
        IL.MarkLabel(endLabel);
        SetStackUnknown();
    }

    /// <summary>
    /// obj.prop += value (compound assignment on property)
    /// </summary>
    protected virtual void EmitCompoundSet(Expr.CompoundSet cs)
    {
        // Handle static field compound assignment: Class.field += x
        if (cs.Object is Expr.Variable classVar &&
            Ctx.Classes.TryGetValue(Ctx.ResolveClassName(classVar.Name.Lexeme), out _))
        {
            string resolvedClassName = Ctx.ResolveClassName(classVar.Name.Lexeme);
            if (Ctx.ClassRegistry!.TryGetStaticField(resolvedClassName, cs.Name.Lexeme, out var staticField))
            {
                EmitExpression(cs.Value);
                EnsureBoxed();
                var valueTemp = IL.DeclareLocal(typeof(object));
                IL.Emit(OpCodes.Stloc, valueTemp);

                IL.Emit(OpCodes.Ldsfld, staticField!);
                IL.Emit(OpCodes.Ldloc, valueTemp);
                EmitCompoundOperation(cs.Operator.Type);
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Stsfld, staticField!);
                SetStackUnknown();
                return;
            }
        }

        // Dynamic property compound assignment
        EmitExpression(cs.Object);
        EnsureBoxed();
        var objTemp = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, objTemp);

        IL.Emit(OpCodes.Ldloc, objTemp);
        IL.Emit(OpCodes.Ldstr, cs.Name.Lexeme);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);

        EmitExpression(cs.Value);
        EnsureBoxed();
        EmitCompoundOperation(cs.Operator.Type);

        var resultLocal = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, resultLocal);

        IL.Emit(OpCodes.Ldloc, objTemp);
        IL.Emit(OpCodes.Ldstr, cs.Name.Lexeme);
        IL.Emit(OpCodes.Ldloc, resultLocal);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.SetProperty);

        IL.Emit(OpCodes.Ldloc, resultLocal);
        SetStackUnknown();
    }

    /// <summary>
    /// obj[index] += value (compound assignment on index)
    /// </summary>
    protected virtual void EmitCompoundSetIndex(Expr.CompoundSetIndex csi)
    {
        EmitExpression(csi.Object);
        EnsureBoxed();
        var objTemp = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, objTemp);

        EmitExpression(csi.Index);
        EnsureBoxed();
        var indexTemp = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, indexTemp);

        IL.Emit(OpCodes.Ldloc, objTemp);
        IL.Emit(OpCodes.Ldloc, indexTemp);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.GetIndex);

        EmitExpression(csi.Value);
        EnsureBoxed();
        EmitCompoundOperation(csi.Operator.Type);

        var resultLocal = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, resultLocal);

        IL.Emit(OpCodes.Ldloc, objTemp);
        IL.Emit(OpCodes.Ldloc, indexTemp);
        IL.Emit(OpCodes.Ldloc, resultLocal);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.SetIndex);

        IL.Emit(OpCodes.Ldloc, resultLocal);
        SetStackUnknown();
    }

    /// <summary>
    /// import('path') - dynamic import expression.
    /// Generator emitters override this to return null (dynamic import not supported in generators).
    /// </summary>
    protected virtual void EmitDynamicImport(Expr.DynamicImport di)
    {
        EmitExpression(di.PathExpression);
        EnsureBoxed();
        IL.Emit(OpCodes.Call, Types.ConvertToStringFromObject);
        IL.Emit(OpCodes.Ldstr, Ctx.CurrentModulePath ?? "");
        IL.Emit(OpCodes.Call, Ctx.Runtime!.DynamicImportModule);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.WrapTaskAsPromise);
        SetStackUnknown();
    }

    /// <summary>
    /// import.meta - returns object with url, filename, dirname properties.
    /// </summary>
    protected virtual void EmitImportMeta(Expr.ImportMeta im)
    {
        string path = Ctx.CurrentModulePath ?? "";
        string url = path;
        if (!string.IsNullOrEmpty(url) && !url.StartsWith("file://"))
        {
            url = "file:///" + url.Replace("\\", "/");
        }
        string dirname = string.IsNullOrEmpty(path) ? "" : System.IO.Path.GetDirectoryName(path) ?? "";

        IL.Emit(OpCodes.Newobj, Types.DictionaryStringObjectCtor);

        IL.Emit(OpCodes.Dup);
        IL.Emit(OpCodes.Ldstr, "url");
        IL.Emit(OpCodes.Ldstr, url);
        IL.Emit(OpCodes.Callvirt, Types.DictionaryStringObjectSetItem);

        IL.Emit(OpCodes.Dup);
        IL.Emit(OpCodes.Ldstr, "filename");
        IL.Emit(OpCodes.Ldstr, path);
        IL.Emit(OpCodes.Callvirt, Types.DictionaryStringObjectSetItem);

        IL.Emit(OpCodes.Dup);
        IL.Emit(OpCodes.Ldstr, "dirname");
        IL.Emit(OpCodes.Ldstr, dirname);
        IL.Emit(OpCodes.Callvirt, Types.DictionaryStringObjectSetItem);

        IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateObject);
        SetStackUnknown();
    }
}
