using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the Node.js 'perf_hooks' module.
/// </summary>
/// <remarks>
/// Provides high-resolution timing APIs similar to the browser's Performance API.
/// Supports performance.now(), timeOrigin, mark(), measure(), getEntries*(), clear*(),
/// and PerformanceObserver.
/// </remarks>
public static class PerfHooksModuleInterpreter
{
    // High-resolution timer start point (process start time)
    private static readonly long StartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    private static readonly double TicksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;

    // Performance entries storage
    [ThreadStatic] private static List<SharpTSObject>? _entries;
    private static List<SharpTSObject> Entries => _entries ??= new List<SharpTSObject>();

    // Registered observers
    [ThreadStatic] private static List<ObserverRegistration>? _observers;
    private static List<ObserverRegistration> Observers => _observers ??= new List<ObserverRegistration>();

    private sealed class ObserverRegistration
    {
        public HashSet<string> EntryTypes { get; set; } = new();
        public object? Callback { get; set; }
        public bool Connected { get; set; }
    }

    /// <summary>
    /// Gets all exported values for the perf_hooks module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        // Reset state for test isolation - each module load starts fresh
        Reset();

        return new Dictionary<string, object?>
        {
            ["performance"] = CreatePerformanceObject(),
            ["PerformanceObserver"] = PerformanceObserverConstructor.Instance
        };
    }

    /// <summary>
    /// Creates the performance object with all its methods and properties.
    /// </summary>
    private static SharpTSObject CreatePerformanceObject()
    {
        var fields = new Dictionary<string, object?>
        {
            ["now"] = new BuiltInMethod("now", 0, 0, PerformanceNow),
            ["timeOrigin"] = GetTimeOrigin(),
            ["mark"] = new BuiltInMethod("mark", 1, 2, PerformanceMark),
            ["measure"] = new BuiltInMethod("measure", 1, 3, PerformanceMeasure),
            ["getEntries"] = new BuiltInMethod("getEntries", 0, 0, PerformanceGetEntries),
            ["getEntriesByName"] = new BuiltInMethod("getEntriesByName", 1, 2, PerformanceGetEntriesByName),
            ["getEntriesByType"] = new BuiltInMethod("getEntriesByType", 1, 1, PerformanceGetEntriesByType),
            ["clearMarks"] = new BuiltInMethod("clearMarks", 0, 1, PerformanceClearMarks),
            ["clearMeasures"] = new BuiltInMethod("clearMeasures", 0, 1, PerformanceClearMeasures)
        };

        return new SharpTSObject(fields);
    }

    /// <summary>
    /// Returns a high resolution time stamp in milliseconds.
    /// </summary>
    private static double GetNowMs()
    {
        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - StartTicks;
        return elapsed / TicksPerMs;
    }

    private static object? PerformanceNow(Interp interpreter, object? receiver, List<object?> args)
    {
        return GetNowMs();
    }

    /// <summary>
    /// Gets the Unix timestamp of when the process started (in milliseconds since epoch).
    /// </summary>
    private static double GetTimeOrigin()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - StartTicks) / TicksPerMs;
        var startTime = now.AddMilliseconds(-elapsedMs);
        return startTime.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// performance.mark(name, options?) - creates a PerformanceMark entry.
    /// </summary>
    private static object? PerformanceMark(Interp interpreter, object? receiver, List<object?> args)
    {
        var name = args[0] as string ?? "";
        double startTime = GetNowMs();

        // Check for options.startTime
        if (args.Count > 1 && args[1] is SharpTSObject options)
        {
            var st = options.GetProperty("startTime");
            if (st is double d) startTime = d;
        }

        var entry = CreateEntry(name, "mark", startTime, 0);
        Entries.Add(entry);
        NotifyObservers(entry, "mark", interpreter);
        return entry;
    }

    /// <summary>
    /// performance.measure(name, startMark?, endMark?) - creates a PerformanceMeasure entry.
    /// </summary>
    private static object? PerformanceMeasure(Interp interpreter, object? receiver, List<object?> args)
    {
        var name = args[0] as string ?? "";
        double startTime = 0;
        double endTime = GetNowMs();

        if (args.Count > 1 && args[1] is string startMarkName)
        {
            var startMark = FindMark(startMarkName);
            if (startMark != null)
            {
                var st = startMark.GetProperty("startTime");
                if (st is double d) startTime = d;
            }
        }

        if (args.Count > 2 && args[2] is string endMarkName)
        {
            var endMark = FindMark(endMarkName);
            if (endMark != null)
            {
                var et = endMark.GetProperty("startTime");
                if (et is double d) endTime = d;
            }
        }

        double duration = endTime - startTime;
        var entry = CreateEntry(name, "measure", startTime, duration);
        Entries.Add(entry);
        NotifyObservers(entry, "measure", interpreter);
        return entry;
    }

    /// <summary>
    /// performance.getEntries() - returns all entries.
    /// </summary>
    private static object? PerformanceGetEntries(Interp interpreter, object? receiver, List<object?> args)
    {
        return new SharpTSArray(new List<object?>(Entries));
    }

    /// <summary>
    /// performance.getEntriesByName(name, type?) - filters by name and optionally type.
    /// </summary>
    private static object? PerformanceGetEntriesByName(Interp interpreter, object? receiver, List<object?> args)
    {
        var name = args[0] as string ?? "";
        string? type = args.Count > 1 ? args[1] as string : null;

        var result = new List<object?>();
        foreach (var entry in Entries)
        {
            var entryName = entry.GetProperty("name") as string;
            if (entryName != name) continue;
            if (type != null)
            {
                var entryType = entry.GetProperty("entryType") as string;
                if (entryType != type) continue;
            }
            result.Add(entry);
        }
        return new SharpTSArray(result);
    }

    /// <summary>
    /// performance.getEntriesByType(type) - filters by entryType.
    /// </summary>
    private static object? PerformanceGetEntriesByType(Interp interpreter, object? receiver, List<object?> args)
    {
        var type = args[0] as string ?? "";
        var result = new List<object?>();
        foreach (var entry in Entries)
        {
            var entryType = entry.GetProperty("entryType") as string;
            if (entryType == type)
                result.Add(entry);
        }
        return new SharpTSArray(result);
    }

    /// <summary>
    /// performance.clearMarks(name?) - removes mark entries.
    /// </summary>
    private static object? PerformanceClearMarks(Interp interpreter, object? receiver, List<object?> args)
    {
        string? name = args.Count > 0 ? args[0] as string : null;
        Entries.RemoveAll(e =>
        {
            var entryType = e.GetProperty("entryType") as string;
            if (entryType != "mark") return false;
            if (name != null)
            {
                var entryName = e.GetProperty("name") as string;
                return entryName == name;
            }
            return true;
        });
        return null;
    }

    /// <summary>
    /// performance.clearMeasures(name?) - removes measure entries.
    /// </summary>
    private static object? PerformanceClearMeasures(Interp interpreter, object? receiver, List<object?> args)
    {
        string? name = args.Count > 0 ? args[0] as string : null;
        Entries.RemoveAll(e =>
        {
            var entryType = e.GetProperty("entryType") as string;
            if (entryType != "measure") return false;
            if (name != null)
            {
                var entryName = e.GetProperty("name") as string;
                return entryName == name;
            }
            return true;
        });
        return null;
    }

    /// <summary>
    /// Clears all entries and observers (for test isolation).
    /// </summary>
    internal static void Reset()
    {
        _entries?.Clear();
        _observers?.Clear();
    }

    private static SharpTSObject CreateEntry(string name, string entryType, double startTime, double duration)
    {
        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["entryType"] = entryType,
            ["startTime"] = startTime,
            ["duration"] = duration
        });
    }

    private static SharpTSObject? FindMark(string name)
    {
        // Search in reverse to find the most recent mark with the given name
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            var entryType = entry.GetProperty("entryType") as string;
            var entryName = entry.GetProperty("name") as string;
            if (entryType == "mark" && entryName == name)
                return entry;
        }
        return null;
    }

    private static void NotifyObservers(SharpTSObject entry, string entryType, Interp? interpreter)
    {
        if (_observers == null) return;
        foreach (var reg in _observers)
        {
            if (!reg.Connected) continue;
            if (!reg.EntryTypes.Contains(entryType)) continue;

            // Create a PerformanceObserverEntryList with getEntries() method
            var entryList = new List<object?> { entry };
            var listObj = new SharpTSObject(new Dictionary<string, object?>
            {
                ["getEntries"] = new BuiltInMethod("getEntries", 0, 0,
                    (_, _, _) => new SharpTSArray(new List<object?>(entryList)))
            });

            // Call the observer callback
            if (reg.Callback is ISharpTSCallable callable)
            {
                callable.Call(interpreter!, new List<object?> { listObj });
            }
        }
    }

    /// <summary>
    /// PerformanceObserver constructor for interpreter mode.
    /// </summary>
    public sealed class PerformanceObserverConstructor : ISharpTSCallable
    {
        public static readonly PerformanceObserverConstructor Instance = new();
        private PerformanceObserverConstructor() { }

        public int Arity() => 1;

        public object? Call(Interp interpreter, List<object?> args)
        {
            var callback = args.Count > 0 ? args[0] : null;

            var registration = new ObserverRegistration { Callback = callback };

            var observer = new SharpTSObject(new Dictionary<string, object?>
            {
                ["observe"] = new BuiltInMethod("observe", 1, 1, (_, _, observeArgs) =>
                {
                    if (observeArgs.Count > 0 && observeArgs[0] is SharpTSObject options)
                    {
                        var entryTypes = options.GetProperty("entryTypes");
                        if (entryTypes is SharpTSArray arr)
                        {
                            registration.EntryTypes.Clear();
                            foreach (var item in arr.Elements)
                            {
                                if (item is string s)
                                    registration.EntryTypes.Add(s);
                            }
                        }
                        else if (entryTypes is List<object?> list)
                        {
                            registration.EntryTypes.Clear();
                            foreach (var item in list)
                            {
                                if (item is string s)
                                    registration.EntryTypes.Add(s);
                            }
                        }
                    }
                    registration.Connected = true;
                    Observers.Add(registration);
                    return null;
                }),
                ["disconnect"] = new BuiltInMethod("disconnect", 0, 0, (_, _, _) =>
                {
                    registration.Connected = false;
                    return null;
                })
            });

            return observer;
        }
    }
}
