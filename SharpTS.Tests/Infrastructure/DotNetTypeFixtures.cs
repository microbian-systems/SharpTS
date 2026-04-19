namespace SharpTS.Tests.Infrastructure;

/// <summary>
/// Controlled .NET surface used by <c>@DotNetType</c> interpreter tests for
/// delegate parameters and event subscription. Separate from production code so
/// we can exercise exact signatures (Action, Func, Predicate, EventHandler) without
/// depending on volatile BCL API shapes.
/// </summary>
public class CallbackFixture
{
    public string LastReceived { get; private set; } = string.Empty;

    /// <summary>Passes a hard-coded string to the callback; verifies Action&lt;string&gt;.</summary>
    public void InvokeWithGreeting(Action<string> callback) => callback("hello");

    /// <summary>Invokes a Func and returns its result doubled; verifies Func&lt;int,int&gt; return flow.</summary>
    public int DoubleOf(Func<int, int> callback, int input) => callback(input) * 2;

    /// <summary>Filters a list via predicate; verifies Predicate&lt;int&gt; + boolean return.</summary>
    public int CountMatching(int[] values, Predicate<int> predicate)
    {
        int count = 0;
        foreach (var v in values)
        {
            if (predicate(v)) count++;
        }
        return count;
    }

    /// <summary>
    /// No-args void delegate; verifies the zero-parameter <c>Action</c> path,
    /// which has no boxing to worry about.
    /// </summary>
    public void InvokeNoArgs(Action callback) => callback();

    /// <summary>Fires the <see cref="StringReceived"/> event with the supplied payload.</summary>
    public void FireStringEvent(string payload)
    {
        LastReceived = payload;
        StringReceived?.Invoke(this, payload);
    }

    /// <summary>Fires the <see cref="Ping"/> event (no payload beyond EventArgs).</summary>
    public void FirePing() => Ping?.Invoke(this, EventArgs.Empty);

    /// <summary>Generic event with a string payload.</summary>
    public event EventHandler<string>? StringReceived;

    /// <summary>Classic EventHandler with no payload.</summary>
    public event EventHandler? Ping;
}

/// <summary>
/// Fixture for testing static event subscription via <see cref="DotNetTypeFixtures"/>-style
/// declarations. Lives here so the static event state can be reset between tests.
/// </summary>
public static class StaticCallbackFixture
{
    public static int LastValue { get; private set; }

    public static event EventHandler<int>? ValueChanged;

    public static void Fire(int value)
    {
        LastValue = value;
        ValueChanged?.Invoke(null, value);
    }

    /// <summary>Test-only reset so event subscribers from a prior test don't leak across tests.</summary>
    public static void Reset()
    {
        LastValue = 0;
        ValueChanged = null;
    }
}
