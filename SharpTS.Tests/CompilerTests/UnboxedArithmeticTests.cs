// All tests migrated to SharedTests/UnboxedArithmeticTests.cs for unified execution
// across both interpreter and compiler modes.
//
// One test remains CompiledOnly in SharedTests:
// - UninitializedNumberLocal_DefaultsToZero (compiler gives 0, interpreter gives undefined)
//
// Migrated tests (68):
// - Numeric Chains: NumericChain_NoIntermediateBoxing, TypedLocal_StaysUnboxed,
//   MultipleArithmeticOps_ComplexExpression, LongChain_AllOperators
// - Typed Locals: ExplicitNumberType_UsesUnboxedLocal, InferredType_FallsBackToObject,
//   UninitializedNumberLocal_DefaultsToZero (CompiledOnly), TypedLocal_ReassignmentWorksCorrectly
// - Boxing Boundaries: FunctionCallWithNumericArg_BoxesCorrectly,
//   ReturnNumericValue_BoxesCorrectly, PropertySetWithNumeric_BoxesCorrectly,
//   ArrayElementWithNumeric_BoxesCorrectly, ConsoleLogWithNumericExpr_BoxesCorrectly
// - Mixed Types: MixedAnyAndNumber_WorksCorrectly, ObjectLocalWithNumber_FallsBackToBoxing,
//   NumberAndStringConcatenation_WorksCorrectly
// - Control Flow: TernaryWithNumericBranches_ProducesCorrectResult,
//   TernaryWithNumericBranches_FalseBranch, IfElseWithNumericAssignment_WorksCorrectly,
//   LoopWithNumericAccumulator_WorksCorrectly, ForOfWithNumericSum_WorksCorrectly
// - Compound Assignment: CompoundAdd/Subtract/Multiply/Divide, Increment/Decrement,
//   PrefixIncrement/Decrement
// - Comparisons: NumericComparison_ProducesBoolean, ComparisonInCondition_WorksCorrectly,
//   ChainedComparisons_WorkCorrectly, ComparisonWithArithmetic_WorksCorrectly
// - Functions: FunctionWithTypedLocals_ComputesCorrectly,
//   RecursiveFunctionWithNumbers_WorksCorrectly, NestedFunctionCalls_WorkCorrectly
// - Classes: ClassMethodWithNumericComputation_WorksCorrectly,
//   ClassWithNumericField_ArithmeticWorksCorrectly
// - Edge Cases: ZeroOperations, NegativeNumbers, FloatingPoint, DivisionBySmallNumber
// - Typed Returns: Fibonacci, MutualRecursion, DeepNesting, ChainedInExpression,
//   InTernaryCondition, BooleanFunction, StringFunction, UsedAsArgument,
//   InWhileCondition, InIfCondition, MultipleReturnPaths, InArrayMap,
//   ClassMethodReturningNumber, ClassMethodChaining, WithDefaultParameter, TailRecursion
// - Arrow Functions: ExpressionBody (Number/Boolean/String), BlockBody (Number/MultipleReturns),
//   NestedArrows, ArrayMap, ArrayFilter, ArrayReduce, ChainedCalls, WithClosure,
//   VoidExpression, InTernary, RecursiveArrow, AsMethodCallback
