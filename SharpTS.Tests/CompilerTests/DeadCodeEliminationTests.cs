// All tests migrated to SharedTests/DeadCodeEliminationTests.cs for unified execution
// across both interpreter and compiler modes.
//
// Migrated tests (34):
// - Level 1 Constant Conditions: IfTrue_OnlyThenBranchExecutes, IfFalse_OnlyElseBranchExecutes,
//   IfFalse_NoElse_NothingExecutes, LogicalAnd_FalseShortCircuits, LogicalOr_TrueShortCircuits,
//   Negation_NotFalse_ExecutesThen, Negation_NotTrue_ExecutesElse,
//   ComplexLogical_TrueAndTrue_ExecutesThen, ComplexLogical_FalseOrFalse_ExecutesElse
// - Level 2 Type-Based Conditions: TypeofString_AlwaysTrue_ExecutesThen,
//   TypeofString_AlwaysFalse_SkipsEntireIf, TypeofNumber_AlwaysTrue_ExecutesThen,
//   TypeofBoolean_AlwaysFalse_ExecutesElse, TypeofNotEqual_StringIsNotNumber_ExecutesThen,
//   TypeofStrictEqual_StringIsString_ExecutesThen, TypeofStrictNotEqual_NumberIsNotString_ExecutesThen,
//   UnionType_MixedTypeof_BothBranchesReachable
// - Level 3 Control Flow: AfterReturn_CodeNotExecuted, AfterThrow_CodeNotExecuted,
//   AfterBreak_CodeNotExecuted, AfterContinue_CodeNotExecuted,
//   ReturnFromFunctionCall_AfterCodeNotExecuted, IfBothBranchesReturn_AfterIfNotExecuted,
//   MultipleReturns_OnlyFirstExecuted
// - Exhaustive Switch: ExhaustiveSwitch_DefaultNotExecuted, NonExhaustiveSwitch_DefaultExecuted,
//   SwitchWithThreeOptions_AllCasesCovered
// - Edge Cases: NestedIfTrue_BothLevelsOptimized, NestedIfFalse_BothLevelsOptimized,
//   WhileFalse_BodyNeverExecutes, TernaryWithTrue_ReturnsFirstValue,
//   TernaryWithFalse_ReturnsSecondValue, GroupedCondition_TrueInParens_ExecutesThen,
//   DoubleNegation_NotNotTrue_ExecutesThen, FunctionWithMultipleExitPoints_CorrectPathExecutes
