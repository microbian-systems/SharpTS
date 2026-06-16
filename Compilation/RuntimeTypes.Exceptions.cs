using SharpTS.Runtime.Types;

namespace SharpTS.Compilation;

public static partial class RuntimeTypes
{
    #region Exceptions

    public static Exception CreateException(object? value)
    {
        return new Exception(Stringify(value));
    }

    public static object WrapException(Exception ex)
    {
        // Check for PromiseRejectedException (emitted $PromiseRejectedException or runtime type)
        // which carries the original rejection reason value (e.g. a raw string from Promise.reject("msg"))
        var reasonProp = ex.GetType().GetProperty("Reason");
        if (reasonProp != null)
        {
            var reason = reasonProp.GetValue(ex);
            if (reason != null) return reason;
        }

        // Wrap a host-originated exception as a real Error so guest `catch` sees a
        // proper Error instance (`e instanceof Error`, `e.name === "Error"`) rather than
        // a bare { message, name=<.NET type> } object. Mirrors the emitted
        // $Runtime.WrapException standard fallback. (#700)
        return new SharpTSError(ex.Message);
    }

    #endregion
}
