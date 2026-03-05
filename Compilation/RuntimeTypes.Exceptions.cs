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

        return new Dictionary<string, object?>
        {
            ["message"] = ex.Message,
            ["name"] = ex.GetType().Name
        };
    }

    #endregion
}
