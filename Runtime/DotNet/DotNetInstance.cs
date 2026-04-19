using System.Reflection;
using SharpTS.Execution;
using SharpTS.Runtime.Types;
using SharpTS.TypeSystem;

namespace SharpTS.Runtime.DotNet;

/// <summary>
/// Runtime wrapper for a .NET object returned from a <c>@DotNetType</c> call or construction.
/// Exposes the object's public members to TypeScript via reflection.
/// </summary>
public sealed class DotNetInstance : ITypeCategorized
{
    /// <summary>
    /// The underlying .NET object. Exposed for <see cref="DotNetMarshaller"/> so a
    /// <c>DotNetInstance</c> passed as an argument to another .NET call can be
    /// unwrapped into its native type.
    /// </summary>
    public object Underlying { get; }

    /// <summary>
    /// The concrete .NET type — may be a subtype of the declared TS mapping.
    /// </summary>
    public Type Type { get; }

    /// <inheritdoc />
    public TypeCategory RuntimeCategory => TypeCategory.External;

    // Lazily created on first addEventListener call.
    private Dictionary<(string, ISharpTSCallable), Delegate>? _eventSubscriptions;

    public DotNetInstance(object underlying, Type type)
    {
        Underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
        Type = type;
    }

    /// <summary>
    /// Evaluates <c>instance.member</c>. Returns a <see cref="DotNetMethod"/> for
    /// methods (bound to this instance), a marshalled value for properties/fields,
    /// or an event binder for the <c>addEventListener</c>/<c>removeEventListener</c> pseudo-methods.
    /// </summary>
    public object? GetMember(string name)
    {
        // Event subscription API — takes precedence over reflected names so .NET types
        // that happen to define their own addEventListener don't shadow the binder.
        if (name == "addEventListener")
        {
            return new DotNetEventBinder(Type, Underlying, GetOrCreateSubscriptions(), isAdd: true);
        }
        if (name == "removeEventListener")
        {
            return new DotNetEventBinder(Type, Underlying, GetOrCreateSubscriptions(), isAdd: false);
        }

        var methods = DotNetTypeRegistry.GetMethods(Type, name, isStatic: false);
        if (methods.Length > 0)
        {
            return new DotNetMethod(methods, Underlying, name, overloadHint: null);
        }

        var member = DotNetTypeRegistry.GetPropertyOrField(Type, name, isStatic: false);
        return member switch
        {
            PropertyInfo pi => DotNetMarshaller.WrapReturn(InvokeWithMapping(() => pi.GetValue(Underlying)), pi.PropertyType),
            FieldInfo fi => DotNetMarshaller.WrapReturn(fi.GetValue(Underlying), fi.FieldType),
            _ => SharpTSUndefined.Instance
        };
    }

    /// <summary>
    /// Assigns <c>instance.member = value</c> via reflection. The interpreter reference
    /// is threaded through so TS functions can be converted to delegates when the target
    /// property is a delegate-typed member.
    /// </summary>
    public void SetMember(string name, object? value, Interpreter? interpreter = null)
    {
        var member = DotNetTypeRegistry.GetPropertyOrField(Type, name, isStatic: false);
        switch (member)
        {
            case PropertyInfo pi when pi.CanWrite:
                InvokeWithMapping(() => pi.SetValue(Underlying, DotNetMarshaller.Convert(value, pi.PropertyType, interpreter)));
                return;
            case FieldInfo fi when !fi.IsInitOnly:
                fi.SetValue(Underlying, DotNetMarshaller.Convert(value, fi.FieldType, interpreter));
                return;
            default:
                throw new Runtime.Exceptions.ThrowException(DotNetExceptionMapper.Map(new MissingMemberException(
                    $"Property or field '{name}' not found (or is read-only) on '{Type.FullName}'.")));
        }
    }

    private Dictionary<(string, ISharpTSCallable), Delegate> GetOrCreateSubscriptions()
    {
        return _eventSubscriptions ??= new Dictionary<(string, ISharpTSCallable), Delegate>();
    }

    /// <summary>
    /// Invokes an action and rewraps .NET exceptions as JS-style errors.
    /// A <see cref="Runtime.Exceptions.ThrowException"/> that bubbles through a
    /// <see cref="System.Reflection.TargetInvocationException"/> (typical for TS throws
    /// inside a delegate callback) is unwrapped and re-thrown as-is so the original
    /// JS error object propagates unchanged.
    /// </summary>
    internal static void InvokeWithMapping(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            var rethrow = UnwrapOrMap(ex);
            throw rethrow;
        }
    }

    internal static T InvokeWithMapping<T>(Func<T> func)
    {
        try { return func(); }
        catch (Exception ex)
        {
            var rethrow = UnwrapOrMap(ex);
            throw rethrow;
        }
    }

    private static Exception UnwrapOrMap(Exception ex)
    {
        // A TS throw inside a delegate surfaces as TargetInvocationException wrapping
        // ThrowException — preserve the original JS error object instead of remapping.
        var cursor = ex;
        while (cursor is System.Reflection.TargetInvocationException { InnerException: { } inner })
        {
            cursor = inner;
        }
        if (cursor is Runtime.Exceptions.ThrowException throwEx) return throwEx;

        // Any other .NET exception → map to a JS-style error.
        return new Runtime.Exceptions.ThrowException(DotNetExceptionMapper.Map(ex));
    }

    public override string ToString() => Underlying.ToString() ?? Type.FullName ?? "[DotNetInstance]";
}
