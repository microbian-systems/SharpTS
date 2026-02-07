using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits path helper methods for standalone DLLs.
/// These replace the PathHelpers class to avoid SharpTS.dll dependency.
/// </summary>
public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits all path helper methods.
    /// </summary>
    private void EmitPathHelpers(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // POSIX path methods (order matters - emit dependencies first)
        EmitPosixJoin(typeBuilder, runtime);
        EmitPosixNormalize(typeBuilder, runtime); // Must be before Resolve (Resolve calls Normalize)
        EmitPosixResolve(typeBuilder, runtime);
        EmitPosixBasename(typeBuilder, runtime);
        EmitPosixDirname(typeBuilder, runtime);
        EmitPosixIsAbsolute(typeBuilder, runtime);
        EmitPosixRelative(typeBuilder, runtime); // Uses ComputeRelative (emitted before this method)
        EmitPosixParse(typeBuilder, runtime);
        EmitPosixFormat(typeBuilder, runtime);

        // Win32 path methods (order matters - emit dependencies first)
        EmitWin32Join(typeBuilder, runtime);
        EmitWin32Normalize(typeBuilder, runtime); // Must be before Resolve (Resolve calls Normalize)
        EmitWin32IsAbsolute(typeBuilder, runtime); // Must be before Resolve (Resolve calls IsAbsolute)
        EmitWin32Resolve(typeBuilder, runtime);
        EmitWin32Basename(typeBuilder, runtime);
        EmitWin32Dirname(typeBuilder, runtime);
        EmitWin32Relative(typeBuilder, runtime); // Uses ComputeRelative (emitted before this method)
        EmitWin32Parse(typeBuilder, runtime);
        EmitWin32Format(typeBuilder, runtime);
    }

    #region POSIX Path Methods

    /// <summary>
    /// Emits: public static string PosixJoin(object?[] args)
    /// Joins path segments using POSIX separator (/).
    /// </summary>
    private void EmitPosixJoin(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PosixJoin",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.ObjectArray]);
        runtime.PosixJoin = method;

        var il = method.GetILGenerator();

        // if (args.Length == 0) return "."
        var notEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Brtrue, notEmptyLabel);
        il.Emit(OpCodes.Ldstr, ".");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notEmptyLabel);

        // var result = new List<string>()
        var listLocal = il.DeclareLocal(_types.ListOfString);
        il.Emit(OpCodes.Newobj, _types.ListOfString.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, listLocal);

        // foreach (var arg in args)
        var indexLocal = il.DeclareLocal(_types.Int32);
        var lengthLocal = il.DeclareLocal(_types.Int32);
        var partLocal = il.DeclareLocal(_types.String);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, lengthLocal);

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var continueLabel = il.DefineLabel();

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, lengthLocal);
        il.Emit(OpCodes.Bge, loopEndLabel);

        // var arg = args[i]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);

        // var part = arg?.ToString() ?? ""
        var argNullLabel = il.DefineLabel();
        var partDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, argNullLabel);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Br, partDoneLabel);
        il.MarkLabel(argNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(partDoneLabel);
        il.Emit(OpCodes.Stloc, partLocal);

        // if (string.IsNullOrEmpty(part)) continue
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brtrue, continueLabel);

        // part = part.Replace('\\', '/')
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Stloc, partLocal);

        // result.Add(part)
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("Add", [_types.String])!);

        il.MarkLabel(continueLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);

        // return result.Count == 0 ? "." : string.Join("/", result)
        var joinLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("get_Count")!);
        il.Emit(OpCodes.Brtrue, joinLabel);
        il.Emit(OpCodes.Ldstr, ".");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(joinLabel);
        il.Emit(OpCodes.Ldstr, "/");
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Join", [_types.String, typeof(IEnumerable<string>)])!);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static string PosixResolve(object?[] args)
    /// Resolves path segments to an absolute POSIX path.
    /// </summary>
    private void EmitPosixResolve(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PosixResolve",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.ObjectArray]);
        runtime.PosixResolve = method;

        var il = method.GetILGenerator();

        // var result = Directory.GetCurrentDirectory().Replace('\\', '/')
        var resultLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Call, _types.Directory.GetMethod("GetCurrentDirectory")!);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Stloc, resultLocal);

        // foreach loop setup
        var indexLocal = il.DeclareLocal(_types.Int32);
        var lengthLocal = il.DeclareLocal(_types.Int32);
        var partLocal = il.DeclareLocal(_types.String);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, lengthLocal);

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var continueLabel = il.DefineLabel();
        var appendLabel = il.DefineLabel();

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, lengthLocal);
        il.Emit(OpCodes.Bge, loopEndLabel);

        // var part = (args[i]?.ToString() ?? "").Replace('\\', '/')
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        var argNullLabel = il.DefineLabel();
        var partDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, argNullLabel);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Br, partDoneLabel);
        il.MarkLabel(argNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(partDoneLabel);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Stloc, partLocal);

        // if (string.IsNullOrEmpty(part)) continue
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brtrue, continueLabel);

        // if (part.StartsWith('/')) result = part; else result = result + "/" + part
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("StartsWith", [_types.Char])!);
        il.Emit(OpCodes.Brfalse, appendLabel);

        // result = part
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, continueLabel);

        // result = result + "/" + part
        il.MarkLabel(appendLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "/");
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Stloc, resultLocal);

        il.MarkLabel(continueLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);

        // return PosixNormalize(result)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Call, runtime.PosixNormalize);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static string PosixBasename(string path, string? ext)
    /// Gets the basename of a POSIX path.
    /// </summary>
    private void EmitPosixBasename(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PosixBasename",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String, _types.String]);
        runtime.PosixBasename = method;

        var il = method.GetILGenerator();

        // path = path.Replace('\\', '/')
        var pathLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Stloc, pathLocal);

        // var lastSep = path.LastIndexOf('/')
        var lastSepLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("LastIndexOf", [_types.Char])!);
        il.Emit(OpCodes.Stloc, lastSepLocal);

        // var filename = lastSep >= 0 ? path[(lastSep + 1)..] : path
        var filenameLocal = il.DeclareLocal(_types.String);
        var usePathLabel = il.DefineLabel();
        var filenameDoneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, usePathLabel);

        // path[(lastSep + 1)..]
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32])!);
        il.Emit(OpCodes.Br, filenameDoneLabel);

        il.MarkLabel(usePathLabel);
        il.Emit(OpCodes.Ldloc, pathLocal);

        il.MarkLabel(filenameDoneLabel);
        il.Emit(OpCodes.Stloc, filenameLocal);

        // if (!string.IsNullOrEmpty(ext) && filename.EndsWith(ext, StringComparison.Ordinal))
        var returnLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);  // ext
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brtrue, returnLabel);

        il.Emit(OpCodes.Ldloc, filenameLocal);
        il.Emit(OpCodes.Ldarg_1);  // ext
        il.Emit(OpCodes.Ldc_I4, (int)StringComparison.Ordinal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("EndsWith", [_types.String, typeof(StringComparison)])!);
        il.Emit(OpCodes.Brfalse, returnLabel);

        // filename = filename[..^ext.Length]
        il.Emit(OpCodes.Ldloc, filenameLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, filenameLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Length")!);
        il.Emit(OpCodes.Ldarg_1);  // ext
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Length")!);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, filenameLocal);

        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ldloc, filenameLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static string PosixDirname(string path)
    /// Gets the directory name of a POSIX path.
    /// </summary>
    private void EmitPosixDirname(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PosixDirname",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String]);
        runtime.PosixDirname = method;

        var il = method.GetILGenerator();

        // path = path.Replace('\\', '/')
        var pathLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Stloc, pathLocal);

        // var lastSep = path.LastIndexOf('/')
        var lastSepLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("LastIndexOf", [_types.Char])!);
        il.Emit(OpCodes.Stloc, lastSepLocal);

        // if (lastSep < 0) return "."
        var checkRootLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Bge, checkRootLabel);
        il.Emit(OpCodes.Ldstr, ".");
        il.Emit(OpCodes.Ret);

        // if (lastSep == 0) return "/"
        il.MarkLabel(checkRootLabel);
        var substringLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Brtrue, substringLabel);
        il.Emit(OpCodes.Ldstr, "/");
        il.Emit(OpCodes.Ret);

        // return path[..lastSep]
        il.MarkLabel(substringLabel);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static string PosixNormalize(string path)
    /// Normalizes a POSIX path.
    /// </summary>
    private void EmitPosixNormalize(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PosixNormalize",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String]);
        runtime.PosixNormalize = method;

        var il = method.GetILGenerator();

        // if (string.IsNullOrEmpty(path)) return "."
        var notEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brfalse, notEmptyLabel);
        il.Emit(OpCodes.Ldstr, ".");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notEmptyLabel);

        // path = path.Replace('\\', '/')
        var pathLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Stloc, pathLocal);

        // var isAbsolute = path.StartsWith('/')
        var isAbsoluteLocal = il.DeclareLocal(_types.Boolean);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("StartsWith", [_types.Char])!);
        il.Emit(OpCodes.Stloc, isAbsoluteLocal);

        // var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
        var partsLocal = il.DeclareLocal(_types.StringArray);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Ldc_I4, (int)StringSplitOptions.RemoveEmptyEntries);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Split", [_types.Char, typeof(StringSplitOptions)])!);
        il.Emit(OpCodes.Stloc, partsLocal);

        // var stack = new List<string>()
        var stackLocal = il.DeclareLocal(_types.ListOfString);
        il.Emit(OpCodes.Newobj, _types.ListOfString.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, stackLocal);

        // Process each part
        var indexLocal = il.DeclareLocal(_types.Int32);
        var lengthLocal = il.DeclareLocal(_types.Int32);
        var partLocal = il.DeclareLocal(_types.String);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, lengthLocal);

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var continueLabel = il.DefineLabel();
        var dotDotLabel = il.DefineLabel();
        var addPartLabel = il.DefineLabel();

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, lengthLocal);
        il.Emit(OpCodes.Bge, loopEndLabel);

        // var part = parts[i]
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, partLocal);

        // if (part == ".") continue
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Ldstr, ".");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, continueLabel);

        // if (part == "..")
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Ldstr, "..");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, dotDotLabel);

        // else: stack.Add(part)
        il.Emit(OpCodes.Br, addPartLabel);

        // Handle ".."
        il.MarkLabel(dotDotLabel);
        // if (stack.Count > 0 && stack[^1] != "..")
        var popLabel = il.DefineLabel();
        var addDotDotLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("get_Count")!);
        il.Emit(OpCodes.Brfalse, addDotDotLabel);

        // Check if last element is not ".."
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("get_Count")!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Callvirt, _types.ListOfStringGetItem);
        il.Emit(OpCodes.Ldstr, "..");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, addDotDotLabel);

        // stack.RemoveAt(stack.Count - 1)
        il.MarkLabel(popLabel);
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("get_Count")!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("RemoveAt", [_types.Int32])!);
        il.Emit(OpCodes.Br, continueLabel);

        // else if (!isAbsolute) stack.Add("..")
        il.MarkLabel(addDotDotLabel);
        il.Emit(OpCodes.Ldloc, isAbsoluteLocal);
        il.Emit(OpCodes.Brtrue, continueLabel);
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Ldstr, "..");
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("Add", [_types.String])!);
        il.Emit(OpCodes.Br, continueLabel);

        // stack.Add(part)
        il.MarkLabel(addPartLabel);
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("Add", [_types.String])!);

        il.MarkLabel(continueLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);

        // var result = string.Join("/", stack)
        var resultLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldstr, "/");
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Join", [_types.String, typeof(IEnumerable<string>)])!);
        il.Emit(OpCodes.Stloc, resultLocal);

        // if (isAbsolute) result = "/" + result
        var checkEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, isAbsoluteLocal);
        il.Emit(OpCodes.Brfalse, checkEmptyLabel);
        il.Emit(OpCodes.Ldstr, "/");
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Call, _types.StringConcat2);
        il.Emit(OpCodes.Stloc, resultLocal);

        // return string.IsNullOrEmpty(result) ? (isAbsolute ? "/" : ".") : result
        il.MarkLabel(checkEmptyLabel);
        var returnResultLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brfalse, returnResultLabel);

        // Empty result
        var returnDotLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, isAbsoluteLocal);
        il.Emit(OpCodes.Brfalse, returnDotLabel);
        il.Emit(OpCodes.Ldstr, "/");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnDotLabel);
        il.Emit(OpCodes.Ldstr, ".");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnResultLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static bool PosixIsAbsolute(string path)
    /// Checks if a POSIX path is absolute.
    /// </summary>
    private void EmitPosixIsAbsolute(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PosixIsAbsolute",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.String]);
        runtime.PosixIsAbsolute = method;

        var il = method.GetILGenerator();

        // return path.StartsWith('/')
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("StartsWith", [_types.Char])!);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static string PosixRelative(string from, string to)
    /// Gets the relative path between two POSIX paths.
    /// </summary>
    private void EmitPosixRelative(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PosixRelative",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String, _types.String]);
        runtime.PosixRelative = method;

        var il = method.GetILGenerator();

        // from = PosixNormalize(from)
        var fromLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PosixNormalize);
        il.Emit(OpCodes.Stloc, fromLocal);

        // to = PosixNormalize(to)
        var toLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.PosixNormalize);
        il.Emit(OpCodes.Stloc, toLocal);

        // Call the shared ComputeRelative helper
        il.Emit(OpCodes.Ldloc, fromLocal);
        il.Emit(OpCodes.Ldloc, toLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Call, runtime.ComputeRelative);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static Dictionary&lt;string, object?&gt; PosixParse(string path)
    /// Parses a POSIX path into its components.
    /// </summary>
    private void EmitPosixParse(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PosixParse",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.DictionaryStringObject,
            [_types.String]);
        runtime.PosixParse = method;

        var il = method.GetILGenerator();

        // path = path.Replace('\\', '/')
        var pathLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Stloc, pathLocal);

        // var root = path.StartsWith('/') ? "/" : ""
        var rootLocal = il.DeclareLocal(_types.String);
        var notRootLabel = il.DefineLabel();
        var rootDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("StartsWith", [_types.Char])!);
        il.Emit(OpCodes.Brfalse, notRootLabel);
        il.Emit(OpCodes.Ldstr, "/");
        il.Emit(OpCodes.Br, rootDoneLabel);
        il.MarkLabel(notRootLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(rootDoneLabel);
        il.Emit(OpCodes.Stloc, rootLocal);

        // var lastSep = path.LastIndexOf('/')
        var lastSepLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("LastIndexOf", [_types.Char])!);
        il.Emit(OpCodes.Stloc, lastSepLocal);

        // Compute dir, base, name, ext (simplified version)
        var dirLocal = il.DeclareLocal(_types.String);
        var baseLocal = il.DeclareLocal(_types.String);
        var nameLocal = il.DeclareLocal(_types.String);
        var extLocal = il.DeclareLocal(_types.String);

        // dir = lastSep > 0 ? path[..lastSep] : (lastSep == 0 ? "/" : "")
        var dirEmptyLabel = il.DefineLabel();
        var dirRootLabel = il.DefineLabel();
        var dirDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, dirRootLabel);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Br, dirDoneLabel);

        il.MarkLabel(dirRootLabel);
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Brtrue, dirEmptyLabel);
        il.Emit(OpCodes.Ldstr, "/");
        il.Emit(OpCodes.Br, dirDoneLabel);

        il.MarkLabel(dirEmptyLabel);
        il.Emit(OpCodes.Ldstr, "");

        il.MarkLabel(dirDoneLabel);
        il.Emit(OpCodes.Stloc, dirLocal);

        // baseName = lastSep >= 0 ? path[(lastSep + 1)..] : path
        var baseFromPathLabel = il.DefineLabel();
        var baseDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, baseFromPathLabel);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32])!);
        il.Emit(OpCodes.Br, baseDoneLabel);
        il.MarkLabel(baseFromPathLabel);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.MarkLabel(baseDoneLabel);
        il.Emit(OpCodes.Stloc, baseLocal);

        // extIdx = baseName.LastIndexOf('.')
        var extIdxLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'.');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("LastIndexOf", [_types.Char])!);
        il.Emit(OpCodes.Stloc, extIdxLocal);

        // ext = extIdx > 0 ? baseName[extIdx..] : ""
        var extEmptyLabel = il.DefineLabel();
        var extDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, extIdxLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, extEmptyLabel);
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Ldloc, extIdxLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32])!);
        il.Emit(OpCodes.Br, extDoneLabel);
        il.MarkLabel(extEmptyLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(extDoneLabel);
        il.Emit(OpCodes.Stloc, extLocal);

        // name = extIdx > 0 ? baseName[..extIdx] : baseName
        var nameFromBaseLabel = il.DefineLabel();
        var nameDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, extIdxLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, nameFromBaseLabel);
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, extIdxLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Br, nameDoneLabel);
        il.MarkLabel(nameFromBaseLabel);
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.MarkLabel(nameDoneLabel);
        il.Emit(OpCodes.Stloc, nameLocal);

        // Create and return dictionary
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObject.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, dictLocal);

        // dict["root"] = root
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "root");
        il.Emit(OpCodes.Ldloc, rootLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectSetItem);

        // dict["dir"] = dir
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "dir");
        il.Emit(OpCodes.Ldloc, dirLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectSetItem);

        // dict["base"] = baseName
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "base");
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectSetItem);

        // dict["name"] = name
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectSetItem);

        // dict["ext"] = ext
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "ext");
        il.Emit(OpCodes.Ldloc, extLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectSetItem);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static string PosixFormat(object? pathObj)
    /// Formats a parsed path object into a POSIX path string.
    /// </summary>
    private void EmitPosixFormat(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PosixFormat",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]);
        runtime.PosixFormat = method;

        var il = method.GetILGenerator();

        // if (pathObj == null) return ""
        var notNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, notNullLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notNullLabel);

        // Use GetOptionString to extract properties
        var dirLocal = il.DeclareLocal(_types.String);
        var rootLocal = il.DeclareLocal(_types.String);
        var baseLocal = il.DeclareLocal(_types.String);
        var nameLocal = il.DeclareLocal(_types.String);
        var extLocal = il.DeclareLocal(_types.String);

        // dir = GetOptionString(pathObj, "dir", "")
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "dir");
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, runtime.GetOptionString);
        il.Emit(OpCodes.Stloc, dirLocal);

        // root = GetOptionString(pathObj, "root", "")
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "root");
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, runtime.GetOptionString);
        il.Emit(OpCodes.Stloc, rootLocal);

        // baseName = GetOptionString(pathObj, "base", "")
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "base");
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, runtime.GetOptionString);
        il.Emit(OpCodes.Stloc, baseLocal);

        // name = GetOptionString(pathObj, "name", "")
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, runtime.GetOptionString);
        il.Emit(OpCodes.Stloc, nameLocal);

        // ext = GetOptionString(pathObj, "ext", "")
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "ext");
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, runtime.GetOptionString);
        il.Emit(OpCodes.Stloc, extLocal);

        // if (string.IsNullOrEmpty(baseName)) baseName = name + ext
        var baseNotEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brfalse, baseNotEmptyLabel);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldloc, extLocal);
        il.Emit(OpCodes.Call, _types.StringConcat2);
        il.Emit(OpCodes.Stloc, baseLocal);

        il.MarkLabel(baseNotEmptyLabel);

        // var directory = !string.IsNullOrEmpty(dir) ? dir : root
        var directoryLocal = il.DeclareLocal(_types.String);
        var useRootLabel = il.DefineLabel();
        var directoryDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dirLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brtrue, useRootLabel);
        il.Emit(OpCodes.Ldloc, dirLocal);
        il.Emit(OpCodes.Br, directoryDoneLabel);
        il.MarkLabel(useRootLabel);
        il.Emit(OpCodes.Ldloc, rootLocal);
        il.MarkLabel(directoryDoneLabel);
        il.Emit(OpCodes.Stloc, directoryLocal);

        // if (string.IsNullOrEmpty(directory)) return baseName ?? ""
        var hasDirectoryLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, directoryLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brfalse, hasDirectoryLabel);

        // Return baseName or ""
        var returnEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, returnEmptyLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(returnEmptyLabel);
        il.Emit(OpCodes.Ret);

        // return directory + "/" + baseName
        il.MarkLabel(hasDirectoryLabel);
        il.Emit(OpCodes.Ldloc, directoryLocal);
        il.Emit(OpCodes.Ldstr, "/");
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Ret);
    }

    #endregion

    #region Win32 Path Methods

    // Win32 methods follow the same pattern as POSIX but with different separators
    // For brevity, I'll implement the key differences

    private void EmitWin32Join(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Win32Join",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.ObjectArray]);
        runtime.Win32Join = method;

        var il = method.GetILGenerator();

        // Similar to PosixJoin but uses '\\' instead of '/'
        var notEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Brtrue, notEmptyLabel);
        il.Emit(OpCodes.Ldstr, ".");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notEmptyLabel);

        var listLocal = il.DeclareLocal(_types.ListOfString);
        il.Emit(OpCodes.Newobj, _types.ListOfString.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, listLocal);

        var indexLocal = il.DeclareLocal(_types.Int32);
        var lengthLocal = il.DeclareLocal(_types.Int32);
        var partLocal = il.DeclareLocal(_types.String);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, lengthLocal);

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var continueLabel = il.DefineLabel();

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, lengthLocal);
        il.Emit(OpCodes.Bge, loopEndLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);

        var argNullLabel = il.DefineLabel();
        var partDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, argNullLabel);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Br, partDoneLabel);
        il.MarkLabel(argNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(partDoneLabel);
        il.Emit(OpCodes.Stloc, partLocal);

        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brtrue, continueLabel);

        // Replace '/' with '\\'
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Stloc, partLocal);

        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("Add", [_types.String])!);

        il.MarkLabel(continueLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);

        var joinLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("get_Count")!);
        il.Emit(OpCodes.Brtrue, joinLabel);
        il.Emit(OpCodes.Ldstr, ".");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(joinLabel);
        il.Emit(OpCodes.Ldstr, "\\");
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Join", [_types.String, typeof(IEnumerable<string>)])!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitWin32Resolve(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Win32Resolve",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.ObjectArray]);
        runtime.Win32Resolve = method;

        var il = method.GetILGenerator();

        // var result = Directory.GetCurrentDirectory().Replace('/', '\\')
        var resultLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Call, _types.Directory.GetMethod("GetCurrentDirectory")!);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Stloc, resultLocal);

        var indexLocal = il.DeclareLocal(_types.Int32);
        var lengthLocal = il.DeclareLocal(_types.Int32);
        var partLocal = il.DeclareLocal(_types.String);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, lengthLocal);

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var continueLabel = il.DefineLabel();
        var appendLabel = il.DefineLabel();

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, lengthLocal);
        il.Emit(OpCodes.Bge, loopEndLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        var argNullLabel = il.DefineLabel();
        var partDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, argNullLabel);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Br, partDoneLabel);
        il.MarkLabel(argNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(partDoneLabel);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Stloc, partLocal);

        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brtrue, continueLabel);

        // Check if Win32 absolute
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Call, runtime.Win32IsAbsolute);
        il.Emit(OpCodes.Brfalse, appendLabel);

        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Br, continueLabel);

        il.MarkLabel(appendLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "\\");
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Stloc, resultLocal);

        il.MarkLabel(continueLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Call, runtime.Win32Normalize);
        il.Emit(OpCodes.Ret);
    }

    private void EmitWin32Basename(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Win32Basename",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String, _types.String]);
        runtime.Win32Basename = method;

        var il = method.GetILGenerator();

        // var lastSep = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'))
        var lastSepLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("LastIndexOf", [_types.Char])!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("LastIndexOf", [_types.Char])!);
        il.Emit(OpCodes.Call, _types.MathMaxInt32);
        il.Emit(OpCodes.Stloc, lastSepLocal);

        // var filename = lastSep >= 0 ? path[(lastSep + 1)..] : path
        var filenameLocal = il.DeclareLocal(_types.String);
        var usePathLabel = il.DefineLabel();
        var filenameDoneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, usePathLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32])!);
        il.Emit(OpCodes.Br, filenameDoneLabel);

        il.MarkLabel(usePathLabel);
        il.Emit(OpCodes.Ldarg_0);

        il.MarkLabel(filenameDoneLabel);
        il.Emit(OpCodes.Stloc, filenameLocal);

        // Check for extension removal (case-insensitive for Win32)
        var returnLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brtrue, returnLabel);

        il.Emit(OpCodes.Ldloc, filenameLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4, (int)StringComparison.OrdinalIgnoreCase);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("EndsWith", [_types.String, typeof(StringComparison)])!);
        il.Emit(OpCodes.Brfalse, returnLabel);

        il.Emit(OpCodes.Ldloc, filenameLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, filenameLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Length")!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Length")!);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, filenameLocal);

        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ldloc, filenameLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitWin32Dirname(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Win32Dirname",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String]);
        runtime.Win32Dirname = method;

        var il = method.GetILGenerator();

        var lastSepLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("LastIndexOf", [_types.Char])!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("LastIndexOf", [_types.Char])!);
        il.Emit(OpCodes.Call, _types.MathMaxInt32);
        il.Emit(OpCodes.Stloc, lastSepLocal);

        // if (lastSep < 0) return "."
        var checkDriveLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Bge, checkDriveLabel);
        il.Emit(OpCodes.Ldstr, ".");
        il.Emit(OpCodes.Ret);

        // Check for drive root
        il.MarkLabel(checkDriveLabel);
        var substringLabel = il.DefineLabel();

        // if (lastSep <= 2 && path.Length >= 2 && path[1] == ':')
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Bgt, substringLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Length")!);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Blt, substringLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Ldc_I4, (int)':');
        il.Emit(OpCodes.Bne_Un, substringLabel);

        // return path[..(lastSep + 1)]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Ret);

        // return path[..lastSep]
        il.MarkLabel(substringLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitWin32Normalize(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Win32Normalize",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String]);
        runtime.Win32Normalize = method;

        var il = method.GetILGenerator();

        // if (string.IsNullOrEmpty(path)) return "."
        var notEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brfalse, notEmptyLabel);
        il.Emit(OpCodes.Ldstr, ".");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notEmptyLabel);

        // path = path.Replace('/', '\\')
        var pathLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Stloc, pathLocal);

        // Extract root (e.g., "C:\\" or "\\\\")
        var rootLocal = il.DeclareLocal(_types.String);
        var remainderLocal = il.DeclareLocal(_types.String);

        // Check for drive letter: path.Length >= 2 && path[1] == ':'
        var checkUncLabel = il.DefineLabel();
        var noRootLabel = il.DefineLabel();
        var processPartsLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Length")!);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Blt, checkUncLabel);

        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Ldc_I4, (int)':');
        il.Emit(OpCodes.Bne_Un, checkUncLabel);

        // Check if path[2] is separator
        var hasThirdCharLabel = il.DefineLabel();
        var driveOnlyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Length")!);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Bge, hasThirdCharLabel);

        // Only "C:" - root is "C:", remainder is ""
        il.MarkLabel(driveOnlyLabel);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, rootLocal);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, remainderLocal);
        il.Emit(OpCodes.Br, processPartsLabel);

        il.MarkLabel(hasThirdCharLabel);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Bne_Un, driveOnlyLabel);

        // "C:\..." - root is "C:\", remainder is rest
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, rootLocal);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32])!);
        il.Emit(OpCodes.Stloc, remainderLocal);
        il.Emit(OpCodes.Br, processPartsLabel);

        // Check for UNC path
        il.MarkLabel(checkUncLabel);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldstr, "\\\\");
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("StartsWith", [_types.String])!);
        il.Emit(OpCodes.Brfalse, noRootLabel);

        // UNC path - root is "\\", remainder is rest
        il.Emit(OpCodes.Ldstr, "\\\\");
        il.Emit(OpCodes.Stloc, rootLocal);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32])!);
        il.Emit(OpCodes.Stloc, remainderLocal);
        il.Emit(OpCodes.Br, processPartsLabel);

        // No root
        il.MarkLabel(noRootLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, rootLocal);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Stloc, remainderLocal);

        // Process parts
        il.MarkLabel(processPartsLabel);

        // var parts = remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries)
        var partsLocal = il.DeclareLocal(_types.StringArray);
        il.Emit(OpCodes.Ldloc, remainderLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Ldc_I4, (int)StringSplitOptions.RemoveEmptyEntries);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Split", [_types.Char, typeof(StringSplitOptions)])!);
        il.Emit(OpCodes.Stloc, partsLocal);

        // var stack = new List<string>()
        var stackLocal = il.DeclareLocal(_types.ListOfString);
        il.Emit(OpCodes.Newobj, _types.ListOfStringDefaultCtor);
        il.Emit(OpCodes.Stloc, stackLocal);

        // Process each part
        var indexLocal = il.DeclareLocal(_types.Int32);
        var lengthLocal = il.DeclareLocal(_types.Int32);
        var partLocal = il.DeclareLocal(_types.String);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, lengthLocal);

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var continueLabel = il.DefineLabel();
        var dotDotLabel = il.DefineLabel();
        var addPartLabel = il.DefineLabel();

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, lengthLocal);
        il.Emit(OpCodes.Bge, loopEndLabel);

        // var part = parts[i]
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, partLocal);

        // if (part == ".") continue
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Ldstr, ".");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, continueLabel);

        // if (part == "..")
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Ldstr, "..");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, dotDotLabel);

        // else: stack.Add(part)
        il.Emit(OpCodes.Br, addPartLabel);

        // Handle ".."
        il.MarkLabel(dotDotLabel);
        // if (stack.Count > 0 && stack[^1] != "..")
        var addDotDotLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfStringGetCount);
        il.Emit(OpCodes.Brfalse, addDotDotLabel);

        // Check if last element is not ".."
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfStringGetCount);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Callvirt, _types.ListOfStringGetItem);
        il.Emit(OpCodes.Ldstr, "..");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, addDotDotLabel);

        // stack.RemoveAt(stack.Count - 1)
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfStringGetCount);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("RemoveAt", [_types.Int32])!);
        il.Emit(OpCodes.Br, continueLabel);

        // Add ".." to stack (for relative paths going above root)
        il.MarkLabel(addDotDotLabel);
        // For absolute paths, don't add ".." if we're at root
        il.Emit(OpCodes.Ldloc, rootLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Length")!);
        il.Emit(OpCodes.Brtrue, continueLabel); // Skip adding ".." for absolute paths

        il.MarkLabel(addPartLabel);
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Ldloc, partLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfStringAdd);

        il.MarkLabel(continueLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);

        // Build result: root + string.Join("\\", stack)
        il.Emit(OpCodes.Ldloc, rootLocal);
        il.Emit(OpCodes.Ldstr, "\\");
        il.Emit(OpCodes.Ldloc, stackLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfStringToArray);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Join", [_types.String, _types.StringArray])!);
        il.Emit(OpCodes.Call, _types.StringConcat2);
        il.Emit(OpCodes.Ret);
    }

    private void EmitWin32IsAbsolute(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Win32IsAbsolute",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.String]);
        runtime.Win32IsAbsolute = method;

        var il = method.GetILGenerator();

        // if (string.IsNullOrEmpty(path)) return false
        var notEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brfalse, notEmptyLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notEmptyLabel);

        // Check for drive letter: path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')
        var checkUncLabel = il.DefineLabel();
        var returnTrueLabel = il.DefineLabel();
        var returnFalseLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Length")!);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Blt, checkUncLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Call, _types.Char.GetMethod("IsLetter", [_types.Char])!);
        il.Emit(OpCodes.Brfalse, checkUncLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Ldc_I4, (int)':');
        il.Emit(OpCodes.Bne_Un, checkUncLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        var char2Local = il.DeclareLocal(_types.Char);
        il.Emit(OpCodes.Stloc, char2Local);
        // Check if path[2] == '\\' or path[2] == '/'
        il.Emit(OpCodes.Ldloc, char2Local);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Beq, returnTrueLabel);
        il.Emit(OpCodes.Ldloc, char2Local);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Beq, returnTrueLabel);
        il.Emit(OpCodes.Br, checkUncLabel);

        // Check for UNC path: path.Length >= 2 && ((path[0] == '\\' && path[1] == '\\') || (path[0] == '/' && path[1] == '/'))
        il.MarkLabel(checkUncLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Length")!);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Blt, returnFalseLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);

        // Check \\ or //
        var char0Local = il.DeclareLocal(_types.Char);
        var char1Local = il.DeclareLocal(_types.Char);
        il.Emit(OpCodes.Stloc, char1Local);
        il.Emit(OpCodes.Stloc, char0Local);

        var checkForwardSlashLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, char0Local);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Bne_Un, checkForwardSlashLabel);
        il.Emit(OpCodes.Ldloc, char1Local);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Beq, returnTrueLabel);

        il.MarkLabel(checkForwardSlashLabel);
        il.Emit(OpCodes.Ldloc, char0Local);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Bne_Un, returnFalseLabel);
        il.Emit(OpCodes.Ldloc, char1Local);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Beq, returnTrueLabel);

        il.MarkLabel(returnFalseLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnTrueLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }

    private void EmitWin32Relative(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Win32Relative",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String, _types.String]);
        runtime.Win32Relative = method;

        var il = method.GetILGenerator();

        var fromLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.Win32Normalize);
        il.Emit(OpCodes.Stloc, fromLocal);

        var toLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.Win32Normalize);
        il.Emit(OpCodes.Stloc, toLocal);

        il.Emit(OpCodes.Ldloc, fromLocal);
        il.Emit(OpCodes.Ldloc, toLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Call, runtime.ComputeRelative);
        il.Emit(OpCodes.Ret);
    }

    private void EmitWin32Parse(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Win32Parse",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.DictionaryStringObject,
            [_types.String]);
        runtime.Win32Parse = method;

        var il = method.GetILGenerator();

        // Create result dictionary
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObject.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, dictLocal);

        // Normalize slashes first
        var pathLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)'/');
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Stloc, pathLocal);

        // Extract root (drive letter or UNC)
        var rootLocal = il.DeclareLocal(_types.String);
        var rootLengthLocal = il.DeclareLocal(_types.Int32);
        var noRootLabel = il.DefineLabel();
        var rootDoneLabel = il.DefineLabel();

        // Check for drive letter: path.Length >= 2 && path[1] == ':'
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Length")!);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Blt, noRootLabel);

        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Ldc_I4, (int)':');
        il.Emit(OpCodes.Bne_Un, noRootLabel);

        // Check if path[2] is separator
        var shortRootLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Length")!);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Blt, shortRootLabel);

        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Bne_Un, shortRootLabel);

        // Root is "C:\"
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, rootLocal);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Stloc, rootLengthLocal);
        il.Emit(OpCodes.Br, rootDoneLabel);

        il.MarkLabel(shortRootLabel);
        // Root is "C:"
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, rootLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Stloc, rootLengthLocal);
        il.Emit(OpCodes.Br, rootDoneLabel);

        il.MarkLabel(noRootLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, rootLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, rootLengthLocal);

        il.MarkLabel(rootDoneLabel);

        // Find last separator for dir/base split
        var lastSepLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("LastIndexOf", [_types.Char])!);
        il.Emit(OpCodes.Stloc, lastSepLocal);

        // Extract dir
        var dirLocal = il.DeclareLocal(_types.String);
        var noSepLabel = il.DefineLabel();
        var dirDoneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, noSepLabel);

        // dir = path.Substring(0, lastSep)
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, dirLocal);
        il.Emit(OpCodes.Br, dirDoneLabel);

        il.MarkLabel(noSepLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, dirLocal);

        il.MarkLabel(dirDoneLabel);

        // Extract base (everything after last separator)
        var baseLocal = il.DeclareLocal(_types.String);
        var noSepForBaseLabel = il.DefineLabel();
        var baseDoneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, noSepForBaseLabel);

        // base = path.Substring(lastSep + 1)
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldloc, lastSepLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32])!);
        il.Emit(OpCodes.Stloc, baseLocal);
        il.Emit(OpCodes.Br, baseDoneLabel);

        il.MarkLabel(noSepForBaseLabel);
        // base = path (no separator means whole path is base)
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Stloc, baseLocal);

        il.MarkLabel(baseDoneLabel);

        // Extract name and ext from base
        var nameLocal = il.DeclareLocal(_types.String);
        var extLocal = il.DeclareLocal(_types.String);
        var dotPosLocal = il.DeclareLocal(_types.Int32);

        // Find last dot in base
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Ldc_I4, (int)'.');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("LastIndexOf", [_types.Char])!);
        il.Emit(OpCodes.Stloc, dotPosLocal);

        var noDotLabel = il.DefineLabel();
        var extDoneLabel = il.DefineLabel();

        // If no dot, or dot is first char (hidden file), name = base, ext = ""
        il.Emit(OpCodes.Ldloc, dotPosLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, noDotLabel);

        // name = base.Substring(0, dotPos)
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, dotPosLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, nameLocal);

        // ext = base.Substring(dotPos)
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Ldloc, dotPosLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32])!);
        il.Emit(OpCodes.Stloc, extLocal);
        il.Emit(OpCodes.Br, extDoneLabel);

        il.MarkLabel(noDotLabel);
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Stloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, extLocal);

        il.MarkLabel(extDoneLabel);

        // Set dictionary values
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "root");
        il.Emit(OpCodes.Ldloc, rootLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectSetItem);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "dir");
        il.Emit(OpCodes.Ldloc, dirLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectSetItem);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "base");
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectSetItem);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectSetItem);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "ext");
        il.Emit(OpCodes.Ldloc, extLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectSetItem);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitWin32Format(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "Win32Format",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]);
        runtime.Win32Format = method;

        var il = method.GetILGenerator();

        // Similar to PosixFormat but uses '\\' separator
        var notNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, notNullLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notNullLabel);

        var dirLocal = il.DeclareLocal(_types.String);
        var rootLocal = il.DeclareLocal(_types.String);
        var baseLocal = il.DeclareLocal(_types.String);
        var nameLocal = il.DeclareLocal(_types.String);
        var extLocal = il.DeclareLocal(_types.String);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "dir");
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, runtime.GetOptionString);
        il.Emit(OpCodes.Stloc, dirLocal);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "root");
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, runtime.GetOptionString);
        il.Emit(OpCodes.Stloc, rootLocal);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "base");
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, runtime.GetOptionString);
        il.Emit(OpCodes.Stloc, baseLocal);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, runtime.GetOptionString);
        il.Emit(OpCodes.Stloc, nameLocal);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "ext");
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, runtime.GetOptionString);
        il.Emit(OpCodes.Stloc, extLocal);

        var baseNotEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brfalse, baseNotEmptyLabel);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldloc, extLocal);
        il.Emit(OpCodes.Call, _types.StringConcat2);
        il.Emit(OpCodes.Stloc, baseLocal);

        il.MarkLabel(baseNotEmptyLabel);

        var directoryLocal = il.DeclareLocal(_types.String);
        var useRootLabel = il.DefineLabel();
        var directoryDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dirLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brtrue, useRootLabel);
        il.Emit(OpCodes.Ldloc, dirLocal);
        il.Emit(OpCodes.Br, directoryDoneLabel);
        il.MarkLabel(useRootLabel);
        il.Emit(OpCodes.Ldloc, rootLocal);
        il.MarkLabel(directoryDoneLabel);
        il.Emit(OpCodes.Stloc, directoryLocal);

        var hasDirectoryLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, directoryLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brfalse, hasDirectoryLabel);

        var returnEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, returnEmptyLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(returnEmptyLabel);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(hasDirectoryLabel);
        il.Emit(OpCodes.Ldloc, directoryLocal);
        il.Emit(OpCodes.Ldstr, "\\");
        il.Emit(OpCodes.Ldloc, baseLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Ret);
    }

    #endregion

    #region Shared Path Helpers

    /// <summary>
    /// Emits: public static string ComputeRelative(string from, string to, char separator)
    /// Computes the relative path between two paths.
    /// </summary>
    private void EmitComputeRelative(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ComputeRelative",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String, _types.String, _types.Char]);
        runtime.ComputeRelative = method;

        var il = method.GetILGenerator();

        // Split both paths
        var fromPartsLocal = il.DeclareLocal(_types.StringArray);
        var toPartsLocal = il.DeclareLocal(_types.StringArray);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4, (int)StringSplitOptions.RemoveEmptyEntries);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Split", [_types.Char, typeof(StringSplitOptions)])!);
        il.Emit(OpCodes.Stloc, fromPartsLocal);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4, (int)StringSplitOptions.RemoveEmptyEntries);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Split", [_types.Char, typeof(StringSplitOptions)])!);
        il.Emit(OpCodes.Stloc, toPartsLocal);

        // Find common prefix
        var commonLengthLocal = il.DeclareLocal(_types.Int32);
        var minLengthLocal = il.DeclareLocal(_types.Int32);
        var iLocal = il.DeclareLocal(_types.Int32);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, commonLengthLocal);

        il.Emit(OpCodes.Ldloc, fromPartsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldloc, toPartsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Call, _types.MathMinInt32);
        il.Emit(OpCodes.Stloc, minLengthLocal);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, minLengthLocal);
        il.Emit(OpCodes.Bge, loopEndLabel);

        // Compare parts[i]
        il.Emit(OpCodes.Ldloc, fromPartsLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Ldloc, toPartsLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brfalse, loopEndLabel);

        il.Emit(OpCodes.Ldloc, commonLengthLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, commonLengthLocal);

        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);

        // Build result
        var resultLocal = il.DeclareLocal(_types.ListOfString);
        il.Emit(OpCodes.Newobj, _types.ListOfString.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        // Add ".." for each remaining part in 'from'
        il.Emit(OpCodes.Ldloc, commonLengthLocal);
        il.Emit(OpCodes.Stloc, iLocal);

        var addDotsLoopStart = il.DefineLabel();
        var addDotsLoopEnd = il.DefineLabel();

        il.MarkLabel(addDotsLoopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, fromPartsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, addDotsLoopEnd);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "..");
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("Add", [_types.String])!);

        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, addDotsLoopStart);

        il.MarkLabel(addDotsLoopEnd);

        // Add remaining parts from 'to'
        il.Emit(OpCodes.Ldloc, commonLengthLocal);
        il.Emit(OpCodes.Stloc, iLocal);

        var addToLoopStart = il.DefineLabel();
        var addToLoopEnd = il.DefineLabel();

        il.MarkLabel(addToLoopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, toPartsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, addToLoopEnd);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, toPartsLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("Add", [_types.String])!);

        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, addToLoopStart);

        il.MarkLabel(addToLoopEnd);

        // return result.Count == 0 ? "." : string.Join(separator.ToString(), result)
        var joinLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfString.GetMethod("get_Count")!);
        il.Emit(OpCodes.Brtrue, joinLabel);
        il.Emit(OpCodes.Ldstr, ".");
        il.Emit(OpCodes.Ret);

        il.MarkLabel(joinLabel);
        il.Emit(OpCodes.Ldarga, 2);
        il.Emit(OpCodes.Call, _types.Char.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Join", [_types.String, typeof(IEnumerable<string>)])!);
        il.Emit(OpCodes.Ret);
    }

    #endregion
}
