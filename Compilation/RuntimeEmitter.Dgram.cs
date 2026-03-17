using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// dgram module support for compiled TypeScript: dgram.createSocket().
/// Uses reflection to create SharpTSDatagramSocket for standalone DLL support.
/// </summary>
public partial class RuntimeEmitter
{
    private void EmitDgramModuleMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitDgramCreateSocket(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits: public static object DgramCreateSocket(object? typeOrOptions, object? callback)
    /// Creates a SharpTSDatagramSocket via reflection for standalone DLL support.
    /// </summary>
    private void EmitDgramCreateSocket(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "DgramCreateSocket",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.DgramCreateSocket = method;

        var il = method.GetILGenerator();

        // Use reflection to create SharpTSDatagramSocket
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.Types.SharpTSDatagramSocket, SharpTS");
        il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetType", [_types.String])!);

        var typeLocal = il.DeclareLocal(typeof(Type));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Check if type was found
        var typeFoundLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Brtrue, typeFoundLabel);

        // Type not found (standalone mode without SharpTS) - return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(typeFoundLabel);

        // Extract type string from first argument
        // If arg is string, pass directly; if object, extract "type" field; default "udp4"
        var typeStringLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldstr, "udp4"); // default
        il.Emit(OpCodes.Stloc, typeStringLocal);

        var isStringLabel = il.DefineLabel();
        var extractedLabel = il.DefineLabel();

        // Check if arg0 is a string
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, isStringLabel);
        il.Emit(OpCodes.Stloc, typeStringLocal);
        il.Emit(OpCodes.Br, extractedLabel);

        il.MarkLabel(isStringLabel);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(extractedLabel);

        // Activator.CreateInstance(dgramType, new object[] { typeString })
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, typeStringLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Call, typeof(Activator).GetMethod("CreateInstance", [typeof(Type), typeof(object[])])!);
        il.Emit(OpCodes.Ret);
    }
}
