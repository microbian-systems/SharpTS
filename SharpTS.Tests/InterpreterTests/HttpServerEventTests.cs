using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.InterpreterTests;

/// <summary>
/// HTTP Server EventEmitter tests have been migrated to SharedTests/HttpServerEventTests.cs
/// to run in both interpreter and compiled modes.
///
/// Migrated tests:
/// - Server_On_RegistersListener
/// - Server_Once_FiresOnlyOnce
/// - Server_Off_RemovesListener
/// - Server_RemoveAllListeners_ClearsAllForEvent
/// - Server_ListenerCount_ReturnsCorrectCount
/// - Server_EventNames_ReturnsRegisteredEvents
/// - Server_MultipleListeners_ReceiveSameEvent
/// - Server_Emit_CustomEvent_Works
/// - Server_Listeners_ReturnsListenerArray
/// - Server_SetMaxListeners_Works
/// - Server_PrependListener_AddsToFront
/// - Server_AddListener_IsAliasForOn
/// - Server_RemoveListener_IsAliasForOff
/// - Server_MethodChaining_Works
/// - Server_TypeCheck_EventEmitterMethods
/// - Server_Emit_ReturnsBoolean
/// - Server_RawListeners_ReturnsListenerArray
/// - Server_PrependOnceListener_AddsOnceListenerToFront
/// </summary>
public class HttpServerEventTests
{
}
