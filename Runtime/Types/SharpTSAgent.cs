using SharpTS.Runtime.BuiltIns;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Runtime representation of a Node.js http.Agent.
/// Manages HTTP connection pooling settings and provides the Node.js Agent API surface.
/// </summary>
/// <remarks>
/// In Node.js, Agent manages connection persistence and reuse for HTTP clients.
/// SharpTS delegates actual connection pooling to .NET's HttpClient/SocketsHttpHandler,
/// but exposes the Agent API for compatibility. The agent's configuration properties
/// (keepAlive, maxSockets, etc.) are available for inspection by user code.
/// </remarks>
public class SharpTSAgent : SharpTSEventEmitter
{
    /// <summary>
    /// The shared global agent singleton, equivalent to http.globalAgent.
    /// </summary>
    public static readonly SharpTSAgent GlobalAgent = new(
        keepAlive: true, keepAliveMsecs: 1000,
        maxSockets: double.PositiveInfinity, maxTotalSockets: double.PositiveInfinity,
        maxFreeSockets: 256, timeout: 0, scheduling: "lifo");

    #pragma warning disable CS0414 // Field is assigned but never read — tracks agent lifecycle state
    private bool _destroyed;
    #pragma warning restore CS0414

    public bool KeepAlive { get; set; }
    public double KeepAliveMsecs { get; set; }
    public double MaxSockets { get; set; }
    public double MaxTotalSockets { get; set; }
    public double MaxFreeSockets { get; set; }
    public double Timeout { get; set; }
    public string Scheduling { get; set; }

    public SharpTSAgent(
        bool keepAlive = false,
        double keepAliveMsecs = 1000,
        double maxSockets = double.PositiveInfinity,
        double maxTotalSockets = double.PositiveInfinity,
        double maxFreeSockets = 256,
        double timeout = 0,
        string scheduling = "lifo")
    {
        KeepAlive = keepAlive;
        KeepAliveMsecs = keepAliveMsecs;
        MaxSockets = maxSockets;
        MaxTotalSockets = maxTotalSockets;
        MaxFreeSockets = maxFreeSockets;
        Timeout = timeout;
        Scheduling = scheduling;
    }

    /// <summary>
    /// Creates an Agent from an options object (used by the constructor in TS code).
    /// </summary>
    public static SharpTSAgent FromOptions(SharpTSObject? options)
    {
        if (options == null)
            return new SharpTSAgent();

        bool keepAlive = options.Fields.TryGetValue("keepAlive", out var ka) && ka is true;
        double keepAliveMsecs = options.Fields.TryGetValue("keepAliveMsecs", out var kam) && kam is double kamv ? kamv : 1000;
        double maxSockets = options.Fields.TryGetValue("maxSockets", out var ms) && ms is double msv ? msv : double.PositiveInfinity;
        double maxTotalSockets = options.Fields.TryGetValue("maxTotalSockets", out var mts) && mts is double mtsv ? mtsv : double.PositiveInfinity;
        double maxFreeSockets = options.Fields.TryGetValue("maxFreeSockets", out var mfs) && mfs is double mfsv ? mfsv : 256;
        double timeout = options.Fields.TryGetValue("timeout", out var to) && to is double tov ? tov : 0;
        string scheduling = options.Fields.TryGetValue("scheduling", out var sc) && sc is string scv ? scv : "lifo";

        return new SharpTSAgent(keepAlive, keepAliveMsecs, maxSockets, maxTotalSockets, maxFreeSockets, timeout, scheduling);
    }

    /// <summary>
    /// Gets a member by name for interpreter dispatch.
    /// </summary>
    public new object? GetMember(string name)
    {
        return name switch
        {
            // Configuration properties
            "keepAlive" => KeepAlive,
            "keepAliveMsecs" => KeepAliveMsecs,
            "maxSockets" => MaxSockets,
            "maxTotalSockets" => MaxTotalSockets,
            "maxFreeSockets" => MaxFreeSockets,
            "timeout" => Timeout,
            "scheduling" => Scheduling,

            // State inspection (empty in our implementation since .NET handles pooling)
            "sockets" => new SharpTSObject(new Dictionary<string, object?>()),
            "freeSockets" => new SharpTSObject(new Dictionary<string, object?>()),
            "requests" => new SharpTSObject(new Dictionary<string, object?>()),

            // Methods
            "destroy" => BuiltInMethod.CreateV2("destroy", 0, Destroy),
            "getName" => BuiltInMethod.CreateV2("getName", 0, 1, GetName),
            "createConnection" => BuiltInMethod.CreateV2("createConnection", 1, 2, CreateConnection),

            // EventEmitter methods
            _ => base.GetMember(name)
        };
    }

    /// <summary>
    /// Resets the global agent to default state for singleton reuse across interpreter runs.
    /// </summary>
    internal static void ResetGlobalAgent()
    {
        GlobalAgent.KeepAlive = true;
        GlobalAgent.KeepAliveMsecs = 1000;
        GlobalAgent.MaxSockets = double.PositiveInfinity;
        GlobalAgent.MaxTotalSockets = double.PositiveInfinity;
        GlobalAgent.MaxFreeSockets = 256;
        GlobalAgent.Timeout = 0;
        GlobalAgent.Scheduling = "lifo";
        GlobalAgent._destroyed = false;
        GlobalAgent.ClearAllListenersInternal();
    }

    /// <summary>
    /// Sets a mutable property on the agent (e.g., agent.maxSockets = 10).
    /// </summary>
    public void SetMember(string name, object? value)
    {
        switch (name)
        {
            case "keepAlive": KeepAlive = value is true; break;
            case "keepAliveMsecs": KeepAliveMsecs = value is double d ? d : KeepAliveMsecs; break;
            case "maxSockets": MaxSockets = value is double ms ? ms : MaxSockets; break;
            case "maxTotalSockets": MaxTotalSockets = value is double mts ? mts : MaxTotalSockets; break;
            case "maxFreeSockets": MaxFreeSockets = value is double mfs ? mfs : MaxFreeSockets; break;
            case "timeout": Timeout = value is double t ? t : Timeout; break;
            case "scheduling": Scheduling = value is string s ? s : Scheduling; break;
        }
    }

    private RuntimeValue Destroy(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        _destroyed = true;
        return RuntimeValue.Null;
    }

    private RuntimeValue GetName(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        // Returns a unique name for a set of request options, used as the pool key.
        // Format: "host:port:localAddress:family"
        if (args.Length == 0 || args[0].ToObject() is not SharpTSObject options)
            return RuntimeValue.FromString("localhost:80::");

        var host = options.Fields.TryGetValue("host", out var h) && h is string hs ? hs : "localhost";
        var port = options.Fields.TryGetValue("port", out var p) && p is double pd ? ((int)pd).ToString() : "80";
        var localAddress = options.Fields.TryGetValue("localAddress", out var la) && la is string las ? las : "";
        var family = options.Fields.TryGetValue("family", out var f) && f is double fd ? ((int)fd).ToString() : "";

        return RuntimeValue.FromString($"{host}:{port}:{localAddress}:{family}");
    }

    private RuntimeValue CreateConnection(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        // In Node.js, this creates a raw socket connection.
        // We return null since our HTTP client uses .NET's built-in connection management.
        return RuntimeValue.Null;
    }

    public override string ToString() => "Agent { }";
}
