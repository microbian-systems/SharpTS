using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits all async fs methods with inline IL (no external FsAsyncHelpers dependency).
    /// Each method calls the corresponding sync implementation and wraps result with Task.FromResult.
    /// </summary>
    private void EmitFsAsyncMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Emit all async fs methods with inline IL
        EmitFsReadFileAsync(typeBuilder, runtime);
        EmitFsWriteFileAsync(typeBuilder, runtime);
        EmitFsAppendFileAsync(typeBuilder, runtime);
        EmitFsStatAsync(typeBuilder, runtime);
        EmitFsLstatAsync(typeBuilder, runtime);
        EmitFsUnlinkAsync(typeBuilder, runtime);
        EmitFsMkdirAsync(typeBuilder, runtime);
        EmitFsRmdirAsync(typeBuilder, runtime);
        EmitFsRmAsync(typeBuilder, runtime);
        EmitFsReaddirAsync(typeBuilder, runtime);
        EmitFsRenameAsync(typeBuilder, runtime);
        EmitFsCopyFileAsync(typeBuilder, runtime);
        EmitFsAccessAsync(typeBuilder, runtime);
        EmitFsChmodAsync(typeBuilder, runtime);
        EmitFsTruncateAsync(typeBuilder, runtime);
        EmitFsUtimesAsync(typeBuilder, runtime);
        EmitFsReadlinkAsync(typeBuilder, runtime);
        EmitFsRealpathAsync(typeBuilder, runtime);
        EmitFsSymlinkAsync(typeBuilder, runtime);
        EmitFsLinkAsync(typeBuilder, runtime);
        EmitFsMkdtempAsync(typeBuilder, runtime);
    }

    /// <summary>
    /// Begins a try-catch block for an async fs method.
    /// Returns fromResult, a local for the result task, and a label for after the try-catch.
    /// The caller should emit the sync call body, then call EndFsAsyncTryCatch.
    /// </summary>
    private (MethodInfo fromResult, LocalBuilder resultTask, Label afterTry) BeginFsAsyncTryCatch(ILGenerator il)
    {
        var fromResult = typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(_types.Object);
        var resultTask = il.DeclareLocal(_types.TaskOfObject);
        var afterTry = il.DefineLabel();
        il.BeginExceptionBlock();
        return (fromResult, resultTask, afterTry);
    }

    /// <summary>
    /// Ends the try-catch block for an async fs method.
    /// Expects Task&lt;object&gt; on the stack (result of Task.FromResult).
    /// Stores it, catches exceptions as Task.FromException, and returns.
    /// </summary>
    private void EndFsAsyncTryCatch(ILGenerator il, LocalBuilder resultTask, Label afterTry)
    {
        var fromException = typeof(Task).GetMethod("FromException", 1, [typeof(Exception)])!.MakeGenericMethod(_types.Object);
        il.Emit(OpCodes.Stloc, resultTask);
        il.Emit(OpCodes.Leave, afterTry);

        il.BeginCatchBlock(typeof(Exception));
        il.Emit(OpCodes.Call, fromException);
        il.Emit(OpCodes.Stloc, resultTask);
        il.Emit(OpCodes.Leave, afterTry);

        il.EndExceptionBlock();
        il.MarkLabel(afterTry);
        il.Emit(OpCodes.Ldloc, resultTask);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsReadFileAsync(object path, object? encoding)
    /// Calls FsReadFileSync and wraps result in Task.FromResult.
    /// </summary>
    private void EmitFsReadFileAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsReadFileAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object]
        );
        runtime.FsReadFileAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsReadFileSync(path, encoding)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.FsReadFileSync);

        // Wrap in Task.FromResult
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "readFile", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsWriteFileAsync(object path, object data, object? options)
    /// Calls FsWriteFileSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsWriteFileAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsWriteFileAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.FsWriteFileAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsWriteFileSync(path, data) - ignores options for now
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.FsWriteFileSync);

        // Return Task.FromResult(null) for void operations
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "writeFile", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsAppendFileAsync(object path, object data, object? options)
    /// Calls FsAppendFileSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsAppendFileAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsAppendFileAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.FsAppendFileAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsAppendFileSync(path, data)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.FsAppendFileSync);

        // Return Task.FromResult(null) for void operations
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "appendFile", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsStatAsync(object path)
    /// Calls FsStatSync and wraps result in Task.FromResult.
    /// </summary>
    private void EmitFsStatAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsStatAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object]
        );
        runtime.FsStatAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsStatSync(path)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.FsStatSync);

        // Wrap in Task.FromResult
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "stat", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsLstatAsync(object path)
    /// Calls FsLstatSync and wraps result in Task.FromResult.
    /// </summary>
    private void EmitFsLstatAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsLstatAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object]
        );
        runtime.FsLstatAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsLstatSync(path)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.FsLstatSync);

        // Wrap in Task.FromResult
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "lstat", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsUnlinkAsync(object path)
    /// Calls FsUnlinkSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsUnlinkAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsUnlinkAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object]
        );
        runtime.FsUnlinkAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsUnlinkSync(path)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.FsUnlinkSync);

        // Return Task.FromResult(null)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "unlink", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsMkdirAsync(object path, object? options)
    /// Calls FsMkdirSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsMkdirAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsMkdirAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object]
        );
        runtime.FsMkdirAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsMkdirSync(path, options)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.FsMkdirSync);

        // Return Task.FromResult(null)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "mkdir", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsRmdirAsync(object path, object? options)
    /// Calls FsRmdirSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsRmdirAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsRmdirAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object]
        );
        runtime.FsRmdirAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsRmdirSync(path, options)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.FsRmdirSync);

        // Return Task.FromResult(null)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "rmdir", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsRmAsync(object path, object? options)
    /// Implements rm with recursive/force options inline.
    /// </summary>
    private void EmitFsRmAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsRmAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object]
        );
        runtime.FsRmAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Convert path to string
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.Stringify);
        var pathLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Stloc, pathLocal);

        // Check options for recursive and force flags
        var recursiveLocal = il.DeclareLocal(_types.Boolean);
        var forceLocal = il.DeclareLocal(_types.Boolean);
        var afterOptionsLabel = il.DefineLabel();
        var afterRecursiveLabel = il.DefineLabel();

        // If options is null, skip
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, afterOptionsLabel);

        // Check recursive option
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "recursive");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Call, runtime.IsTruthy);
        il.Emit(OpCodes.Stloc, recursiveLocal);

        // Check force option
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "force");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Call, runtime.IsTruthy);
        il.Emit(OpCodes.Stloc, forceLocal);
        il.Emit(OpCodes.Br, afterRecursiveLabel);

        il.MarkLabel(afterOptionsLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, recursiveLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, forceLocal);

        il.MarkLabel(afterRecursiveLabel);

        // Check if path exists
        var existsLabel = il.DefineLabel();
        var doneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.File, "Exists", _types.String));
        il.Emit(OpCodes.Brtrue, existsLabel);

        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Directory, "Exists", _types.String));
        il.Emit(OpCodes.Brtrue, existsLabel);

        // Path doesn't exist - if force, just return; otherwise throw
        var throwLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, forceLocal);
        il.Emit(OpCodes.Brfalse, throwLabel);
        il.Emit(OpCodes.Br, doneLabel);

        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "ENOENT: no such file or directory");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        il.MarkLabel(existsLabel);

        // Check if it's a directory
        var isFileLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Directory, "Exists", _types.String));
        il.Emit(OpCodes.Brfalse, isFileLabel);

        // It's a directory - delete recursively if recursive flag set
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Ldloc, recursiveLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Directory, "Delete", _types.String, _types.Boolean));
        il.Emit(OpCodes.Br, doneLabel);

        // It's a file - delete it
        il.MarkLabel(isFileLabel);
        il.Emit(OpCodes.Ldloc, pathLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.File, "Delete", _types.String));

        il.MarkLabel(doneLabel);

        // Return Task.FromResult(null)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "rm", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsReaddirAsync(object path, object? options)
    /// Calls FsReaddirSync and wraps result in Task.FromResult.
    /// </summary>
    private void EmitFsReaddirAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsReaddirAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object]
        );
        runtime.FsReaddirAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsReaddirSync(path, options)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.FsReaddirSync);

        // Wrap in Task.FromResult
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "readdir", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsRenameAsync(object oldPath, object newPath)
    /// Calls FsRenameSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsRenameAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsRenameAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object]
        );
        runtime.FsRenameAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsRenameSync(oldPath, newPath)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.FsRenameSync);

        // Return Task.FromResult(null)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "rename", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsCopyFileAsync(object src, object dest, object? mode)
    /// Calls FsCopyFileSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsCopyFileAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsCopyFileAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.FsCopyFileAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsCopyFileSync(src, dest) - ignores mode for now
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.FsCopyFileSync);

        // Return Task.FromResult(null)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "copyFile", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsAccessAsync(object path, object? mode)
    /// Calls FsAccessSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsAccessAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsAccessAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object]
        );
        runtime.FsAccessAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsAccessSync(path, mode)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.FsAccessSync);

        // Return Task.FromResult(null)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "access", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsChmodAsync(object path, object mode)
    /// Calls FsChmodSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsChmodAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsChmodAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object]
        );
        runtime.FsChmodAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsChmodSync(path, mode)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.FsChmodSync);

        // Return Task.FromResult(null)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "chmod", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsTruncateAsync(object path, object? len)
    /// Calls FsTruncateSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsTruncateAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsTruncateAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object]
        );
        runtime.FsTruncateAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsTruncateSync(path, len)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.FsTruncateSync);

        // Return Task.FromResult(null)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "truncate", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsUtimesAsync(object path, object atime, object mtime)
    /// Calls FsUtimesSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsUtimesAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsUtimesAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.FsUtimesAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsUtimesSync(path, atime, mtime)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, runtime.FsUtimesSync);

        // Return Task.FromResult(null)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "utimes", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsReadlinkAsync(object path)
    /// Calls FsReadlinkSync and wraps result in Task.FromResult.
    /// </summary>
    private void EmitFsReadlinkAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsReadlinkAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object]
        );
        runtime.FsReadlinkAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsReadlinkSync(path)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.FsReadlinkSync);

        // Wrap in Task.FromResult
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "readlink", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsRealpathAsync(object path)
    /// Calls FsRealpathSync and wraps result in Task.FromResult.
    /// </summary>
    private void EmitFsRealpathAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsRealpathAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object]
        );
        runtime.FsRealpathAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsRealpathSync(path)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.FsRealpathSync);

        // Wrap in Task.FromResult
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "realpath", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsSymlinkAsync(object target, object path, object? type)
    /// Calls FsSymlinkSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsSymlinkAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsSymlinkAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.FsSymlinkAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsSymlinkSync(target, path, type)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, runtime.FsSymlinkSync);

        // Return Task.FromResult(null)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "symlink", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsLinkAsync(object existingPath, object newPath)
    /// Calls FsLinkSync and returns Task.FromResult(null).
    /// </summary>
    private void EmitFsLinkAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsLinkAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object, _types.Object]
        );
        runtime.FsLinkAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsLinkSync(existingPath, newPath)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.FsLinkSync);

        // Return Task.FromResult(null)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "link", method);
    }

    /// <summary>
    /// Emits: Task&lt;object?&gt; FsMkdtempAsync(object prefix)
    /// Calls FsMkdtempSync and wraps result in Task.FromResult.
    /// </summary>
    private void EmitFsMkdtempAsync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "FsMkdtempAsync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.TaskOfObject,
            [_types.Object]
        );
        runtime.FsMkdtempAsync = method;

        var il = method.GetILGenerator();
        var (fromResult, resultTask, afterTry) = BeginFsAsyncTryCatch(il);

        // Call FsMkdtempSync(prefix)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.FsMkdtempSync);

        // Wrap in Task.FromResult
        il.Emit(OpCodes.Call, fromResult);
        EndFsAsyncTryCatch(il, resultTask, afterTry);

        runtime.RegisterBuiltInModuleMethod("fs/promises", "mkdtemp", method);
    }

    /// <summary>
    /// Emits: public static object FsGetPromisesNamespace()
    /// Returns a namespace object containing all fs.promises methods.
    /// Creates TSFunctions that wrap the async helper methods and return Promises.
    /// </summary>
    private void EmitFsGetPromisesNamespace(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // First, emit wrapper methods that call async helpers and wrap results in Promises
        EmitFsPromisesWrapperMethods(typeBuilder, runtime);

        var method = typeBuilder.DefineMethod(
            "FsGetPromisesNamespace",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes
        );
        runtime.FsGetPromisesNamespace = method;

        var il = method.GetILGenerator();

        // Create a new Dictionary<string, object?>
        var dictCtor = _types.DictionaryStringObject.GetConstructor(Type.EmptyTypes)!;
        var addMethod = _types.DictionaryStringObject.GetMethod("Add", [typeof(string), typeof(object)])!;

        il.Emit(OpCodes.Newobj, dictCtor);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        // Add each wrapper method as a TSFunction
        var fsPromisesWrappers = runtime.FsPromisesWrapperMethods;
        foreach (var (name, wrapper) in fsPromisesWrappers)
        {
            il.Emit(OpCodes.Ldloc, dictLocal);
            il.Emit(OpCodes.Ldstr, name);

            // Create TSFunction: new $TSFunction(null, wrapperMethod)
            il.Emit(OpCodes.Ldnull); // target
            il.Emit(OpCodes.Ldtoken, wrapper);
            il.Emit(OpCodes.Call, _types.GetMethod(typeof(MethodBase), "GetMethodFromHandle", typeof(RuntimeMethodHandle)));
            il.Emit(OpCodes.Castclass, typeof(MethodInfo));
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);

            il.Emit(OpCodes.Call, addMethod);
        }

        // Add constants
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "constants");
        il.Emit(OpCodes.Call, runtime.FsGetConstants);
        il.Emit(OpCodes.Call, addMethod);

        // Create a SharpTSObject from the dictionary
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits wrapper methods for fs.promises that call the async helpers and wrap results in Promises.
    /// Each wrapper method takes List&lt;object?&gt; args (for TSFunction compatibility).
    /// </summary>
    private void EmitFsPromisesWrapperMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.FsPromisesWrapperMethods = new Dictionary<string, MethodBuilder>();

        EmitPromisesWrapper(typeBuilder, runtime, "readFile", runtime.FsReadFileAsync, 2);
        EmitPromisesWrapper(typeBuilder, runtime, "writeFile", runtime.FsWriteFileAsync, 3);
        EmitPromisesWrapper(typeBuilder, runtime, "appendFile", runtime.FsAppendFileAsync, 3);
        EmitPromisesWrapper(typeBuilder, runtime, "stat", runtime.FsStatAsync, 1);
        EmitPromisesWrapper(typeBuilder, runtime, "lstat", runtime.FsLstatAsync, 1);
        EmitPromisesWrapper(typeBuilder, runtime, "unlink", runtime.FsUnlinkAsync, 1);
        EmitPromisesWrapper(typeBuilder, runtime, "mkdir", runtime.FsMkdirAsync, 2);
        EmitPromisesWrapper(typeBuilder, runtime, "rmdir", runtime.FsRmdirAsync, 2);
        EmitPromisesWrapper(typeBuilder, runtime, "rm", runtime.FsRmAsync, 2);
        EmitPromisesWrapper(typeBuilder, runtime, "readdir", runtime.FsReaddirAsync, 2);
        EmitPromisesWrapper(typeBuilder, runtime, "rename", runtime.FsRenameAsync, 2);
        EmitPromisesWrapper(typeBuilder, runtime, "copyFile", runtime.FsCopyFileAsync, 3);
        EmitPromisesWrapper(typeBuilder, runtime, "access", runtime.FsAccessAsync, 2);
        EmitPromisesWrapper(typeBuilder, runtime, "chmod", runtime.FsChmodAsync, 2);
        EmitPromisesWrapper(typeBuilder, runtime, "truncate", runtime.FsTruncateAsync, 2);
        EmitPromisesWrapper(typeBuilder, runtime, "utimes", runtime.FsUtimesAsync, 3);
        EmitPromisesWrapper(typeBuilder, runtime, "readlink", runtime.FsReadlinkAsync, 1);
        EmitPromisesWrapper(typeBuilder, runtime, "realpath", runtime.FsRealpathAsync, 1);
        EmitPromisesWrapper(typeBuilder, runtime, "symlink", runtime.FsSymlinkAsync, 3);
        EmitPromisesWrapper(typeBuilder, runtime, "link", runtime.FsLinkAsync, 2);
        EmitPromisesWrapper(typeBuilder, runtime, "mkdtemp", runtime.FsMkdtempAsync, 1);
    }

    /// <summary>
    /// Emits a single wrapper method for fs.promises.
    /// Signature: object MethodName(object arg0, object arg1, ...)
    /// Takes individual object parameters to work with TSFunction.Invoke reflection call.
    /// Calls the async helper, wraps the Task in a Promise, and returns it.
    /// </summary>
    private void EmitPromisesWrapper(
        TypeBuilder typeBuilder,
        EmittedRuntime runtime,
        string name,
        MethodBuilder asyncMethod,
        int argCount)
    {
        // Create parameter types - all object, matching the expected arg count
        var paramTypes = new Type[argCount];
        for (int i = 0; i < argCount; i++)
            paramTypes[i] = _types.Object;

        var wrapper = typeBuilder.DefineMethod(
            $"FsPromises_{name}_Wrapper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            paramTypes
        );

        var il = wrapper.GetILGenerator();

        // Load each argument
        for (int i = 0; i < argCount; i++)
        {
            il.Emit(OpCodes.Ldarg, i);
        }

        // Call the async method
        il.Emit(OpCodes.Call, asyncMethod);

        // Wrap the Task in a Promise
        il.Emit(OpCodes.Call, runtime.WrapTaskAsPromise);

        il.Emit(OpCodes.Ret);

        runtime.FsPromisesWrapperMethods[name] = wrapper;
    }
}
