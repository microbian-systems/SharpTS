using System.Reflection;
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

    public DotNetInstance(object underlying, Type type)
    {
        Underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
        Type = type;
    }

    /// <summary>
    /// Evaluates <c>instance.member</c>. Returns a <see cref="DotNetMethod"/> for
    /// methods (bound to this instance), a marshalled value for properties/fields.
    /// </summary>
    public object? GetMember(string name)
    {
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
    /// Assigns <c>instance.member = value</c> via reflection.
    /// </summary>
    public void SetMember(string name, object? value)
    {
        var member = DotNetTypeRegistry.GetPropertyOrField(Type, name, isStatic: false);
        switch (member)
        {
            case PropertyInfo pi when pi.CanWrite:
                InvokeWithMapping(() => pi.SetValue(Underlying, DotNetMarshaller.Convert(value, pi.PropertyType)));
                return;
            case FieldInfo fi when !fi.IsInitOnly:
                fi.SetValue(Underlying, DotNetMarshaller.Convert(value, fi.FieldType));
                return;
            default:
                throw new Runtime.Exceptions.ThrowException(DotNetExceptionMapper.Map(new MissingMemberException(
                    $"Property or field '{name}' not found (or is read-only) on '{Type.FullName}'.")));
        }
    }

    /// <summary>
    /// Invokes an action and rewraps .NET exceptions as JS-style errors.
    /// </summary>
    internal static void InvokeWithMapping(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not Runtime.Exceptions.ThrowException)
        {
            throw new Runtime.Exceptions.ThrowException(DotNetExceptionMapper.Map(ex));
        }
    }

    internal static T InvokeWithMapping<T>(Func<T> func)
    {
        try { return func(); }
        catch (Exception ex) when (ex is not Runtime.Exceptions.ThrowException)
        {
            throw new Runtime.Exceptions.ThrowException(DotNetExceptionMapper.Map(ex));
        }
    }

    public override string ToString() => Underlying.ToString() ?? Type.FullName ?? "[DotNetInstance]";
}
