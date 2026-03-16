namespace SharpTS.Runtime.Types;

/// <summary>
/// Singleton marker for the JavaScript process object.
/// Extends EventEmitter to support process.on('exit'), process.on('uncaughtException'), etc.
/// </summary>
public class SharpTSProcess : SharpTSEventEmitter
{
    public static readonly SharpTSProcess Instance = new();
    private SharpTSProcess() { }

    public override string ToString() => "[object process]";
}
