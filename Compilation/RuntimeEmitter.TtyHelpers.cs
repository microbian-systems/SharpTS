using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits tty module helper methods for the runtime class.
/// </summary>
public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits tty module helper methods and registers them for TSFunction wrapping.
    /// </summary>
    internal void EmitTtyModuleMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Emit: public static object Tty_isatty(object? fd)
        var method = typeBuilder.DefineMethod(
            "Tty_isatty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );

        var il = method.GetILGenerator();

        // Convert fd (object?) to int
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Convert).GetMethod("ToInt32", [typeof(object)])!);

        var case1 = il.DefineLabel();
        var case2 = il.DefineLabel();
        var defaultCase = il.DefineLabel();
        var done = il.DefineLabel();

        // fd == 0?
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Bne_Un, case1);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Call, typeof(Console).GetProperty("IsInputRedirected")!.GetMethod!);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Box, typeof(bool));
        il.Emit(OpCodes.Br, done);

        // fd == 1?
        il.MarkLabel(case1);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Bne_Un, case2);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Call, typeof(Console).GetProperty("IsOutputRedirected")!.GetMethod!);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Box, typeof(bool));
        il.Emit(OpCodes.Br, done);

        // fd == 2?
        il.MarkLabel(case2);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Bne_Un, defaultCase);
        il.Emit(OpCodes.Call, typeof(Console).GetProperty("IsErrorRedirected")!.GetMethod!);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Box, typeof(bool));
        il.Emit(OpCodes.Br, done);

        // default: false
        il.MarkLabel(defaultCase);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, typeof(bool));

        il.MarkLabel(done);
        il.Emit(OpCodes.Ret);

        runtime.TtyIsatty = method;
        runtime.RegisterBuiltInModuleMethod("tty", "isatty", method);
    }
}
