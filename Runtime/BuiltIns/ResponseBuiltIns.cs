using System.Text;
using System.Text.Json;
using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Provides static methods for the Response namespace (Response.json, Response.redirect, Response.error).
/// </summary>
public static class ResponseBuiltIns
{
    /// <summary>
    /// Gets a static method from the Response namespace.
    /// </summary>
    public static object? GetStaticMethod(string name)
    {
        return name switch
        {
            "json" => new BuiltInMethod("json", 1, 2, (_, _, args) =>
            {
                var data = args.Count > 0 ? args[0] : null;
                var init = args.Count > 1 ? args[1] as SharpTSObject : null;

                var json = SerializeToJson(data);
                var bodyBytes = Encoding.UTF8.GetBytes(json);

                double status = 200;
                string statusText = "";
                var headers = new SharpTSHeaders();
                headers.Set("content-type", "application/json");

                if (init != null)
                {
                    if (init.Fields.TryGetValue("status", out var statusObj) && statusObj is double s)
                        status = s;
                    if (init.Fields.TryGetValue("statusText", out var stObj) && stObj is string st)
                        statusText = st;
                    if (init.Fields.TryGetValue("headers", out var headersObj))
                    {
                        var extraHeaders = headersObj switch
                        {
                            SharpTSHeaders h => h,
                            SharpTSObject obj => new SharpTSHeaders(obj),
                            _ => null
                        };
                        if (extraHeaders != null)
                        {
                            foreach (var entry in extraHeaders.GetEntries())
                                headers.Set(entry.Key, entry.Value);
                        }
                    }
                }

                return new SharpTSResponse(status, statusText, headers, bodyBytes, "default");
            }),

            "redirect" => new BuiltInMethod("redirect", 1, 2, (_, _, args) =>
            {
                var url = args.Count > 0 ? args[0]?.ToString() ?? "" : "";
                var status = args.Count > 1 && args[1] is double s ? s : 302.0;

                var headers = new SharpTSHeaders();
                headers.Set("location", url);

                return new SharpTSResponse(status, "", headers, [], "default");
            }),

            "error" => new BuiltInMethod("error", 0, (_, _, _) =>
            {
                return new SharpTSResponse(0, "", new SharpTSHeaders(), [], "error");
            }),

            _ => null
        };
    }

    private static string SerializeToJson(object? value)
    {
        return JsonSerializer.Serialize(ConvertToSerializable(value));
    }

    private static object? ConvertToSerializable(object? value)
    {
        return value switch
        {
            null => null,
            bool b => b,
            double d => d,
            string s => s,
            SharpTSArray arr => arr.Elements.Select(ConvertToSerializable).ToArray(),
            SharpTSObject obj => obj.Fields.ToDictionary(kv => kv.Key, kv => ConvertToSerializable(kv.Value)),
            SharpTSUndefined => null,
            _ => value.ToString()
        };
    }
}
