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

    /// <summary>
    /// Emits: public static object ChildProcessExec(string command, object optionsOrCallback, object callback)
    /// Delegates to ChildProcessModuleInterpreter.GetExports()["exec"] via reflection.
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
        EmitChildProcessReflectionCall(il, "exec", 3);
    }

    /// <summary>
    /// Emits: public static object ChildProcessSpawn(string command, object args, object options)
    /// Delegates to ChildProcessModuleInterpreter.GetExports()["spawn"] via reflection.
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
        EmitChildProcessReflectionCall(il, "spawn", 3);
    }

    /// <summary>
    /// Emits IL that calls ChildProcessModuleInterpreter.GetExports()[methodName] via reflection.
    /// The method loads the type, calls GetExports(), gets the BuiltInMethod, and invokes it with args.
    /// </summary>
    private void EmitChildProcessReflectionCall(ILGenerator il, string methodName, int argCount)
    {
        // Type moduleType = Type.GetType("SharpTS.Runtime.BuiltIns.Modules.Interpreter.ChildProcessModuleInterpreter, SharpTS");
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.BuiltIns.Modules.Interpreter.ChildProcessModuleInterpreter, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        var typeLocal = il.DeclareLocal(_types.Type);
        il.Emit(OpCodes.Stloc, typeLocal);

        // If null, return null
        var typeOk = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Brtrue, typeOk);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(typeOk);

        // MethodInfo getExports = moduleType.GetMethod("GetExports");
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "GetExports");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        // object exports = getExports.Invoke(null, Array.Empty<object>());
        il.Emit(OpCodes.Ldnull); // static method
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        // Cast to Dictionary<string, object?>
        var exportsLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, exportsLocal);

        // Get the method by name: dict[methodName]
        // Use reflection: exports.GetType().GetProperty("Item").GetValue(exports, new object[] { methodName })
        il.Emit(OpCodes.Ldloc, exportsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "Item");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetProperty", _types.String));
        il.Emit(OpCodes.Ldloc, exportsLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldstr, methodName);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.PropertyInfo, "GetValue", _types.Object, _types.ObjectArray));
        var builtInLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, builtInLocal);

        // Build args list: new List<object?> { arg0, arg1, arg2 }
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        var argsLocal = il.DeclareLocal(_types.ListOfObjectNullable);
        il.Emit(OpCodes.Stloc, argsLocal);

        for (int i = 0; i < argCount; i++)
        {
            il.Emit(OpCodes.Ldloc, argsLocal);
            il.Emit(OpCodes.Ldarg, i);
            if (i == 0) // string command - box to object
            {
                // already object via Ldarg
            }
            il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObjectNullable, "Add", _types.Object));
        }

        // Call builtIn.GetType().GetMethod("Call").Invoke(builtIn, new object[] { null, argsList })
        il.Emit(OpCodes.Ldloc, builtInLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "Call");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String));
        il.Emit(OpCodes.Ldloc, builtInLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldnull); // interpreter = null
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
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
    /// Delegates to ChildProcessModuleInterpreter.GetExports()["execFile"] via reflection.
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
        EmitChildProcessReflectionCall(il, "execFile", 4);
    }

    /// <summary>
    /// Emits: public static object ChildProcessFork(string modulePath, object args, object options)
    /// Delegates to ChildProcessModuleInterpreter.GetExports()["fork"] via reflection.
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
        EmitChildProcessReflectionCall(il, "fork", 3);
    }
}
