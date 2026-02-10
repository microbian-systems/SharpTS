using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits: public static byte[] SignDataBytes(string privateKeyPem, byte[] data, HashAlgorithmName hashAlgorithm)
    /// Signs data using RSA or EC private key. Uses try/catch to detect key type.
    /// </summary>
    private void EmitSignDataBytes(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SignDataBytes",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ByteArray,
            [_types.String, _types.ByteArray, _types.HashAlgorithmName]);
        runtime.SignDataBytes = method;

        var il = method.GetILGenerator();

        // Result local used for both EC and RSA paths
        var resultLocal = il.DeclareLocal(_types.ByteArray);
        var exitLabel = il.DefineLabel();
        var rsaSignLabel = il.DefineLabel();

        // Check for explicit RSA header
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "RSA PRIVATE KEY");
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Contains", [_types.String])!);
        il.Emit(OpCodes.Brtrue, rsaSignLabel);

        // Generic format or EC - try EC first with try/catch
        var ecdsaLocal = il.DeclareLocal(typeof(ECDsa));

        // try { ECDsa sign }
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Call, typeof(ECDsa).GetMethod("Create", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, ecdsaLocal);
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(MemoryExtensions).GetMethod("AsSpan", [typeof(string)])!);
        il.Emit(OpCodes.Callvirt, typeof(ECDsa).GetMethod("ImportFromPem", [typeof(ReadOnlySpan<char>)])!);
        // result = ecdsa.SignData(data, hashAlgorithm)
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, typeof(ECDsa).GetMethod("SignData", [typeof(byte[]), typeof(HashAlgorithmName)])!);
        il.Emit(OpCodes.Stloc, resultLocal);
        // Dispose ecdsa
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        il.Emit(OpCodes.Callvirt, typeof(IDisposable).GetMethod("Dispose")!);
        il.Emit(OpCodes.Leave, exitLabel);

        // catch (CryptographicException) { fall back to RSA }
        il.BeginCatchBlock(typeof(CryptographicException));
        il.Emit(OpCodes.Pop);
        // Dispose the failed ECDsa if it was created
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        var ecdsaNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, ecdsaNullLabel);
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        il.Emit(OpCodes.Callvirt, typeof(IDisposable).GetMethod("Dispose")!);
        il.MarkLabel(ecdsaNullLabel);
        il.Emit(OpCodes.Leave, rsaSignLabel);
        il.EndExceptionBlock();

        // RSA signing path
        il.MarkLabel(rsaSignLabel);
        var rsaLocal = il.DeclareLocal(typeof(RSA));
        il.Emit(OpCodes.Call, typeof(RSA).GetMethod("Create", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, rsaLocal);
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(MemoryExtensions).GetMethod("AsSpan", [typeof(string)])!);
        il.Emit(OpCodes.Callvirt, typeof(RSA).GetMethod("ImportFromPem", [typeof(ReadOnlySpan<char>)])!);
        // result = rsa.SignData(data, hashAlgorithm, RSASignaturePadding.Pkcs1)
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, typeof(RSASignaturePadding).GetProperty("Pkcs1")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, typeof(RSA).GetMethod("SignData", [typeof(byte[]), typeof(HashAlgorithmName), typeof(RSASignaturePadding)])!);
        il.Emit(OpCodes.Stloc, resultLocal);
        // Dispose rsa
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Callvirt, typeof(IDisposable).GetMethod("Dispose")!);

        // Exit: return result
        il.MarkLabel(exitLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static bool VerifyDataBytes(string publicKeyPem, byte[] data, HashAlgorithmName hashAlgorithm, byte[] signature)
    /// Verifies a signature using RSA or EC public key. Uses try/catch to detect key type.
    /// </summary>
    private void EmitVerifyDataBytes(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "VerifyDataBytes",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.String, _types.ByteArray, _types.HashAlgorithmName, _types.ByteArray]);
        runtime.VerifyDataBytes = method;

        var il = method.GetILGenerator();

        // Result local used for both EC and RSA paths
        var resultLocal = il.DeclareLocal(_types.Boolean);
        var exitLabel = il.DefineLabel();
        var rsaVerifyLabel = il.DefineLabel();

        // Check for explicit RSA header
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "RSA PUBLIC KEY");
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Contains", [_types.String])!);
        il.Emit(OpCodes.Brtrue, rsaVerifyLabel);

        // Generic format or EC - try EC first with try/catch
        var ecdsaLocal = il.DeclareLocal(typeof(ECDsa));

        // try { ECDsa verify }
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Call, typeof(ECDsa).GetMethod("Create", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, ecdsaLocal);
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(MemoryExtensions).GetMethod("AsSpan", [typeof(string)])!);
        il.Emit(OpCodes.Callvirt, typeof(ECDsa).GetMethod("ImportFromPem", [typeof(ReadOnlySpan<char>)])!);
        // result = ecdsa.VerifyData(data, signature, hashAlgorithm)
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        il.Emit(OpCodes.Ldarg_1);  // data
        il.Emit(OpCodes.Ldarg_3);  // signature
        il.Emit(OpCodes.Ldarg_2);  // hashAlgorithm
        il.Emit(OpCodes.Callvirt, typeof(ECDsa).GetMethod("VerifyData", [typeof(byte[]), typeof(byte[]), typeof(HashAlgorithmName)])!);
        il.Emit(OpCodes.Stloc, resultLocal);
        // Dispose ecdsa
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        il.Emit(OpCodes.Callvirt, typeof(IDisposable).GetMethod("Dispose")!);
        il.Emit(OpCodes.Leave, exitLabel);

        // catch (CryptographicException) { fall back to RSA }
        il.BeginCatchBlock(typeof(CryptographicException));
        il.Emit(OpCodes.Pop);
        // Dispose the failed ECDsa if it was created
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        var ecdsaNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, ecdsaNullLabel);
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        il.Emit(OpCodes.Callvirt, typeof(IDisposable).GetMethod("Dispose")!);
        il.MarkLabel(ecdsaNullLabel);
        il.Emit(OpCodes.Leave, rsaVerifyLabel);
        il.EndExceptionBlock();

        // RSA verification path
        il.MarkLabel(rsaVerifyLabel);
        var rsaLocal = il.DeclareLocal(typeof(RSA));
        il.Emit(OpCodes.Call, typeof(RSA).GetMethod("Create", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, rsaLocal);
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(MemoryExtensions).GetMethod("AsSpan", [typeof(string)])!);
        il.Emit(OpCodes.Callvirt, typeof(RSA).GetMethod("ImportFromPem", [typeof(ReadOnlySpan<char>)])!);
        // result = rsa.VerifyData(data, signature, hashAlgorithm, RSASignaturePadding.Pkcs1)
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Ldarg_1);  // data
        il.Emit(OpCodes.Ldarg_3);  // signature
        il.Emit(OpCodes.Ldarg_2);  // hashAlgorithm
        il.Emit(OpCodes.Call, typeof(RSASignaturePadding).GetProperty("Pkcs1")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, typeof(RSA).GetMethod("VerifyData", [typeof(byte[]), typeof(byte[]), typeof(HashAlgorithmName), typeof(RSASignaturePadding)])!);
        il.Emit(OpCodes.Stloc, resultLocal);
        // Dispose rsa
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Callvirt, typeof(IDisposable).GetMethod("Dispose")!);

        // Exit: return result
        il.MarkLabel(exitLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }
}
