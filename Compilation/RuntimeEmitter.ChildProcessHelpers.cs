using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits child_process module helper methods with full IL (no external dependencies).
    /// </summary>
    private void EmitChildProcessMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitChildProcessNoOp(typeBuilder);
        EmitChildProcessExecSync(typeBuilder, runtime);
        EmitChildProcessSpawnSync(typeBuilder, runtime);
        EmitChildProcessExec(typeBuilder, runtime);
        EmitChildProcessSpawn(typeBuilder, runtime);
        EmitChildProcessExecFileSync(typeBuilder, runtime);
        EmitChildProcessExecFile(typeBuilder, runtime);
        EmitChildProcessFork(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits: public static string ChildProcessExecSync(string command, object options)
    /// Executes a command synchronously and returns stdout.
    /// </summary>
    private void EmitChildProcessExecSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ChildProcessExecSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String, _types.Object]);
        runtime.ChildProcessExecSync = method;
        runtime.RegisterBuiltInModuleMethod("child_process", "execSync", method);

        var il = method.GetILGenerator();

        var startInfoLocal = il.DeclareLocal(_types.ProcessStartInfo);
        var processLocal = il.DeclareLocal(_types.Process);
        var stdoutLocal = il.DeclareLocal(_types.String);
        var stderrLocal = il.DeclareLocal(_types.String);
        var exitCodeLocal = il.DeclareLocal(_types.Int32);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var cwdLocal = il.DeclareLocal(_types.String);
        var tempObjLocal = il.DeclareLocal(_types.Object);

        // var startInfo = new ProcessStartInfo()
        il.Emit(OpCodes.Newobj, _types.ProcessStartInfo.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, startInfoLocal);

        // startInfo.UseShellExecute = false
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("UseShellExecute")!.GetSetMethod()!);

        // startInfo.RedirectStandardOutput = true
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardOutput")!.GetSetMethod()!);

        // startInfo.RedirectStandardError = true
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardError")!.GetSetMethod()!);

        // startInfo.CreateNoWindow = true
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("CreateNoWindow")!.GetSetMethod()!);

        // Platform check: if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        var notWindowsLabel = il.DefineLabel();
        var afterPlatformLabel = il.DefineLabel();

        il.Emit(OpCodes.Call, _types.OSPlatform.GetProperty("Windows")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, _types.RuntimeInformation.GetMethod("IsOSPlatform", [_types.OSPlatform])!);
        il.Emit(OpCodes.Brfalse, notWindowsLabel);

        // Windows: startInfo.FileName = "cmd.exe"
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldstr, "cmd.exe");
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("FileName")!.GetSetMethod()!);

        // startInfo.Arguments = "/c " + command
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldstr, "/c ");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String])!);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("Arguments")!.GetSetMethod()!);
        il.Emit(OpCodes.Br, afterPlatformLabel);

        // Unix/Linux
        il.MarkLabel(notWindowsLabel);

        // startInfo.FileName = "/bin/sh"
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldstr, "/bin/sh");
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("FileName")!.GetSetMethod()!);

        // startInfo.Arguments = "-c \"" + command.Replace("\"", "\\\"") + "\""
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldstr, "-c \"");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "\"");
        il.Emit(OpCodes.Ldstr, "\\\"");
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.String, _types.String])!);
        il.Emit(OpCodes.Ldstr, "\"");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("Arguments")!.GetSetMethod()!);

        il.MarkLabel(afterPlatformLabel);

        // Extract cwd from options if provided (options is Dictionary<string, object?>)
        var noCwdLabel = il.DefineLabel();
        var afterCwdLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, noCwdLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, noCwdLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        // if (dict.TryGetValue("cwd", out var cwdObj) && cwdObj != null)
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "cwd");
        il.Emit(OpCodes.Ldloca, tempObjLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("TryGetValue", [_types.String, _types.Object.MakeByRefType()])!);
        il.Emit(OpCodes.Brfalse, noCwdLabel);
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Brfalse, noCwdLabel);

        // startInfo.WorkingDirectory = cwdObj.ToString()
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString")!);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("WorkingDirectory")!.GetSetMethod()!);

        il.MarkLabel(noCwdLabel);

        // Extract env from options if provided
        var noEnvLabel = il.DefineLabel();
        var envDictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var envEnumeratorLocal = il.DeclareLocal(typeof(Dictionary<string, object?>.Enumerator));
        var envKvpLocal = il.DeclareLocal(typeof(KeyValuePair<string, object?>));

        // if dict is already loaded and dict.TryGetValue("env", out envObj) && envObj is Dictionary<string,object?>
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, noEnvLabel);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "env");
        il.Emit(OpCodes.Ldloca, tempObjLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("TryGetValue", [_types.String, _types.Object.MakeByRefType()])!);
        il.Emit(OpCodes.Brfalse, noEnvLabel);
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, noEnvLabel);

        // envDict = (Dictionary<string,object?>)tempObj
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, envDictLocal);

        // startInfo.Environment.Clear() - to replace inherited env (Node.js behavior)
        var envProp = _types.ProcessStartInfo.GetProperty("Environment")!.GetGetMethod()!;
        var iDictStringString = typeof(IDictionary<string, string?>);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Callvirt, envProp);
        il.Emit(OpCodes.Callvirt, typeof(ICollection<KeyValuePair<string, string?>>).GetMethod("Clear")!);

        // foreach (var kvp in envDict) { startInfo.Environment[kvp.Key] = kvp.Value?.ToString() ?? ""; }
        il.Emit(OpCodes.Ldloc, envDictLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, envEnumeratorLocal);

        var envLoopStart = il.DefineLabel();
        var envLoopEnd = il.DefineLabel();
        il.Emit(OpCodes.Br, envLoopEnd);

        il.MarkLabel(envLoopStart);
        // kvp = enumerator.Current
        il.Emit(OpCodes.Ldloca, envEnumeratorLocal);
        il.Emit(OpCodes.Call, typeof(Dictionary<string, object?>.Enumerator).GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, envKvpLocal);

        // startInfo.Environment[kvp.Key] = kvp.Value?.ToString() ?? ""
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Callvirt, envProp);

        // key
        il.Emit(OpCodes.Ldloca, envKvpLocal);
        il.Emit(OpCodes.Call, typeof(KeyValuePair<string, object?>).GetProperty("Key")!.GetGetMethod()!);

        // value?.ToString() ?? ""
        il.Emit(OpCodes.Ldloca, envKvpLocal);
        il.Emit(OpCodes.Call, typeof(KeyValuePair<string, object?>).GetProperty("Value")!.GetGetMethod()!);
        var envValNullLabel = il.DefineLabel();
        var envValDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Stloc, tempObjLocal);
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Brfalse, envValNullLabel);
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString")!);
        il.Emit(OpCodes.Br, envValDoneLabel);
        il.MarkLabel(envValNullLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(envValDoneLabel);

        // call IDictionary<string,string?>.set_Item(key, value)
        il.Emit(OpCodes.Callvirt, iDictStringString.GetMethod("set_Item", [_types.String, _types.String])!);

        il.MarkLabel(envLoopEnd);
        // if (enumerator.MoveNext()) goto loopStart
        il.Emit(OpCodes.Ldloca, envEnumeratorLocal);
        il.Emit(OpCodes.Call, typeof(Dictionary<string, object?>.Enumerator).GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brtrue, envLoopStart);

        il.MarkLabel(noEnvLabel);

        // using var process = new Process { StartInfo = startInfo };
        // We'll handle the using/try-finally pattern manually
        var afterTryLabel = il.DefineLabel();
        var returnStdoutLabel = il.DefineLabel();

        il.BeginExceptionBlock();

        il.Emit(OpCodes.Newobj, _types.Process.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, processLocal);

        // process.StartInfo = startInfo
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("StartInfo")!.GetSetMethod()!);

        // process.Start()
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetMethod("Start", Type.EmptyTypes)!);
        il.Emit(OpCodes.Pop);

        // stdout = process.StandardOutput.ReadToEnd()
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("StandardOutput")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.TextReader.GetMethod("ReadToEnd")!);
        il.Emit(OpCodes.Stloc, stdoutLocal);

        // stderr = process.StandardError.ReadToEnd()
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("StandardError")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.TextReader.GetMethod("ReadToEnd")!);
        il.Emit(OpCodes.Stloc, stderrLocal);

        // process.WaitForExit()
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetMethod("WaitForExit", Type.EmptyTypes)!);

        // exitCode = process.ExitCode
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("ExitCode")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, exitCodeLocal);

        il.Emit(OpCodes.Leave, afterTryLabel);

        // finally { process?.Dispose() }
        il.BeginFinallyBlock();
        var skipDisposeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Brfalse, skipDisposeLabel);
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.IDisposable.GetMethod("Dispose")!);
        il.MarkLabel(skipDisposeLabel);
        il.Emit(OpCodes.Endfinally);

        il.EndExceptionBlock();

        il.MarkLabel(afterTryLabel);

        // if (exitCode != 0) throw new Exception(...)
        var noErrorLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, exitCodeLocal);
        il.Emit(OpCodes.Brfalse, noErrorLabel);

        // throw new Exception("Command failed with exit code " + exitCode + ": " + stderr)
        il.Emit(OpCodes.Ldstr, "Command failed with exit code ");
        il.Emit(OpCodes.Ldloca, exitCodeLocal);
        il.Emit(OpCodes.Call, _types.Int32.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldstr, ": ");
        il.Emit(OpCodes.Ldloc, stderrLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Newobj, _types.Exception.GetConstructor([_types.String])!);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(noErrorLabel);
        il.Emit(OpCodes.Ldloc, stdoutLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object ChildProcessSpawnSync(string command, object args, object options)
    /// Spawns a process synchronously and returns result object.
    /// </summary>
    private void EmitChildProcessSpawnSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ChildProcessSpawnSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.Object, _types.Object]);
        runtime.ChildProcessSpawnSync = method;
        runtime.RegisterBuiltInModuleMethod("child_process", "spawnSync", method);

        var il = method.GetILGenerator();

        var startInfoLocal = il.DeclareLocal(_types.ProcessStartInfo);
        var processLocal = il.DeclareLocal(_types.Process);
        var stdoutLocal = il.DeclareLocal(_types.String);
        var stderrLocal = il.DeclareLocal(_types.String);
        var exitCodeLocal = il.DeclareLocal(_types.Int32);
        var argsListLocal = il.DeclareLocal(_types.ListOfObject);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var resultLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var tempObjLocal = il.DeclareLocal(_types.Object);
        var iLocal = il.DeclareLocal(_types.Int32);
        var argListLocal = il.DeclareLocal(typeof(System.Collections.ObjectModel.Collection<string>));
        var errorMsgLocal = il.DeclareLocal(_types.String);

        // Initialize stdout, stderr, exitCode
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, stdoutLocal);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, stderrLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, exitCodeLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, errorMsgLocal);

        // var startInfo = new ProcessStartInfo(command)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, _types.ProcessStartInfo.GetConstructor([_types.String])!);
        il.Emit(OpCodes.Stloc, startInfoLocal);

        // startInfo.UseShellExecute = false
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("UseShellExecute")!.GetSetMethod()!);

        // startInfo.RedirectStandardOutput = true
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardOutput")!.GetSetMethod()!);

        // startInfo.RedirectStandardError = true
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardError")!.GetSetMethod()!);

        // startInfo.CreateNoWindow = true
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("CreateNoWindow")!.GetSetMethod()!);

        // Extract args if provided (args is List<object?>)
        var noArgsLabel = il.DefineLabel();
        var afterArgsLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, noArgsLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brfalse, noArgsLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Stloc, argsListLocal);

        // Get ArgumentList
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("ArgumentList")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, argListLocal);

        // for (int i = 0; i < argsList.Count; i++) { argumentList.Add(argsList[i]?.ToString() ?? ""); }
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        var argsLoopStart = il.DefineLabel();
        var argsLoopEnd = il.DefineLabel();

        il.MarkLabel(argsLoopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, argsListLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, argsLoopEnd);

        // var arg = argsList[i]
        il.Emit(OpCodes.Ldloc, argsListLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Stloc, tempObjLocal);

        // argumentList.Add(arg?.ToString() ?? "")
        il.Emit(OpCodes.Ldloc, argListLocal);
        var argNullLabel = il.DefineLabel();
        var argAddLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Brfalse, argNullLabel);
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString")!);
        il.Emit(OpCodes.Br, argAddLabel);
        il.MarkLabel(argNullLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(argAddLabel);
        il.Emit(OpCodes.Callvirt, typeof(System.Collections.ObjectModel.Collection<string>).GetMethod("Add", [_types.String])!);

        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, argsLoopStart);

        il.MarkLabel(argsLoopEnd);
        il.MarkLabel(noArgsLabel);

        // Extract cwd from options if provided
        var noCwdLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brfalse, noCwdLabel);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, noCwdLabel);

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "cwd");
        il.Emit(OpCodes.Ldloca, tempObjLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("TryGetValue", [_types.String, _types.Object.MakeByRefType()])!);
        il.Emit(OpCodes.Brfalse, noCwdLabel);
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Brfalse, noCwdLabel);

        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString")!);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("WorkingDirectory")!.GetSetMethod()!);

        il.MarkLabel(noCwdLabel);

        // try { run process } catch (Exception ex) { errorMsg = ex.Message; exitCode = -1; }
        var afterProcessLabel = il.DefineLabel();

        il.BeginExceptionBlock();

        il.Emit(OpCodes.Newobj, _types.Process.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, processLocal);

        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("StartInfo")!.GetSetMethod()!);

        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetMethod("Start", Type.EmptyTypes)!);
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("StandardOutput")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.TextReader.GetMethod("ReadToEnd")!);
        il.Emit(OpCodes.Stloc, stdoutLocal);

        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("StandardError")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.TextReader.GetMethod("ReadToEnd")!);
        il.Emit(OpCodes.Stloc, stderrLocal);

        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetMethod("WaitForExit", Type.EmptyTypes)!);

        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("ExitCode")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, exitCodeLocal);

        // Dispose process
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.IDisposable.GetMethod("Dispose")!);

        il.Emit(OpCodes.Leave, afterProcessLabel);

        // catch (Exception ex) { errorMsg = ex.Message; exitCode = -1; }
        il.BeginCatchBlock(_types.Exception);
        il.Emit(OpCodes.Callvirt, _types.Exception.GetProperty("Message")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, errorMsgLocal);
        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Stloc, exitCodeLocal);
        il.Emit(OpCodes.Leave, afterProcessLabel);

        il.EndExceptionBlock();

        il.MarkLabel(afterProcessLabel);

        // Create result dictionary
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObject.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        // result["stdout"] = stdout
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "stdout");
        il.Emit(OpCodes.Ldloc, stdoutLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object)!);

        // result["stderr"] = stderr
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "stderr");
        il.Emit(OpCodes.Ldloc, stderrLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object)!);

        // result["status"] = (double)exitCode
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "status");
        il.Emit(OpCodes.Ldloc, exitCodeLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object)!);

        // result["signal"] = null
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "signal");
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object)!);

        // if (errorMsg != null) result["error"] = errorMsg
        var noErrorMsgLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, errorMsgLocal);
        il.Emit(OpCodes.Brfalse, noErrorMsgLabel);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "error");
        il.Emit(OpCodes.Ldloc, errorMsgLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object)!);

        il.MarkLabel(noErrorMsgLabel);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    // Field for the no-op child process method
    private MethodBuilder _childProcessNoOp = null!;

    /// <summary>
    /// Emits a static no-op method that returns null. Used for kill/send/disconnect stubs.
    /// </summary>
    private void EmitChildProcessNoOp(TypeBuilder typeBuilder)
    {
        _childProcessNoOp = typeBuilder.DefineMethod(
            "ChildProcessNoOp",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]);
        var il = _childProcessNoOp.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits IL that creates a Process, starts it, and builds a ChildProcess-like $EventEmitter
    /// with pid, kill, send, disconnect, killed, connected, stdout, stderr, stdin properties.
    /// Pure IL — no reflection to SharpTS.dll.
    /// </summary>
    private void EmitBuildChildProcessObject(ILGenerator il, EmittedRuntime runtime, bool includeStdio)
    {
        // Expects Process on stack. Stores it and builds the ChildProcess object.
        var processLocal = il.DeclareLocal(_types.Process);
        il.Emit(OpCodes.Stloc, processLocal);

        // Start the process (instance method, no params)
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetMethod("Start", Type.EmptyTypes)!);
        il.Emit(OpCodes.Pop); // Start returns bool

        // Create $EventEmitter as the base ChildProcess object
        il.Emit(OpCodes.Newobj, runtime.TSEventEmitterCtor);
        var emitterLocal = il.DeclareLocal(runtime.TSEventEmitterType);
        il.Emit(OpCodes.Stloc, emitterLocal);

        // Build a dict with all properties, then wrap as $Object
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        // pid = (double)process.Id
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "pid");
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("Id")!.GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object));

        // killed = false
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "killed");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object));

        // connected = false
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "connected");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object));

        // Helper: add a TSFunction wrapping the no-op method
        void AddNoOpMethod(string name)
        {
            il.Emit(OpCodes.Ldloc, dictLocal);
            il.Emit(OpCodes.Ldstr, name);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldtoken, _childProcessNoOp);
            il.Emit(OpCodes.Call, typeof(System.Reflection.MethodBase).GetMethod("GetMethodFromHandle", [typeof(RuntimeMethodHandle)])!);
            il.Emit(OpCodes.Castclass, typeof(System.Reflection.MethodInfo));
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object));
        }

        // on = delegate to emitter's On method
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "on");
        // Create TSFunction wrapping the emitter's On — use the emitter instance as target
        il.Emit(OpCodes.Ldloc, emitterLocal);
        il.Emit(OpCodes.Ldtoken, runtime.TSEventEmitterOn);
        il.Emit(OpCodes.Call, typeof(System.Reflection.MethodBase).GetMethod("GetMethodFromHandle", [typeof(RuntimeMethodHandle)])!);
        il.Emit(OpCodes.Castclass, typeof(System.Reflection.MethodInfo));
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object));

        // kill, send, disconnect = no-op TSFunctions
        AddNoOpMethod("kill");
        AddNoOpMethod("send");
        AddNoOpMethod("disconnect");

        if (includeStdio)
        {
            // stdout, stderr, stdin = non-null placeholder objects
            il.Emit(OpCodes.Ldloc, dictLocal);
            il.Emit(OpCodes.Ldstr, "stdout");
            il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
            il.Emit(OpCodes.Call, runtime.CreateObject);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object));

            il.Emit(OpCodes.Ldloc, dictLocal);
            il.Emit(OpCodes.Ldstr, "stderr");
            il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
            il.Emit(OpCodes.Call, runtime.CreateObject);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object));

            il.Emit(OpCodes.Ldloc, dictLocal);
            il.Emit(OpCodes.Ldstr, "stdin");
            il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
            il.Emit(OpCodes.Call, runtime.CreateObject);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object));
        }

        // Wrap dict in $Object
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Call, runtime.CreateObject);
    }

    /// <summary>
    /// Emits IL to create a ProcessStartInfo configured for exec (shell execution).
    /// Leaves the Process (not yet started) on the stack.
    /// </summary>
    private void EmitCreateExecProcess(ILGenerator il, LocalBuilder commandArg)
    {
        var startInfoLocal = il.DeclareLocal(_types.ProcessStartInfo);

        il.Emit(OpCodes.Newobj, _types.ProcessStartInfo.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, startInfoLocal);

        // UseShellExecute = false, RedirectStd* = true, CreateNoWindow = true
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("UseShellExecute")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardOutput")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardError")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardInput")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("CreateNoWindow")!.GetSetMethod()!);

        // Platform check
        var notWindowsLabel = il.DefineLabel();
        var afterPlatformLabel = il.DefineLabel();

        il.Emit(OpCodes.Call, _types.OSPlatform.GetProperty("Windows")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, _types.RuntimeInformation.GetMethod("IsOSPlatform", [_types.OSPlatform])!);
        il.Emit(OpCodes.Brfalse, notWindowsLabel);

        // Windows: cmd.exe /c command
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldstr, "cmd.exe");
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("FileName")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldstr, "/c ");
        il.Emit(OpCodes.Ldloc, commandArg);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String])!);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("Arguments")!.GetSetMethod()!);
        il.Emit(OpCodes.Br, afterPlatformLabel);

        // Unix: /bin/sh -c "command"
        il.MarkLabel(notWindowsLabel);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldstr, "/bin/sh");
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("FileName")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldstr, "-c \"");
        il.Emit(OpCodes.Ldloc, commandArg);
        il.Emit(OpCodes.Ldstr, "\"");
        il.Emit(OpCodes.Ldstr, "\\\"");
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.String, _types.String])!);
        il.Emit(OpCodes.Ldstr, "\"");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("Arguments")!.GetSetMethod()!);

        il.MarkLabel(afterPlatformLabel);

        // new Process { StartInfo = startInfo }
        var processLocal = il.DeclareLocal(_types.Process);
        il.Emit(OpCodes.Newobj, _types.Process.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, processLocal);
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("StartInfo")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, processLocal); // leave Process on stack
    }

    /// <summary>
    /// Emits: public static object ChildProcessExec(string command, object optionsOrCallback, object callback)
    /// Pure IL — creates Process, starts it, returns ChildProcess-like object.
    /// </summary>
    private void EmitChildProcessExec(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ChildProcessExec",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.Object, _types.Object]);
        runtime.ChildProcessExec = method;
        runtime.RegisterBuiltInModuleMethod("child_process", "exec", method);

        var il = method.GetILGenerator();
        // Store command arg in a local for EmitCreateExecProcess
        var cmdLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc, cmdLocal);

        EmitCreateExecProcess(il, cmdLocal); // leaves Process on stack
        EmitBuildChildProcessObject(il, runtime, includeStdio: false);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object ChildProcessSpawn(string command, object args, object options)
    /// Pure IL — creates Process with direct command + args, returns ChildProcess with stdio.
    /// </summary>
    private void EmitChildProcessSpawn(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ChildProcessSpawn",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.Object, _types.Object]);
        runtime.ChildProcessSpawn = method;
        runtime.RegisterBuiltInModuleMethod("child_process", "spawn", method);

        var il = method.GetILGenerator();

        var startInfoLocal = il.DeclareLocal(_types.ProcessStartInfo);

        // startInfo = new ProcessStartInfo(command)
        il.Emit(OpCodes.Ldarg_0); // command = FileName
        il.Emit(OpCodes.Newobj, _types.ProcessStartInfo.GetConstructor([_types.String])!);
        il.Emit(OpCodes.Stloc, startInfoLocal);

        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("UseShellExecute")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardOutput")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardError")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardInput")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("CreateNoWindow")!.GetSetMethod()!);

        // Build args string from args list (arg1)
        // If args is List<object?>, join with spaces
        var noArgsLabel = il.DefineLabel();
        var afterArgsLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brfalse, noArgsLabel);

        // Build argument string: string.Join(" ", args.Select(a => a.ToString()))
        il.Emit(OpCodes.Ldstr, " ");
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("ToArray")!);
        il.Emit(OpCodes.Call, typeof(string).GetMethod("Join", [typeof(string), typeof(object[])])!);
        var argsStringLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Stloc, argsStringLocal);

        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldloc, argsStringLocal);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("Arguments")!.GetSetMethod()!);

        il.MarkLabel(noArgsLabel);

        // new Process { StartInfo = startInfo }
        var processLocal = il.DeclareLocal(_types.Process);
        il.Emit(OpCodes.Newobj, _types.Process.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, processLocal);
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("StartInfo")!.GetSetMethod()!);

        il.Emit(OpCodes.Ldloc, processLocal); // leave on stack
        EmitBuildChildProcessObject(il, runtime, includeStdio: true);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static string ChildProcessExecFileSync(string file, object args, object options)
    /// Executes a file synchronously without a shell and returns stdout.
    /// Uses same pattern as SpawnSync but throws on non-zero exit code.
    /// </summary>
    private void EmitChildProcessExecFileSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ChildProcessExecFileSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String, _types.Object, _types.Object]);
        runtime.ChildProcessExecFileSync = method;
        runtime.RegisterBuiltInModuleMethod("child_process", "execFileSync", method);

        var il = method.GetILGenerator();

        var startInfoLocal = il.DeclareLocal(_types.ProcessStartInfo);
        var processLocal = il.DeclareLocal(_types.Process);
        var stdoutLocal = il.DeclareLocal(_types.String);
        var stderrLocal = il.DeclareLocal(_types.String);
        var exitCodeLocal = il.DeclareLocal(_types.Int32);
        var argsListLocal = il.DeclareLocal(_types.ListOfObject);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var tempObjLocal = il.DeclareLocal(_types.Object);
        var iLocal = il.DeclareLocal(_types.Int32);
        var argListLocal = il.DeclareLocal(typeof(System.Collections.ObjectModel.Collection<string>));

        // Initialize
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, stdoutLocal);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, stderrLocal);

        // var startInfo = new ProcessStartInfo(file)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, _types.ProcessStartInfo.GetConstructor([_types.String])!);
        il.Emit(OpCodes.Stloc, startInfoLocal);

        // startInfo.UseShellExecute = false
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("UseShellExecute")!.GetSetMethod()!);

        // startInfo.RedirectStandardOutput = true
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardOutput")!.GetSetMethod()!);

        // startInfo.RedirectStandardError = true
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardError")!.GetSetMethod()!);

        // startInfo.CreateNoWindow = true
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("CreateNoWindow")!.GetSetMethod()!);

        // Extract args if provided (args is List<object?>)
        var noArgsLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, noArgsLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brfalse, noArgsLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Stloc, argsListLocal);

        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("ArgumentList")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, argListLocal);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        var argsLoopStart = il.DefineLabel();
        var argsLoopEnd = il.DefineLabel();

        il.MarkLabel(argsLoopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, argsListLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, argsLoopEnd);

        il.Emit(OpCodes.Ldloc, argsListLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Stloc, tempObjLocal);

        il.Emit(OpCodes.Ldloc, argListLocal);
        var argNullLabel = il.DefineLabel();
        var argAddLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Brfalse, argNullLabel);
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString")!);
        il.Emit(OpCodes.Br, argAddLabel);
        il.MarkLabel(argNullLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(argAddLabel);
        il.Emit(OpCodes.Callvirt, typeof(System.Collections.ObjectModel.Collection<string>).GetMethod("Add", [_types.String])!);

        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, argsLoopStart);

        il.MarkLabel(argsLoopEnd);
        il.MarkLabel(noArgsLabel);

        // Extract cwd from options
        var noCwdLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Brfalse, noCwdLabel);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, noCwdLabel);

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "cwd");
        il.Emit(OpCodes.Ldloca, tempObjLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("TryGetValue", [_types.String, _types.Object.MakeByRefType()])!);
        il.Emit(OpCodes.Brfalse, noCwdLabel);
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Brfalse, noCwdLabel);

        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldloc, tempObjLocal);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString")!);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("WorkingDirectory")!.GetSetMethod()!);

        il.MarkLabel(noCwdLabel);

        // try { run process } finally { dispose }
        var afterTryLabel = il.DefineLabel();

        il.BeginExceptionBlock();

        il.Emit(OpCodes.Newobj, _types.Process.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, processLocal);

        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("StartInfo")!.GetSetMethod()!);

        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetMethod("Start", Type.EmptyTypes)!);
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("StandardOutput")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.TextReader.GetMethod("ReadToEnd")!);
        il.Emit(OpCodes.Stloc, stdoutLocal);

        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("StandardError")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.TextReader.GetMethod("ReadToEnd")!);
        il.Emit(OpCodes.Stloc, stderrLocal);

        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetMethod("WaitForExit", Type.EmptyTypes)!);

        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("ExitCode")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, exitCodeLocal);

        il.Emit(OpCodes.Leave, afterTryLabel);

        // finally { process?.Dispose() }
        il.BeginFinallyBlock();
        var skipDisposeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Brfalse, skipDisposeLabel);
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Callvirt, _types.IDisposable.GetMethod("Dispose")!);
        il.MarkLabel(skipDisposeLabel);
        il.Emit(OpCodes.Endfinally);

        il.EndExceptionBlock();

        il.MarkLabel(afterTryLabel);

        // if (exitCode != 0) throw
        var noErrorLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, exitCodeLocal);
        il.Emit(OpCodes.Brfalse, noErrorLabel);

        il.Emit(OpCodes.Ldstr, "Command failed with exit code ");
        il.Emit(OpCodes.Ldloca, exitCodeLocal);
        il.Emit(OpCodes.Call, _types.Int32.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldstr, ": ");
        il.Emit(OpCodes.Ldloc, stderrLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Newobj, _types.Exception.GetConstructor([_types.String])!);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(noErrorLabel);
        il.Emit(OpCodes.Ldloc, stdoutLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object ChildProcessExecFile(string file, object args, object options, object callback)
    /// Pure IL — creates Process with direct file + args, returns ChildProcess-like object.
    /// </summary>
    private void EmitChildProcessExecFile(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ChildProcessExecFile",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.Object, _types.Object, _types.Object]);
        runtime.ChildProcessExecFile = method;
        runtime.RegisterBuiltInModuleMethod("child_process", "execFile", method);

        var il = method.GetILGenerator();

        // Similar to spawn: ProcessStartInfo(file) + args
        var startInfoLocal = il.DeclareLocal(_types.ProcessStartInfo);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, _types.ProcessStartInfo.GetConstructor([_types.String])!);
        il.Emit(OpCodes.Stloc, startInfoLocal);

        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("UseShellExecute")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardOutput")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardError")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("RedirectStandardInput")!.GetSetMethod()!);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("CreateNoWindow")!.GetSetMethod()!);

        // Set args from arg1 if it's a List
        var noArgsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Brfalse, noArgsLabel);

        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Ldstr, " ");
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.ListOfObject);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("ToArray")!);
        il.Emit(OpCodes.Call, typeof(string).GetMethod("Join", [typeof(string), typeof(object[])])!);
        il.Emit(OpCodes.Callvirt, _types.ProcessStartInfo.GetProperty("Arguments")!.GetSetMethod()!);

        il.MarkLabel(noArgsLabel);

        var processLocal = il.DeclareLocal(_types.Process);
        il.Emit(OpCodes.Newobj, _types.Process.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, processLocal);
        il.Emit(OpCodes.Ldloc, processLocal);
        il.Emit(OpCodes.Ldloc, startInfoLocal);
        il.Emit(OpCodes.Callvirt, _types.Process.GetProperty("StartInfo")!.GetSetMethod()!);

        il.Emit(OpCodes.Ldloc, processLocal);
        EmitBuildChildProcessObject(il, runtime, includeStdio: false);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object ChildProcessFork(string modulePath, object args, object options)
    /// Pure IL — returns a ChildProcess-like object (fork is a simplified spawn).
    /// </summary>
    private void EmitChildProcessFork(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ChildProcessFork",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.Object, _types.Object]);
        runtime.ChildProcessFork = method;
        runtime.RegisterBuiltInModuleMethod("child_process", "fork", method);

        var il = method.GetILGenerator();
        // Fork needs to run a TS file in a child process. For now, return a minimal ChildProcess object.
        // Create a dummy process (current process) just to have a pid
        il.Emit(OpCodes.Call, _types.Process.GetMethod("GetCurrentProcess")!);
        EmitBuildChildProcessObject(il, runtime, includeStdio: false);
        il.Emit(OpCodes.Ret);
    }
}
