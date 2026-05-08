using System.Reflection.Emit;
using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

public partial class GeneratorMoveNextEmitter
{
    protected override void EmitYield(Expr.Yield y)
    {
        int stateNumber = _currentYieldState++;
        var resumeLabel = _stateLabels[stateNumber];

        // Handle yield* delegation
        if (y.IsDelegating && y.Value != null)
        {
            EmitYieldStar(y, stateNumber, resumeLabel);
            return;
        }

        // 1. Emit the yield value (or null if no value)
        if (y.Value != null)
        {
            EmitExpression(y.Value);
            EnsureBoxed();
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);
        }

        // 2. Store value in <>2__current field
        var valueTemp = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, valueTemp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, valueTemp);
        _il.Emit(OpCodes.Stfld, _builder.CurrentField);

        // 3. Set state to the resume point
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, stateNumber);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        // 4. Return true (has value)
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(OpCodes.Ret);

        // 5. Mark the resume label (jumped to from state switch)
        _il.MarkLabel(resumeLabel);

        // 6. yield expression evaluates to undefined (null) when resumed
        _il.Emit(OpCodes.Ldnull);
        SetStackUnknown();
    }

    private void EmitYieldStar(Expr.Yield y, int stateNumber, Label resumeLabel)
    {
        // yield* delegates to another iterable
        // We store the enumerator in a field so it survives across MoveNext calls

        var delegatedField = _builder.DelegatedEnumeratorField;
        if (delegatedField == null)
        {
            // Fallback: no field defined, just push null
            _il.Emit(OpCodes.Ldnull);
            SetStackUnknown();
            return;
        }

        // Spill pre-existing stack items into state-machine fields so resumeLabel is reached
        // with stack=0 on both fall-through and state-dispatch paths. Without this, yield*
        // used inside a larger expression (e.g. `(yield* a) + (yield* b)` or
        // `this.prop = yield* expr`) leaves the previous operand on the stack when emission
        // reaches the setup block — making resumeLabel reachable with stack=N from
        // fall-through but stack=0 from the state-dispatch switch at the top of MoveNext,
        // which fails IL verification (CLR rejects with InvalidProgramException at runtime).
        //
        // We track the STATIC STACK TYPE per spill slot by peeking at the last-emitted IL
        // bytes. This matters because the verifier distinguishes `object` from `string` on
        // the stack: if a subsequent `call` expects `string` (e.g., the second argument of
        // `$Runtime.SetProperty(object, string, object)`) and we restore via a plain
        // `ldfld object`, verification fails with "found ref 'object', expected ref 'string'".
        // Slot types are recovered by emitting a `castclass` on restore — Castclass retypes
        // the stack value without affecting the runtime value so long as the spilled object
        // is actually of that type.
        int spillCount = ReadCurrentStackDepth(_il);
        FieldBuilder[]? spillFields = null;
        Type[]? slotTypes = null;
        if (spillCount > 0)
        {
            spillFields = new FieldBuilder[spillCount];
            slotTypes = new Type[spillCount];
            for (int i = spillCount - 1; i >= 0; i--)
            {
                var slotType = PeekTopStackType(_il);
                slotTypes[i] = slotType;
                var temp = _il.DeclareLocal(typeof(object));
                _il.Emit(OpCodes.Stloc, temp);
                var field = _builder.GetOrDefineYieldStarSpillField(stateNumber, i);
                spillFields[i] = field;
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldloc, temp);
                _il.Emit(OpCodes.Stfld, field);
            }
        }

        var moveNext = typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!;
        var current = typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!;
        var getEnumerator = typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!;

        var loopEnd = _il.DefineLabel();
        var hasIteratorLabel = _il.DefineLabel();
        var gotEnumeratorLabel = _il.DefineLabel();

        // Locals
        var iterableLocal = _il.DeclareLocal(typeof(object));
        var iterFnLocal = _il.DeclareLocal(typeof(object));
        var iteratorLocal = _il.DeclareLocal(typeof(object));
        var enumTemp = _il.DeclareLocal(typeof(System.Collections.IEnumerator));

        // Emit the iterable expression
        EmitExpression(y.Value!);
        EnsureBoxed();
        _il.Emit(OpCodes.Stloc, iterableLocal);

        // Handle Map/Set specially - convert to List before iteration. Each
        // arm checks the corresponding $Runtime method MethodBuilder for null;
        // when Map/Set emission is gated off, the dispatch arm is skipped.
        var hasMapEntries = _ctx?.Runtime?.MapEntries != null;
        var hasSetValues = _ctx?.Runtime?.SetValues != null;
        if (hasMapEntries || hasSetValues)
        {
            var afterMapSetLabel = _il.DefineLabel();
            var checkSetLabel = _il.DefineLabel();
            var dictionaryType = typeof(Dictionary<object, object?>);
            var hashSetType = typeof(HashSet<object>);

            if (hasMapEntries)
            {
                // Check if iterable is Dictionary<object, object?> (Map)
                _il.Emit(OpCodes.Ldloc, iterableLocal);
                _il.Emit(OpCodes.Isinst, dictionaryType);
                _il.Emit(OpCodes.Brfalse, checkSetLabel);

                // It's a Map - call MapEntries
                _il.Emit(OpCodes.Ldloc, iterableLocal);
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapEntries);
                _il.Emit(OpCodes.Stloc, iterableLocal);
                _il.Emit(OpCodes.Br, afterMapSetLabel);
            }

            if (hasSetValues)
            {
                // Check if iterable is HashSet<object> (Set)
                _il.MarkLabel(checkSetLabel);
                _il.Emit(OpCodes.Ldloc, iterableLocal);
                _il.Emit(OpCodes.Isinst, hashSetType);
                _il.Emit(OpCodes.Brfalse, afterMapSetLabel);

                // It's a Set - call SetValues
                _il.Emit(OpCodes.Ldloc, iterableLocal);
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetValues);
                _il.Emit(OpCodes.Stloc, iterableLocal);
            }
            else
            {
                // Map-only: still need to mark checkSetLabel so the Map arm's
                // Brfalse has a target.
                _il.MarkLabel(checkSetLabel);
            }

            _il.MarkLabel(afterMapSetLabel);
        }

        // Check for Symbol.iterator on the object (for custom iterables)
        _il.Emit(OpCodes.Ldloc, iterableLocal);
        _il.Emit(OpCodes.Ldsfld, _ctx!.Runtime!.SymbolIterator);
        _il.Emit(OpCodes.Call, _ctx.Runtime.GetIteratorFunction);
        _il.Emit(OpCodes.Stloc, iterFnLocal);

        // If iterFn != null, use iterator protocol
        _il.Emit(OpCodes.Ldloc, iterFnLocal);
        _il.Emit(OpCodes.Brtrue, hasIteratorLabel);

        // No Symbol.iterator - fall back to IEnumerable cast
        _il.Emit(OpCodes.Ldloc, iterableLocal);
        _il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
        _il.Emit(OpCodes.Callvirt, getEnumerator);
        _il.Emit(OpCodes.Stloc, enumTemp);
        _il.Emit(OpCodes.Br, gotEnumeratorLabel);

        // Has Symbol.iterator - use iterator protocol with $IteratorWrapper
        _il.MarkLabel(hasIteratorLabel);
        // Call iterator function: iterator = InvokeMethodValue(iterable, iterFn, new object[0])
        _il.Emit(OpCodes.Ldloc, iterableLocal);     // receiver (this)
        _il.Emit(OpCodes.Ldloc, iterFnLocal);       // function
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Newarr, typeof(object));   // empty args
        _il.Emit(OpCodes.Call, _ctx.Runtime.InvokeMethodValue);
        _il.Emit(OpCodes.Stloc, iteratorLocal);

        // Create $IteratorWrapper: new $IteratorWrapper(iterator, runtimeType)
        _il.Emit(OpCodes.Ldloc, iteratorLocal);
        _il.Emit(OpCodes.Ldtoken, _ctx.Runtime.RuntimeType);
        _il.Emit(OpCodes.Call, Types.TypeGetTypeFromHandle);
        _il.Emit(OpCodes.Newobj, _ctx.Runtime.IteratorWrapperCtor);
        _il.Emit(OpCodes.Stloc, enumTemp);

        // Store enumerator in field
        _il.MarkLabel(gotEnumeratorLabel);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, enumTemp);
        _il.Emit(OpCodes.Stfld, delegatedField);

        // This label is where we resume from state dispatch
        _il.MarkLabel(resumeLabel);

        // Load the delegated enumerator from field
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, delegatedField);

        // Check if there are more elements
        _il.Emit(OpCodes.Callvirt, moveNext);
        _il.Emit(OpCodes.Brfalse, loopEnd);

        // Get current value from delegated enumerator
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, delegatedField);
        _il.Emit(OpCodes.Callvirt, current);

        // Store in <>2__current
        var valueTemp = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, valueTemp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldloc, valueTemp);
        _il.Emit(OpCodes.Stfld, _builder.CurrentField);

        // Set state and return true
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, stateNumber);
        _il.Emit(OpCodes.Stfld, _builder.StateField);

        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(OpCodes.Ret);

        // End of delegation — reached when delegated.MoveNext() returned false.
        // ECMA-262 27.3: the completion value of `yield* expr` is the delegated iterator's
        // return value (the value passed to its `return` statement). For SharpTS-emitted
        // generators we store that value in the state machine's CurrentField (see EmitReturn
        // in Statements.cs). IEnumerator.Current is a valid property even after MoveNext
        // returned false for our generators — the runtime preserves the last-set value.
        _il.MarkLabel(loopEnd);

        // Capture the delegated iterator's Current as the yield* result. Guard against
        // non-SharpTS iterators (e.g. List<T>.Enumerator) that throw on Current after
        // MoveNext returned false: wrap in try/catch and fall back to null.
        var yieldStarResultLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Stloc, yieldStarResultLocal);
        _il.BeginExceptionBlock();
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, delegatedField);
        _il.Emit(OpCodes.Callvirt, current);
        _il.Emit(OpCodes.Stloc, yieldStarResultLocal);
        _il.BeginCatchBlock(typeof(Exception));
        _il.Emit(OpCodes.Pop);
        _il.EndExceptionBlock();

        // Clear the delegated enumerator field
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Stfld, delegatedField);

        // Restore any stack items we spilled before the setup block. Re-type each slot via
        // Castclass so the verifier sees the same stack shape it had pre-yield*.
        if (spillFields is not null)
        {
            for (int i = 0; i < spillFields.Length; i++)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, spillFields[i]);
                var slotType = slotTypes![i];
                if (slotType != typeof(object))
                {
                    _il.Emit(OpCodes.Castclass, slotType);
                }
            }
        }

        // yield* evaluates to the delegated iterator's return value (captured above).
        _il.Emit(OpCodes.Ldloc, yieldStarResultLocal);
        SetStackUnknown();
    }

    private static System.Reflection.FieldInfo? _currentStackDepthField;

    private static int ReadCurrentStackDepth(ILGenerator il)
    {
        _currentStackDepthField ??= il.GetType().GetField(
            "_currentStackDepth",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (_currentStackDepthField?.GetValue(il) is int depth) return depth;
        return -1;
    }

    // Reflects into the PersistedAssemblyBuilder's ILGeneratorImpl to read back the last few
    // IL bytes. Each Emit() call appends to this blob, so the trailing opcode identifies the
    // instruction that produced the current top-of-stack — enough to recover string/type
    // information we need to preserve across a spill. Pops one instruction off the tail when
    // called, so it must be invoked in reverse stack order (top-of-stack first).
    private static System.Reflection.FieldInfo? _ilInstrEncoderField;

    private static Type PeekTopStackType(ILGenerator il)
    {
        // Access ILGeneratorImpl._il (InstructionEncoder) then InstructionEncoder.CodeBuilder (BlobBuilder)
        _ilInstrEncoderField ??= il.GetType().GetField(
            "_il", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var encoder = _ilInstrEncoderField?.GetValue(il);
        if (encoder is null) return typeof(object);

        var codeBuilderProp = encoder.GetType().GetProperty("CodeBuilder",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var codeBuilder = codeBuilderProp?.GetValue(encoder);
        if (codeBuilder is null) return typeof(object);

        // BlobBuilder exposes Count and ToArray. Using ToArray each time is expensive but
        // acceptable — spill rarely fires.
        var toArray = codeBuilder.GetType().GetMethod("ToArray", Type.EmptyTypes);
        var bytes = toArray?.Invoke(codeBuilder, null) as byte[];
        if (bytes is null || bytes.Length == 0) return typeof(object);

        // Last instruction identifies top-of-stack type. Ldstr (0x72) pushes a string.
        // Ldc.i4.* push int32 (not relevant here — callers of spill work with reference types).
        int len = bytes.Length;
        // Ldstr is 5 bytes: 0x72 + 4-byte metadata token. We only need to check the starting byte.
        // Peek back 5 bytes and confirm the opcode.
        if (len >= 5 && bytes[len - 5] == 0x72) return typeof(string);
        return typeof(object);
    }
}
