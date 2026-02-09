using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits crypto module helper methods.
    /// </summary>
    private void EmitCryptoMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Scrypt helpers (must be emitted first - scrypt methods are used by EmitCryptoScryptSync)
        EmitScryptMethods(typeBuilder, runtime);

        EmitCryptoCreateHash(typeBuilder, runtime);
        EmitCryptoCreateHmac(typeBuilder, runtime);
        EmitCryptoCreateCipheriv(typeBuilder, runtime);
        EmitCryptoCreateDecipheriv(typeBuilder, runtime);
        EmitCryptoRandomBytes(typeBuilder, runtime);
        EmitCryptoRandomFillSync(typeBuilder, runtime);
        EmitCryptoPbkdf2Sync(typeBuilder, runtime);
        EmitCryptoScryptSync(typeBuilder, runtime);
        EmitCryptoTimingSafeEqual(typeBuilder, runtime);
        EmitCryptoCreateSign(typeBuilder, runtime);
        EmitCryptoCreateVerify(typeBuilder, runtime);
        EmitCryptoGetHashes(typeBuilder, runtime);
        EmitCryptoGetCiphers(typeBuilder, runtime);

        // Standalone helpers (must be emitted before methods that use them)
        // RSA helpers
        EmitExtractKeyPem(typeBuilder, runtime);
        EmitRsaEncryptRaw(typeBuilder, runtime);
        EmitRsaDecryptRaw(typeBuilder, runtime);
        // Key pair generation helpers
        EmitGetOptionInt(typeBuilder, runtime);
        EmitGetOptionString(typeBuilder, runtime);
        EmitGenerateRsaKeyPairRaw(typeBuilder, runtime);
        EmitGenerateEcKeyPairRaw(typeBuilder, runtime);

        // Methods that use the standalone helpers
        EmitCryptoGenerateKeyPairSync(typeBuilder, runtime);
        EmitCryptoCreateDiffieHellman(typeBuilder, runtime);
        EmitCryptoGetDiffieHellman(typeBuilder, runtime);
        EmitCryptoCreateECDH(typeBuilder, runtime);

        // RSA encryption/decryption (uses ExtractKeyPem, RsaEncryptRaw, RsaDecryptRaw)
        EmitCryptoPublicEncrypt(typeBuilder, runtime);
        EmitCryptoPrivateDecrypt(typeBuilder, runtime);
        EmitCryptoPrivateEncrypt(typeBuilder, runtime);
        EmitCryptoPublicDecrypt(typeBuilder, runtime);

        // HKDF key derivation
        EmitCryptoHkdfSync(typeBuilder, runtime);

        // Sign/Verify helpers (standalone)
        EmitSignDataBytes(typeBuilder, runtime);
        EmitVerifyDataBytes(typeBuilder, runtime);

        // ECDH helpers (standalone)
        EmitTSECDHHelpers(typeBuilder, runtime);

        // KeyObject API
        EmitCryptoCreateSecretKey(typeBuilder, runtime);
        EmitCryptoCreatePublicKey(typeBuilder, runtime);
        EmitCryptoCreatePrivateKey(typeBuilder, runtime);

        // Emit wrapper methods for named imports
        EmitCryptoMethodWrappers(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits wrapper methods for crypto module functions to support named imports.
    /// Each wrapper takes individual object parameters (compatible with TSFunction.Invoke).
    /// </summary>
    private void EmitCryptoMethodWrappers(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // createHash(algorithm) -> $Hash
        EmitCryptoMethodWrapper(typeBuilder, runtime, "createHash", 1, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToString(il);
            il.Emit(OpCodes.Call, runtime.CryptoCreateHash);
        });

        // createHmac(algorithm, key) -> $Hmac
        EmitCryptoMethodWrapper(typeBuilder, runtime, "createHmac", 2, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToString(il);
            il.Emit(OpCodes.Ldarg_1);
            EmitObjectToKeyBytes(il);
            il.Emit(OpCodes.Call, runtime.CryptoCreateHmac);
        });

        // randomBytes(size) -> $Array
        EmitCryptoMethodWrapper(typeBuilder, runtime, "randomBytes", 1, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToInt32(il);
            il.Emit(OpCodes.Call, runtime.CryptoRandomBytes);
        });

        // randomUUID() -> string
        EmitCryptoMethodWrapper(typeBuilder, runtime, "randomUUID", 0, il =>
        {
            var guidLocal = il.DeclareLocal(_types.Guid);
            il.Emit(OpCodes.Call, _types.GetMethodNoParams(_types.Guid, "NewGuid"));
            il.Emit(OpCodes.Stloc, guidLocal);
            il.Emit(OpCodes.Ldloca, guidLocal);
            il.Emit(OpCodes.Constrained, _types.Guid);
            il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        });

        // randomInt(min?, max?) -> number
        EmitCryptoMethodWrapper(typeBuilder, runtime, "randomInt", 2, il =>
        {
            // If arg0 is null, return 0
            var hasArg0Label = il.DefineLabel();
            var hasArg1Label = il.DefineLabel();
            var doRandomLabel = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brtrue, hasArg0Label);

            // No args - return 0
            il.Emit(OpCodes.Ldc_R8, 0.0);
            il.Emit(OpCodes.Box, _types.Double);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(hasArg0Label);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Brtrue, hasArg1Label);

            // One arg - randomInt(max): range [0, max)
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToInt32(il);
            il.Emit(OpCodes.Br, doRandomLabel);

            // Two args - randomInt(min, max): range [min, max)
            il.MarkLabel(hasArg1Label);
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToInt32(il);
            il.Emit(OpCodes.Ldarg_1);
            EmitObjectToInt32(il);

            il.MarkLabel(doRandomLabel);
            il.Emit(OpCodes.Call, _types.GetMethod(
                _types.RandomNumberGenerator,
                "GetInt32",
                _types.Int32, _types.Int32));
            il.Emit(OpCodes.Conv_R8);
            il.Emit(OpCodes.Box, _types.Double);
        });

        // createCipheriv(algorithm, key, iv) -> $Cipher
        EmitCryptoMethodWrapper(typeBuilder, runtime, "createCipheriv", 3, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToString(il);
            il.Emit(OpCodes.Ldarg_1);
            EmitObjectToKeyBytes(il);
            il.Emit(OpCodes.Ldarg_2);
            EmitObjectToKeyBytes(il);
            il.Emit(OpCodes.Call, runtime.CryptoCreateCipheriv);
        });

        // createDecipheriv(algorithm, key, iv) -> $Decipher
        EmitCryptoMethodWrapper(typeBuilder, runtime, "createDecipheriv", 3, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToString(il);
            il.Emit(OpCodes.Ldarg_1);
            EmitObjectToKeyBytes(il);
            il.Emit(OpCodes.Ldarg_2);
            EmitObjectToKeyBytes(il);
            il.Emit(OpCodes.Call, runtime.CryptoCreateDecipheriv);
        });

        // pbkdf2Sync(password, salt, iterations, keylen, digest) -> Buffer
        EmitCryptoMethodWrapper(typeBuilder, runtime, "pbkdf2Sync", 5, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToKeyBytes(il);
            il.Emit(OpCodes.Ldarg_1);
            EmitObjectToKeyBytes(il);
            il.Emit(OpCodes.Ldarg_2);
            EmitObjectToInt32(il);
            il.Emit(OpCodes.Ldarg_3);
            EmitObjectToInt32(il);
            il.Emit(OpCodes.Ldarg, 4);
            EmitObjectToString(il);
            il.Emit(OpCodes.Call, runtime.CryptoPbkdf2Sync);
        });

        // scryptSync(password, salt, keylen, options?) -> Buffer
        EmitCryptoMethodWrapper(typeBuilder, runtime, "scryptSync", 4, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToKeyBytes(il);
            il.Emit(OpCodes.Ldarg_1);
            EmitObjectToKeyBytes(il);
            il.Emit(OpCodes.Ldarg_2);
            EmitObjectToInt32(il);
            il.Emit(OpCodes.Ldarg_3);  // options (can be null)
            il.Emit(OpCodes.Call, runtime.CryptoScryptSync);
        });

        // timingSafeEqual(a, b) -> boolean
        EmitCryptoMethodWrapper(typeBuilder, runtime, "timingSafeEqual", 2, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToKeyBytes(il);
            il.Emit(OpCodes.Ldarg_1);
            EmitObjectToKeyBytes(il);
            il.Emit(OpCodes.Call, runtime.CryptoTimingSafeEqual);
        });

        // createSign(algorithm) -> $Sign
        EmitCryptoMethodWrapper(typeBuilder, runtime, "createSign", 1, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToString(il);
            il.Emit(OpCodes.Call, runtime.CryptoCreateSign);
        });

        // createVerify(algorithm) -> $Verify
        EmitCryptoMethodWrapper(typeBuilder, runtime, "createVerify", 1, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToString(il);
            il.Emit(OpCodes.Call, runtime.CryptoCreateVerify);
        });

        // getHashes() -> $Array
        EmitCryptoMethodWrapper(typeBuilder, runtime, "getHashes", 0, il =>
        {
            il.Emit(OpCodes.Call, runtime.CryptoGetHashes);
        });

        // getCiphers() -> $Array
        EmitCryptoMethodWrapper(typeBuilder, runtime, "getCiphers", 0, il =>
        {
            il.Emit(OpCodes.Call, runtime.CryptoGetCiphers);
        });

        // generateKeyPairSync(type, options?) -> $Object
        EmitCryptoMethodWrapper(typeBuilder, runtime, "generateKeyPairSync", 2, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToString(il);
            il.Emit(OpCodes.Ldarg_1);  // options (can be null)
            il.Emit(OpCodes.Call, runtime.CryptoGenerateKeyPairSync);
        });

        // createDiffieHellman(primeOrLength, generator?) -> $DiffieHellman
        EmitCryptoMethodWrapper(typeBuilder, runtime, "createDiffieHellman", 2, il =>
        {
            il.Emit(OpCodes.Ldarg_0);  // prime or length
            il.Emit(OpCodes.Ldarg_1);  // generator (can be null)
            il.Emit(OpCodes.Call, runtime.CryptoCreateDiffieHellman);
        });

        // getDiffieHellman(groupName) -> $DiffieHellman
        EmitCryptoMethodWrapper(typeBuilder, runtime, "getDiffieHellman", 1, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToString(il);
            il.Emit(OpCodes.Call, runtime.CryptoGetDiffieHellman);
        });

        // createECDH(curveName) -> $ECDH
        EmitCryptoMethodWrapper(typeBuilder, runtime, "createECDH", 1, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToString(il);
            il.Emit(OpCodes.Call, runtime.CryptoCreateECDH);
        });

        // publicEncrypt(key, buffer) -> Buffer
        EmitCryptoMethodWrapper(typeBuilder, runtime, "publicEncrypt", 2, il =>
        {
            il.Emit(OpCodes.Ldarg_0);  // key (as object)
            il.Emit(OpCodes.Ldarg_1);
            EmitObjectToKeyBytes(il);  // buffer as byte[]
            il.Emit(OpCodes.Call, runtime.CryptoPublicEncrypt);
        });

        // privateDecrypt(key, buffer) -> Buffer
        EmitCryptoMethodWrapper(typeBuilder, runtime, "privateDecrypt", 2, il =>
        {
            il.Emit(OpCodes.Ldarg_0);  // key (as object)
            il.Emit(OpCodes.Ldarg_1);
            EmitObjectToKeyBytes(il);  // buffer as byte[]
            il.Emit(OpCodes.Call, runtime.CryptoPrivateDecrypt);
        });

        // privateEncrypt(key, buffer) -> Buffer
        EmitCryptoMethodWrapper(typeBuilder, runtime, "privateEncrypt", 2, il =>
        {
            il.Emit(OpCodes.Ldarg_0);  // key (as object)
            il.Emit(OpCodes.Ldarg_1);
            EmitObjectToKeyBytes(il);  // buffer as byte[]
            il.Emit(OpCodes.Call, runtime.CryptoPrivateEncrypt);
        });

        // publicDecrypt(key, buffer) -> Buffer
        EmitCryptoMethodWrapper(typeBuilder, runtime, "publicDecrypt", 2, il =>
        {
            il.Emit(OpCodes.Ldarg_0);  // key (as object)
            il.Emit(OpCodes.Ldarg_1);
            EmitObjectToKeyBytes(il);  // buffer as byte[]
            il.Emit(OpCodes.Call, runtime.CryptoPublicDecrypt);
        });

        // hkdfSync(digest, ikm, salt, info, keylen) -> Buffer
        EmitCryptoMethodWrapper(typeBuilder, runtime, "hkdfSync", 5, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitObjectToString(il);  // digest
            il.Emit(OpCodes.Ldarg_1);
            EmitObjectToKeyBytes(il);  // ikm
            il.Emit(OpCodes.Ldarg_2);
            EmitObjectToKeyBytes(il);  // salt
            il.Emit(OpCodes.Ldarg_3);
            EmitObjectToKeyBytes(il);  // info
            il.Emit(OpCodes.Ldarg, 4);
            EmitObjectToInt32(il);  // keylen
            il.Emit(OpCodes.Call, runtime.CryptoHkdfSync);
        });

        // createSecretKey(key, encoding?) -> KeyObject
        EmitCryptoMethodWrapper(typeBuilder, runtime, "createSecretKey", 2, il =>
        {
            il.Emit(OpCodes.Ldarg_0);  // key (as object)
            il.Emit(OpCodes.Ldarg_1);  // encoding (can be null)
            il.Emit(OpCodes.Call, runtime.CryptoCreateSecretKey);
        });

        // createPublicKey(key) -> KeyObject
        EmitCryptoMethodWrapper(typeBuilder, runtime, "createPublicKey", 1, il =>
        {
            il.Emit(OpCodes.Ldarg_0);  // key (as object)
            il.Emit(OpCodes.Call, runtime.CryptoCreatePublicKey);
        });

        // createPrivateKey(key) -> KeyObject
        EmitCryptoMethodWrapper(typeBuilder, runtime, "createPrivateKey", 1, il =>
        {
            il.Emit(OpCodes.Ldarg_0);  // key (as object)
            il.Emit(OpCodes.Call, runtime.CryptoCreatePrivateKey);
        });
    }

    /// <summary>
    /// Emits a wrapper method for a crypto module function.
    /// Takes individual object parameters (compatible with TSFunction.Invoke).
    /// </summary>
    private void EmitCryptoMethodWrapper(
        TypeBuilder typeBuilder,
        EmittedRuntime runtime,
        string methodName,
        int paramCount,
        Action<ILGenerator> emitBody)
    {
        var paramTypes = new Type[paramCount];
        for (int i = 0; i < paramCount; i++)
            paramTypes[i] = _types.Object;

        var method = typeBuilder.DefineMethod(
            "CryptoWrapper_" + methodName,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            paramTypes);

        var il = method.GetILGenerator();
        emitBody(il);
        il.Emit(OpCodes.Ret);

        // Register the wrapper for named imports
        runtime.RegisterBuiltInModuleMethod("crypto", methodName, method);
    }

    /// <summary>
    /// Emits code to convert an object to string (handles null).
    /// </summary>
    private void EmitObjectToString(ILGenerator il)
    {
        // obj?.ToString() ?? ""
        var isNullLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, isNullLabel);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(isNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");

        il.MarkLabel(endLabel);
    }

    /// <summary>
    /// Emits code to convert an object to int32 (handles null and boxed doubles).
    /// </summary>
    private void EmitObjectToInt32(ILGenerator il)
    {
        // Check for null
        var notNullLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, notNullLabel);

        // Unbox as double first (TypeScript numbers are doubles)
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(notNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldc_I4_0);

        il.MarkLabel(endLabel);
    }

    /// <summary>
    /// Emits code to convert an object to byte[] for HMAC key.
    /// Handles string (UTF-8) and $Array (Buffer-like).
    /// </summary>
    private void EmitObjectToKeyBytes(ILGenerator il)
    {
        // Check if string or $Array
        var isStringLabel = il.DefineLabel();
        var convertLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        var objLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, objLocal);

        // if (obj is string) -> UTF8.GetBytes
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, isStringLabel);

        // Otherwise, try to convert from $Array to byte[]
        // For now, just encode as UTF-8 string (fallback)
        il.Emit(OpCodes.Br, convertLabel);

        // String path
        il.MarkLabel(isStringLabel);
        il.Emit(OpCodes.Call, _types.Encoding.GetProperty("UTF8")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, _types.Encoding.GetMethod("GetBytes", [_types.String])!);
        il.Emit(OpCodes.Br, endLabel);

        // Fallback - convert to string and then UTF-8
        il.MarkLabel(convertLabel);
        il.Emit(OpCodes.Call, _types.Encoding.GetProperty("UTF8")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Callvirt, _types.Encoding.GetMethod("GetBytes", [_types.String])!);

        il.MarkLabel(endLabel);
    }

    /// <summary>
    /// Emits: public static object CryptoCreateHash(string algorithm)
    /// </summary>
    private void EmitCryptoCreateHash(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoCreateHash",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String]);
        runtime.CryptoCreateHash = method;

        var il = method.GetILGenerator();

        // new $Hash(algorithm) - use emitted type for standalone compatibility
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, runtime.TSHashCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoCreateHmac(string algorithm, byte[] key)
    /// </summary>
    private void EmitCryptoCreateHmac(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoCreateHmac",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.MakeArrayType(_types.Byte)]);
        runtime.CryptoCreateHmac = method;

        var il = method.GetILGenerator();

        // new $Hmac(algorithm, key) - use emitted type for standalone compatibility
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Newobj, runtime.TSHmacCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoCreateCipheriv(string algorithm, byte[] key, byte[] iv)
    /// </summary>
    private void EmitCryptoCreateCipheriv(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoCreateCipheriv",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.MakeArrayType(_types.Byte), _types.MakeArrayType(_types.Byte)]);
        runtime.CryptoCreateCipheriv = method;

        var il = method.GetILGenerator();

        // new $Cipher(algorithm, key, iv)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Newobj, runtime.TSCipherCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoCreateDecipheriv(string algorithm, byte[] key, byte[] iv)
    /// </summary>
    private void EmitCryptoCreateDecipheriv(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoCreateDecipheriv",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.MakeArrayType(_types.Byte), _types.MakeArrayType(_types.Byte)]);
        runtime.CryptoCreateDecipheriv = method;

        var il = method.GetILGenerator();

        // new $Decipher(algorithm, key, iv)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Newobj, runtime.TSDecipherCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoRandomBytes(int size)
    /// Returns a $Buffer containing random bytes.
    /// </summary>
    private void EmitCryptoRandomBytes(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoRandomBytes",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Int32]);
        runtime.CryptoRandomBytes = method;

        var il = method.GetILGenerator();

        // var bytes = RandomNumberGenerator.GetBytes(size);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.RandomNumberGeneratorGetBytes);

        // Return new $Buffer(bytes)
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoRandomFillSync(object buffer, int offset, int size)
    /// Fills the buffer with random bytes in-place and returns the buffer.
    /// If size is -1, fills from offset to end of buffer.
    /// </summary>
    private void EmitCryptoRandomFillSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoRandomFillSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Int32, _types.Int32]);
        runtime.CryptoRandomFillSync = method;

        var il = method.GetILGenerator();

        // Local for buffer's byte[] data
        var dataLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        var sizeLocal = il.DeclareLocal(_types.Int32);
        var randomBytesLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));

        // Get buffer.Data (assume arg0 is $Buffer with Data property)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSBufferType);
        il.Emit(OpCodes.Call, runtime.TSBufferGetData);
        il.Emit(OpCodes.Stloc, dataLocal);

        // Calculate actual size: if size == -1, use data.Length - offset
        var sizeCalculatedLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_2); // size
        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Bne_Un, sizeCalculatedLabel);

        // size = data.Length - offset
        il.Emit(OpCodes.Ldloc, dataLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldarg_1); // offset
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, sizeLocal);
        var continueLabel = il.DefineLabel();
        il.Emit(OpCodes.Br, continueLabel);

        il.MarkLabel(sizeCalculatedLabel);
        il.Emit(OpCodes.Ldarg_2); // size
        il.Emit(OpCodes.Stloc, sizeLocal);

        il.MarkLabel(continueLabel);

        // Generate random bytes: RandomNumberGenerator.GetBytes(size)
        il.Emit(OpCodes.Ldloc, sizeLocal);
        il.Emit(OpCodes.Call, _types.RandomNumberGeneratorGetBytes);
        il.Emit(OpCodes.Stloc, randomBytesLocal);

        // Array.Copy(randomBytes, 0, data, offset, size)
        il.Emit(OpCodes.Ldloc, randomBytesLocal); // sourceArray
        il.Emit(OpCodes.Ldc_I4_0);                // sourceIndex
        il.Emit(OpCodes.Ldloc, dataLocal);        // destinationArray
        il.Emit(OpCodes.Ldarg_1);                 // destinationIndex (offset)
        il.Emit(OpCodes.Ldloc, sizeLocal);        // length
        il.Emit(OpCodes.Call, _types.ArrayCopy5);

        // Return the buffer
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoPbkdf2Sync(byte[] password, byte[] salt, int iterations, int keylen, string digest)
    /// Returns a $Buffer containing the derived key.
    /// </summary>
    private void EmitCryptoPbkdf2Sync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoPbkdf2Sync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.MakeArrayType(_types.Byte), _types.MakeArrayType(_types.Byte), _types.Int32, _types.Int32, _types.String]);
        runtime.CryptoPbkdf2Sync = method;

        var il = method.GetILGenerator();

        // Get HashAlgorithmName based on digest string
        var hashLocal = il.DeclareLocal(typeof(HashAlgorithmName));
        var sha1Label = il.DefineLabel();
        var sha256Label = il.DefineLabel();
        var sha384Label = il.DefineLabel();
        var sha512Label = il.DefineLabel();
        var md5Label = il.DefineLabel();
        var callPbkdf2Label = il.DefineLabel();
        var throwLabel = il.DefineLabel();

        // Convert digest to lowercase for comparison
        il.Emit(OpCodes.Ldarg, 4);
        il.Emit(OpCodes.Callvirt, _types.StringToLowerInvariant);
        var digestLower = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Stloc, digestLower);

        // Check for sha256 (most common)
        il.Emit(OpCodes.Ldloc, digestLower);
        il.Emit(OpCodes.Ldstr, "sha256");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, sha256Label);

        // Check for sha1
        il.Emit(OpCodes.Ldloc, digestLower);
        il.Emit(OpCodes.Ldstr, "sha1");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, sha1Label);

        // Check for sha384
        il.Emit(OpCodes.Ldloc, digestLower);
        il.Emit(OpCodes.Ldstr, "sha384");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, sha384Label);

        // Check for sha512
        il.Emit(OpCodes.Ldloc, digestLower);
        il.Emit(OpCodes.Ldstr, "sha512");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, sha512Label);

        // Check for md5
        il.Emit(OpCodes.Ldloc, digestLower);
        il.Emit(OpCodes.Ldstr, "md5");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, md5Label);

        // Unknown algorithm - throw
        il.Emit(OpCodes.Br, throwLabel);

        // sha256 case
        il.MarkLabel(sha256Label);
        il.Emit(OpCodes.Call, typeof(HashAlgorithmName).GetProperty("SHA256")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, hashLocal);
        il.Emit(OpCodes.Br, callPbkdf2Label);

        // sha1 case
        il.MarkLabel(sha1Label);
        il.Emit(OpCodes.Call, typeof(HashAlgorithmName).GetProperty("SHA1")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, hashLocal);
        il.Emit(OpCodes.Br, callPbkdf2Label);

        // sha384 case
        il.MarkLabel(sha384Label);
        il.Emit(OpCodes.Call, typeof(HashAlgorithmName).GetProperty("SHA384")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, hashLocal);
        il.Emit(OpCodes.Br, callPbkdf2Label);

        // sha512 case
        il.MarkLabel(sha512Label);
        il.Emit(OpCodes.Call, typeof(HashAlgorithmName).GetProperty("SHA512")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, hashLocal);
        il.Emit(OpCodes.Br, callPbkdf2Label);

        // md5 case
        il.MarkLabel(md5Label);
        il.Emit(OpCodes.Call, typeof(HashAlgorithmName).GetProperty("MD5")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, hashLocal);
        il.Emit(OpCodes.Br, callPbkdf2Label);

        // throw case
        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "Unsupported digest algorithm");
        il.Emit(OpCodes.Newobj, _types.ArgumentExceptionCtorString);
        il.Emit(OpCodes.Throw);

        // Call Rfc2898DeriveBytes.Pbkdf2
        il.MarkLabel(callPbkdf2Label);
        il.Emit(OpCodes.Ldarg_0);  // password
        il.Emit(OpCodes.Ldarg_1);  // salt
        il.Emit(OpCodes.Ldarg_2);  // iterations
        il.Emit(OpCodes.Ldloc, hashLocal);  // hashAlgorithm
        il.Emit(OpCodes.Ldarg_3);  // keylen
        il.Emit(OpCodes.Call, typeof(Rfc2898DeriveBytes).GetMethod("Pbkdf2",
            [typeof(byte[]), typeof(byte[]), typeof(int), typeof(HashAlgorithmName), typeof(int)])!);

        // Return new $Buffer(derivedKey)
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoScryptSync(byte[] password, byte[] salt, int keylen, object? options)
    /// Returns a $Buffer containing the derived key.
    /// </summary>
    private void EmitCryptoScryptSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoScryptSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.MakeArrayType(_types.Byte), _types.MakeArrayType(_types.Byte), _types.Int32, _types.Object]);
        runtime.CryptoScryptSync = method;

        // Note: ScryptDeriveBytes is already emitted by EmitScryptMethods (called at start of EmitCryptoMethods)

        // Define a helper method to extract option value
        var getOptionMethod = EmitScryptGetOption(typeBuilder, runtime);

        var il = method.GetILGenerator();

        // Default parameters
        var NLocal = il.DeclareLocal(_types.Int32);
        var rLocal = il.DeclareLocal(_types.Int32);
        var pLocal = il.DeclareLocal(_types.Int32);

        // N = 16384 (default)
        il.Emit(OpCodes.Ldc_I4, 16384);
        il.Emit(OpCodes.Stloc, NLocal);

        // r = 8 (default)
        il.Emit(OpCodes.Ldc_I4, 8);
        il.Emit(OpCodes.Stloc, rLocal);

        // p = 1 (default)
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, pLocal);

        // Check if options is not null
        var noOptionsLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Brfalse, noOptionsLabel);

        // Try to get N from options
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldstr, "N");
        il.Emit(OpCodes.Ldloc, NLocal);
        il.Emit(OpCodes.Call, getOptionMethod);
        il.Emit(OpCodes.Stloc, NLocal);

        // Try to get cost (alias for N)
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldstr, "cost");
        il.Emit(OpCodes.Ldloc, NLocal);
        il.Emit(OpCodes.Call, getOptionMethod);
        il.Emit(OpCodes.Stloc, NLocal);

        // Try to get r from options
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldstr, "r");
        il.Emit(OpCodes.Ldloc, rLocal);
        il.Emit(OpCodes.Call, getOptionMethod);
        il.Emit(OpCodes.Stloc, rLocal);

        // Try to get blockSize (alias for r)
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldstr, "blockSize");
        il.Emit(OpCodes.Ldloc, rLocal);
        il.Emit(OpCodes.Call, getOptionMethod);
        il.Emit(OpCodes.Stloc, rLocal);

        // Try to get p from options
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldstr, "p");
        il.Emit(OpCodes.Ldloc, pLocal);
        il.Emit(OpCodes.Call, getOptionMethod);
        il.Emit(OpCodes.Stloc, pLocal);

        // Try to get parallelization (alias for p)
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldstr, "parallelization");
        il.Emit(OpCodes.Ldloc, pLocal);
        il.Emit(OpCodes.Call, getOptionMethod);
        il.Emit(OpCodes.Stloc, pLocal);

        il.MarkLabel(noOptionsLabel);

        // Call scrypt helper: ScryptDeriveBytes(password, salt, N, r, p, keylen)
        il.Emit(OpCodes.Ldarg_0);  // password
        il.Emit(OpCodes.Ldarg_1);  // salt
        il.Emit(OpCodes.Ldloc, NLocal);  // N
        il.Emit(OpCodes.Ldloc, rLocal);  // r
        il.Emit(OpCodes.Ldloc, pLocal);  // p
        il.Emit(OpCodes.Ldarg_2);  // keylen
        il.Emit(OpCodes.Call, runtime.ScryptDeriveBytes);

        // Return new $Buffer(derivedKey)
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a helper method to extract an int option from an object.
    /// Signature: int GetScryptOption(object options, string name, int defaultValue)
    /// Handles both $Object and Dictionary&lt;string, object&gt; types.
    /// </summary>
    private MethodBuilder EmitScryptGetOption(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetScryptOption",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.Int32,
            [_types.Object, _types.String, _types.Int32]);

        var il = method.GetILGenerator();
        var valueLocal = il.DeclareLocal(_types.Object);
        var returnDefaultLabel = il.DefineLabel();
        var tryDictionaryLabel = il.DefineLabel();
        var checkValueLabel = il.DefineLabel();

        // Check if options is $Object
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brfalse, tryDictionaryLabel);

        // It's $Object - call GetProperty
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectGetProperty);
        il.Emit(OpCodes.Stloc, valueLocal);
        il.Emit(OpCodes.Br, checkValueLabel);

        // Try Dictionary<string, object>
        il.MarkLabel(tryDictionaryLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, returnDefaultLabel);

        // It's Dictionary - call TryGetValue
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("TryGetValue", [_types.String, _types.Object.MakeByRefType()])!);
        il.Emit(OpCodes.Brfalse, returnDefaultLabel);

        // Check if value is double
        il.MarkLabel(checkValueLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Brfalse, returnDefaultLabel);

        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, returnDefaultLabel);

        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ret);

        // returnDefault:
        il.MarkLabel(returnDefaultLabel);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ret);

        return method;
    }

    // Note: EmitScryptHelper removed - scrypt is now emitted by EmitScryptMethods in RuntimeEmitter.Scrypt.cs

    /// <summary>
    /// Emits: public static object CryptoTimingSafeEqual(byte[] a, byte[] b)
    /// Returns a boxed boolean indicating whether the buffers are equal using constant-time comparison.
    /// Throws if the buffers have different lengths (Node.js behavior).
    /// </summary>
    private void EmitCryptoTimingSafeEqual(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoTimingSafeEqual",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.MakeArrayType(_types.Byte), _types.MakeArrayType(_types.Byte)]);
        runtime.CryptoTimingSafeEqual = method;

        var il = method.GetILGenerator();

        // Check if lengths are equal
        var lengthsMatchLabel = il.DefineLabel();
        var aLenLocal = il.DeclareLocal(_types.Int32);
        var bLenLocal = il.DeclareLocal(_types.Int32);

        // Get length of a
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, aLenLocal);

        // Get length of b
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, bLenLocal);

        // if (a.Length == b.Length) goto lengthsMatch
        il.Emit(OpCodes.Ldloc, aLenLocal);
        il.Emit(OpCodes.Ldloc, bLenLocal);
        il.Emit(OpCodes.Beq, lengthsMatchLabel);

        // Throw exception with Node.js-style message
        // "Input buffers must have the same byte length. Received {aLen} and {bLen}"
        il.Emit(OpCodes.Ldstr, "crypto.timingSafeEqual: Input buffers must have the same byte length. Received ");
        il.Emit(OpCodes.Ldloca, aLenLocal);
        il.Emit(OpCodes.Call, _types.Int32.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldstr, " and ");
        il.Emit(OpCodes.Ldloca, bLenLocal);
        il.Emit(OpCodes.Call, _types.Int32.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", [typeof(string), typeof(string), typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Newobj, _types.ArgumentExceptionCtorString);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(lengthsMatchLabel);

        // Convert byte[] to ReadOnlySpan<byte> and call CryptographicOperations.FixedTimeEquals
        // The implicit conversion from byte[] to ReadOnlySpan<byte> is done via op_Implicit
        var opImplicit = _types.ReadOnlySpanOfByte.GetMethod("op_Implicit", [typeof(byte[])])!;
        var fixedTimeEquals = _types.CryptographicOperations.GetMethod("FixedTimeEquals",
            [_types.ReadOnlySpanOfByte, _types.ReadOnlySpanOfByte])!;

        // Convert first byte[] to ReadOnlySpan<byte>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, opImplicit);

        // Convert second byte[] to ReadOnlySpan<byte>
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, opImplicit);

        // Call CryptographicOperations.FixedTimeEquals(span1, span2)
        il.Emit(OpCodes.Call, fixedTimeEquals);

        // Box the result and return
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoCreateSign(string algorithm)
    /// </summary>
    private void EmitCryptoCreateSign(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoCreateSign",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String]);
        runtime.CryptoCreateSign = method;

        var il = method.GetILGenerator();

        // new $Sign(algorithm) - use emitted type for standalone compatibility
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, runtime.TSSignCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoCreateVerify(string algorithm)
    /// </summary>
    private void EmitCryptoCreateVerify(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoCreateVerify",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String]);
        runtime.CryptoCreateVerify = method;

        var il = method.GetILGenerator();

        // new $Verify(algorithm) - use emitted type for standalone compatibility
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, runtime.TSVerifyCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoGetHashes()
    /// Returns an array of supported hash algorithm names.
    /// </summary>
    private void EmitCryptoGetHashes(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoGetHashes",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes);
        runtime.CryptoGetHashes = method;

        var il = method.GetILGenerator();

        // Create List<object?> with hash names
        string[] hashes = ["md5", "sha1", "sha256", "sha384", "sha512"];

        // new List<object?>()
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        var listLocal = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        // Add each hash name to the list
        foreach (var hash in hashes)
        {
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Ldstr, hash);
            il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add", [_types.Object])!);
        }

        // return new $Array(list)
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoGetCiphers()
    /// Returns an array of supported cipher algorithm names.
    /// </summary>
    private void EmitCryptoGetCiphers(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoGetCiphers",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes);
        runtime.CryptoGetCiphers = method;

        var il = method.GetILGenerator();

        // Create List<object?> with cipher names
        string[] ciphers = ["aes-128-cbc", "aes-192-cbc", "aes-256-cbc", "aes-128-gcm", "aes-192-gcm", "aes-256-gcm"];

        // new List<object?>()
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        var listLocal = il.DeclareLocal(_types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        // Add each cipher name to the list
        foreach (var cipher in ciphers)
        {
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Ldstr, cipher);
            il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add", [_types.Object])!);
        }

        // return new $Array(list)
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoGenerateKeyPairSync(string type, object? options)
    /// Generates an RSA or EC key pair.
    /// Uses pure IL - no SharpTS.dll dependency.
    /// </summary>
    private void EmitCryptoGenerateKeyPairSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoGenerateKeyPairSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.Object]);
        runtime.CryptoGenerateKeyPairSync = method;

        var il = method.GetILGenerator();

        var tupleLocal = il.DeclareLocal(typeof((string, string)));
        var rsaLabel = il.DefineLabel();
        var ecLabel = il.DefineLabel();
        var throwLabel = il.DefineLabel();
        var createObjectLabel = il.DefineLabel();

        // Convert type to lowercase
        var typeLowerLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.StringToLowerInvariant);
        il.Emit(OpCodes.Stloc, typeLowerLocal);

        // Check for "rsa"
        il.Emit(OpCodes.Ldloc, typeLowerLocal);
        il.Emit(OpCodes.Ldstr, "rsa");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, rsaLabel);

        // Check for "ec"
        il.Emit(OpCodes.Ldloc, typeLowerLocal);
        il.Emit(OpCodes.Ldstr, "ec");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, ecLabel);

        // Unknown type - throw
        il.Emit(OpCodes.Br, throwLabel);

        // RSA key generation
        il.MarkLabel(rsaLabel);
        il.Emit(OpCodes.Ldarg_1);  // options
        il.Emit(OpCodes.Call, runtime.GenerateRsaKeyPairRaw);
        il.Emit(OpCodes.Stloc, tupleLocal);
        il.Emit(OpCodes.Br, createObjectLabel);

        // EC key generation
        il.MarkLabel(ecLabel);
        il.Emit(OpCodes.Ldarg_1);  // options
        il.Emit(OpCodes.Call, runtime.GenerateEcKeyPairRaw);
        il.Emit(OpCodes.Stloc, tupleLocal);
        il.Emit(OpCodes.Br, createObjectLabel);

        // Throw for unknown type
        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "crypto.generateKeyPairSync: unsupported key type");
        il.Emit(OpCodes.Newobj, _types.ArgumentExceptionCtorString);
        il.Emit(OpCodes.Throw);

        // Create result object
        il.MarkLabel(createObjectLabel);

        // Create Dictionary<string, object?> for $Object
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObject.GetConstructor(Type.EmptyTypes)!);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        // dict["publicKey"] = tuple.Item1
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "publicKey");
        il.Emit(OpCodes.Ldloca, tupleLocal);
        il.Emit(OpCodes.Ldfld, typeof((string, string)).GetField("Item1")!);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [_types.String, _types.Object])!);

        // dict["privateKey"] = tuple.Item2
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "privateKey");
        il.Emit(OpCodes.Ldloca, tupleLocal);
        il.Emit(OpCodes.Ldfld, typeof((string, string)).GetField("Item2")!);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [_types.String, _types.Object])!);

        // return new $Object(dict)
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoCreateDiffieHellman(object primeOrLength, object? generator)
    /// Creates a DiffieHellman object using the emitted $DiffieHellman class.
    /// </summary>
    private void EmitCryptoCreateDiffieHellman(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoCreateDiffieHellman",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.CryptoCreateDiffieHellman = method;

        var il = method.GetILGenerator();

        // Check if primeOrLength is a double (number) -> use prime length constructor
        var notNumberLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, notNumberLabel);

        // Use prime length constructor: new $DiffieHellman((int)(double)primeOrLength)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Newobj, runtime.TSDiffieHellmanCtorPrimeLength);
        il.Emit(OpCodes.Ret);

        // Not a number - decode prime and generator bytes
        il.MarkLabel(notNumberLabel);

        // Decode prime bytes
        var primeLocal = il.DeclareLocal(_types.ByteArray);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldnull);  // encoding
        il.Emit(OpCodes.Call, runtime.TSDHDecodeInput);
        il.Emit(OpCodes.Stloc, primeLocal);

        // Decode generator bytes (if not null)
        var generatorLocal = il.DeclareLocal(_types.ByteArray);
        var generatorNullLabel = il.DefineLabel();
        var afterGeneratorLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, generatorNullLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, runtime.TSDHDecodeInput);
        il.Emit(OpCodes.Stloc, generatorLocal);
        il.Emit(OpCodes.Br, afterGeneratorLabel);
        il.MarkLabel(generatorNullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, generatorLocal);
        il.MarkLabel(afterGeneratorLabel);

        // Use prime/generator constructor: new $DiffieHellman(prime, generator)
        il.Emit(OpCodes.Ldloc, primeLocal);
        il.Emit(OpCodes.Ldloc, generatorLocal);
        il.Emit(OpCodes.Newobj, runtime.TSDiffieHellmanCtorPrimeGenerator);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoGetDiffieHellman(string groupName)
    /// Gets a predefined DiffieHellman group using the emitted $DiffieHellman class.
    /// </summary>
    private void EmitCryptoGetDiffieHellman(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoGetDiffieHellman",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String]);
        runtime.CryptoGetDiffieHellman = method;

        var il = method.GetILGenerator();

        // Use group constructor: new $DiffieHellman(groupName)
        il.Emit(OpCodes.Ldarg_0);  // groupName
        il.Emit(OpCodes.Newobj, runtime.TSDiffieHellmanCtorGroup);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoCreateECDH(string curveName)
    /// Creates an ECDH object using the emitted $ECDH class.
    /// </summary>
    private void EmitCryptoCreateECDH(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoCreateECDH",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String]);
        runtime.CryptoCreateECDH = method;

        var il = method.GetILGenerator();

        // Create new $ECDH(curveName)
        il.Emit(OpCodes.Ldarg_0);  // curveName
        il.Emit(OpCodes.Newobj, runtime.TSECDHCtor);
        il.Emit(OpCodes.Ret);
    }

    #region RSA Helpers (Standalone)

    /// <summary>
    /// Emits: public static string ExtractKeyPem(object key)
    /// Extracts PEM string from various key input types (string, object with GetProperty, etc.)
    /// Used by RSA operations for standalone DLLs without SharpTS.dll dependency.
    /// </summary>
    private void EmitExtractKeyPem(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ExtractKeyPem",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]);
        runtime.ExtractKeyPem = method;

        var il = method.GetILGenerator();
        var keyLocal = il.DeclareLocal(_types.Object);
        var stringResultLabel = il.DefineLabel();
        var tryGetPropertyLabel = il.DefineLabel();
        var throwLabel = il.DefineLabel();
        var returnLabel = il.DefineLabel();

        // Store key
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc, keyLocal);

        // if (key is string) return (string)key
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, stringResultLabel);
        il.Emit(OpCodes.Pop);

        // Try to get "key" property from object
        // First try compiled $Object with GetProperty method
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Brfalse, throwLabel);

        // Standalone-only: require emitted $Object for object key extraction.
        var notTsObjectLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brfalse, notTsObjectLabel);

        // var keyValue = (($Object)key).GetProperty("key");
        var keyValueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldstr, "key");
        il.Emit(OpCodes.Callvirt, runtime.TSObjectGetProperty);
        il.Emit(OpCodes.Stloc, keyValueLocal);

        // if (keyValue is string keyStr) return keyStr;
        il.Emit(OpCodes.Ldloc, keyValueLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, returnLabel);
        il.Emit(OpCodes.Pop);
        il.MarkLabel(notTsObjectLabel);

        // throw new ArgumentException(...)
        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "Key must be a PEM string or object with 'key' property");
        il.Emit(OpCodes.Newobj, _types.ArgumentExceptionCtorString);
        il.Emit(OpCodes.Throw);

        // return string result
        il.MarkLabel(stringResultLabel);
        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static byte[] RsaEncryptRaw(string pem, byte[] data, bool useOaep)
    /// Encrypts data using RSA. If useOaep is true, uses OAEP-SHA1; otherwise PKCS#1 v1.5.
    /// </summary>
    private void EmitRsaEncryptRaw(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "RsaEncryptRaw",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.MakeArrayType(_types.Byte),
            [_types.String, _types.MakeArrayType(_types.Byte), _types.Boolean]);
        runtime.RsaEncryptRaw = method;

        var il = method.GetILGenerator();

        // using var rsa = RSA.Create();
        var rsaLocal = il.DeclareLocal(_types.RSA);
        il.Emit(OpCodes.Call, _types.RSA.GetMethod("Create", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, rsaLocal);

        // try { ... } finally { rsa?.Dispose(); }
        il.BeginExceptionBlock();

        // rsa.ImportFromPem(pem);
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Ldarg_0);  // pem
        // ImportFromPem is an extension method, need to use the actual method
        // It's on AsymmetricAlgorithm: public void ImportFromPem(ReadOnlySpan<char> input)
        // For simplicity, convert string to char span
        var importFromPemMethod = typeof(RSA).GetMethod("ImportFromPem", [typeof(ReadOnlySpan<char>)])!;
        // Convert string to ReadOnlySpan<char>
        il.Emit(OpCodes.Call, typeof(MemoryExtensions).GetMethod("AsSpan", [typeof(string)])!);
        il.Emit(OpCodes.Callvirt, importFromPemMethod);

        // var padding = useOaep ? RSAEncryptionPadding.OaepSHA1 : RSAEncryptionPadding.Pkcs1;
        var paddingLocal = il.DeclareLocal(_types.RSAEncryptionPadding);
        var usePkcs1Label = il.DefineLabel();
        var paddingDoneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);  // useOaep
        il.Emit(OpCodes.Brfalse, usePkcs1Label);
        il.Emit(OpCodes.Call, _types.RSAEncryptionPadding.GetProperty("OaepSHA1")!.GetGetMethod()!);
        il.Emit(OpCodes.Br, paddingDoneLabel);
        il.MarkLabel(usePkcs1Label);
        il.Emit(OpCodes.Call, _types.RSAEncryptionPadding.GetProperty("Pkcs1")!.GetGetMethod()!);
        il.MarkLabel(paddingDoneLabel);
        il.Emit(OpCodes.Stloc, paddingLocal);

        // return rsa.Encrypt(data, padding);
        var resultLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Ldarg_1);  // data
        il.Emit(OpCodes.Ldloc, paddingLocal);
        il.Emit(OpCodes.Callvirt, _types.RSA.GetMethod("Encrypt", [typeof(byte[]), typeof(RSAEncryptionPadding)])!);
        il.Emit(OpCodes.Stloc, resultLocal);

        // finally block
        il.BeginFinallyBlock();
        var skipDisposeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Brfalse, skipDisposeLabel);
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IDisposable, "Dispose"));
        il.MarkLabel(skipDisposeLabel);
        il.EndExceptionBlock();

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static byte[] RsaDecryptRaw(string pem, byte[] data, bool useOaep)
    /// Decrypts data using RSA. If useOaep is true, uses OAEP-SHA1; otherwise PKCS#1 v1.5.
    /// </summary>
    private void EmitRsaDecryptRaw(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "RsaDecryptRaw",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.MakeArrayType(_types.Byte),
            [_types.String, _types.MakeArrayType(_types.Byte), _types.Boolean]);
        runtime.RsaDecryptRaw = method;

        var il = method.GetILGenerator();

        // using var rsa = RSA.Create();
        var rsaLocal = il.DeclareLocal(_types.RSA);
        il.Emit(OpCodes.Call, _types.RSA.GetMethod("Create", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, rsaLocal);

        // try { ... } finally { rsa?.Dispose(); }
        il.BeginExceptionBlock();

        // rsa.ImportFromPem(pem);
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Ldarg_0);  // pem
        var importFromPemMethod = typeof(RSA).GetMethod("ImportFromPem", [typeof(ReadOnlySpan<char>)])!;
        il.Emit(OpCodes.Call, typeof(MemoryExtensions).GetMethod("AsSpan", [typeof(string)])!);
        il.Emit(OpCodes.Callvirt, importFromPemMethod);

        // var padding = useOaep ? RSAEncryptionPadding.OaepSHA1 : RSAEncryptionPadding.Pkcs1;
        var paddingLocal = il.DeclareLocal(_types.RSAEncryptionPadding);
        var usePkcs1Label = il.DefineLabel();
        var paddingDoneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);  // useOaep
        il.Emit(OpCodes.Brfalse, usePkcs1Label);
        il.Emit(OpCodes.Call, _types.RSAEncryptionPadding.GetProperty("OaepSHA1")!.GetGetMethod()!);
        il.Emit(OpCodes.Br, paddingDoneLabel);
        il.MarkLabel(usePkcs1Label);
        il.Emit(OpCodes.Call, _types.RSAEncryptionPadding.GetProperty("Pkcs1")!.GetGetMethod()!);
        il.MarkLabel(paddingDoneLabel);
        il.Emit(OpCodes.Stloc, paddingLocal);

        // return rsa.Decrypt(data, padding);
        var resultLocal = il.DeclareLocal(_types.MakeArrayType(_types.Byte));
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Ldarg_1);  // data
        il.Emit(OpCodes.Ldloc, paddingLocal);
        il.Emit(OpCodes.Callvirt, _types.RSA.GetMethod("Decrypt", [typeof(byte[]), typeof(RSAEncryptionPadding)])!);
        il.Emit(OpCodes.Stloc, resultLocal);

        // finally block
        il.BeginFinallyBlock();
        var skipDisposeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Brfalse, skipDisposeLabel);
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IDisposable, "Dispose"));
        il.MarkLabel(skipDisposeLabel);
        il.EndExceptionBlock();

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    #endregion

    #region Key Pair Generation Helpers (Standalone)

    /// <summary>
    /// Emits: public static int GetOptionInt(object options, string name, int defaultValue)
    /// Extracts an int option from an object using GetProperty or reflection.
    /// </summary>
    private void EmitGetOptionInt(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetOptionInt",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Int32,
            [_types.Object, _types.String, _types.Int32]);
        runtime.GetOptionInt = method;

        var il = method.GetILGenerator();
        var returnDefaultLabel = il.DefineLabel();
        var checkValueLabel = il.DefineLabel();

        // if (options == null) return defaultValue
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnDefaultLabel);

        // options must be emitted $Object in standalone mode.
        var optionsObjLocal = il.DeclareLocal(runtime.TSObjectType);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Stloc, optionsObjLocal);
        il.Emit(OpCodes.Ldloc, optionsObjLocal);
        il.Emit(OpCodes.Brfalse, returnDefaultLabel);

        // var value = options.GetProperty(name)
        var valueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, optionsObjLocal);
        il.Emit(OpCodes.Ldarg_1);  // name
        il.Emit(OpCodes.Callvirt, runtime.TSObjectGetProperty);
        il.Emit(OpCodes.Stloc, valueLocal);

        // if (value is double d) return (int)d
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, returnDefaultLabel);

        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ret);

        // return defaultValue
        il.MarkLabel(returnDefaultLabel);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static string GetOptionString(object options, string name, string defaultValue)
    /// Extracts a string option from an object using GetProperty or reflection.
    /// </summary>
    private void EmitGetOptionString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetOptionString",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object, _types.String, _types.String]);
        runtime.GetOptionString = method;

        var il = method.GetILGenerator();
        var returnDefaultLabel = il.DefineLabel();

        // if (options == null) return defaultValue
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnDefaultLabel);

        // options must be emitted $Object in standalone mode.
        var optionsObjLocal = il.DeclareLocal(runtime.TSObjectType);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Stloc, optionsObjLocal);
        il.Emit(OpCodes.Ldloc, optionsObjLocal);
        il.Emit(OpCodes.Brfalse, returnDefaultLabel);

        // var value = options.GetProperty(name)
        var valueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, optionsObjLocal);
        il.Emit(OpCodes.Ldarg_1);  // name
        il.Emit(OpCodes.Callvirt, runtime.TSObjectGetProperty);
        il.Emit(OpCodes.Stloc, valueLocal);

        // if (value is string s) return s
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Dup);
        var returnStringLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, returnStringLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Br, returnDefaultLabel);

        il.MarkLabel(returnStringLabel);
        il.Emit(OpCodes.Ret);

        // return defaultValue
        il.MarkLabel(returnDefaultLabel);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static (string, string) GenerateRsaKeyPairRaw(object? options)
    /// Generates RSA key pair and returns (publicKeyPem, privateKeyPem).
    /// </summary>
    private void EmitGenerateRsaKeyPairRaw(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var tupleType = typeof(ValueTuple<string, string>);
        var method = typeBuilder.DefineMethod(
            "GenerateRsaKeyPairRaw",
            MethodAttributes.Public | MethodAttributes.Static,
            tupleType,
            [_types.Object]);
        runtime.GenerateRsaKeyPairRaw = method;

        var il = method.GetILGenerator();

        // int modulusLength = GetOptionInt(options, "modulusLength", 2048)
        var modulusLengthLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldarg_0);  // options
        il.Emit(OpCodes.Ldstr, "modulusLength");
        il.Emit(OpCodes.Ldc_I4, 2048);
        il.Emit(OpCodes.Call, runtime.GetOptionInt);
        il.Emit(OpCodes.Stloc, modulusLengthLocal);

        // using var rsa = RSA.Create(modulusLength)
        var rsaLocal = il.DeclareLocal(_types.RSA);
        il.Emit(OpCodes.Ldloc, modulusLengthLocal);
        il.Emit(OpCodes.Call, _types.RSA.GetMethod("Create", [typeof(int)])!);
        il.Emit(OpCodes.Stloc, rsaLocal);

        il.BeginExceptionBlock();

        // var publicKey = rsa.ExportSubjectPublicKeyInfoPem()
        var publicKeyLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Callvirt, _types.RSA.GetMethod("ExportSubjectPublicKeyInfoPem", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, publicKeyLocal);

        // var privateKey = rsa.ExportPkcs8PrivateKeyPem()
        var privateKeyLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Callvirt, _types.RSA.GetMethod("ExportPkcs8PrivateKeyPem", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, privateKeyLocal);

        il.BeginFinallyBlock();
        var skipDisposeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Brfalse, skipDisposeLabel);
        il.Emit(OpCodes.Ldloc, rsaLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IDisposable, "Dispose"));
        il.MarkLabel(skipDisposeLabel);
        il.EndExceptionBlock();

        // return (publicKey, privateKey)
        il.Emit(OpCodes.Ldloc, publicKeyLocal);
        il.Emit(OpCodes.Ldloc, privateKeyLocal);
        il.Emit(OpCodes.Newobj, tupleType.GetConstructor([typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static (string, string) GenerateEcKeyPairRaw(object? options)
    /// Generates EC key pair and returns (publicKeyPem, privateKeyPem).
    /// </summary>
    private void EmitGenerateEcKeyPairRaw(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var tupleType = typeof(ValueTuple<string, string>);
        var method = typeBuilder.DefineMethod(
            "GenerateEcKeyPairRaw",
            MethodAttributes.Public | MethodAttributes.Static,
            tupleType,
            [_types.Object]);
        runtime.GenerateEcKeyPairRaw = method;

        var il = method.GetILGenerator();

        // string curveName = GetOptionString(options, "namedCurve", "prime256v1")
        var curveNameLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_0);  // options
        il.Emit(OpCodes.Ldstr, "namedCurve");
        il.Emit(OpCodes.Ldstr, "prime256v1");
        il.Emit(OpCodes.Call, runtime.GetOptionString);
        il.Emit(OpCodes.Stloc, curveNameLocal);

        // Map curveName to ECCurve
        var curveLocal = il.DeclareLocal(typeof(ECCurve));
        var p256Label = il.DefineLabel();
        var p384Label = il.DefineLabel();
        var p521Label = il.DefineLabel();
        var throwLabel = il.DefineLabel();
        var createKeyLabel = il.DefineLabel();

        // Convert to lowercase
        il.Emit(OpCodes.Ldloc, curveNameLocal);
        il.Emit(OpCodes.Callvirt, _types.StringToLowerInvariant);
        il.Emit(OpCodes.Stloc, curveNameLocal);

        // Check for P-256 variants
        il.Emit(OpCodes.Ldloc, curveNameLocal);
        il.Emit(OpCodes.Ldstr, "prime256v1");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, p256Label);
        il.Emit(OpCodes.Ldloc, curveNameLocal);
        il.Emit(OpCodes.Ldstr, "secp256r1");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, p256Label);
        il.Emit(OpCodes.Ldloc, curveNameLocal);
        il.Emit(OpCodes.Ldstr, "p-256");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, p256Label);

        // Check for P-384 variants
        il.Emit(OpCodes.Ldloc, curveNameLocal);
        il.Emit(OpCodes.Ldstr, "secp384r1");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, p384Label);
        il.Emit(OpCodes.Ldloc, curveNameLocal);
        il.Emit(OpCodes.Ldstr, "p-384");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, p384Label);

        // Check for P-521 variants
        il.Emit(OpCodes.Ldloc, curveNameLocal);
        il.Emit(OpCodes.Ldstr, "secp521r1");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, p521Label);
        il.Emit(OpCodes.Ldloc, curveNameLocal);
        il.Emit(OpCodes.Ldstr, "p-521");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, p521Label);

        // Unknown curve - throw
        il.Emit(OpCodes.Br, throwLabel);

        // P-256
        il.MarkLabel(p256Label);
        il.Emit(OpCodes.Call, typeof(ECCurve.NamedCurves).GetProperty("nistP256")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, curveLocal);
        il.Emit(OpCodes.Br, createKeyLabel);

        // P-384
        il.MarkLabel(p384Label);
        il.Emit(OpCodes.Call, typeof(ECCurve.NamedCurves).GetProperty("nistP384")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, curveLocal);
        il.Emit(OpCodes.Br, createKeyLabel);

        // P-521
        il.MarkLabel(p521Label);
        il.Emit(OpCodes.Call, typeof(ECCurve.NamedCurves).GetProperty("nistP521")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, curveLocal);
        il.Emit(OpCodes.Br, createKeyLabel);

        // Throw for unknown curve
        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "crypto.generateKeyPairSync: unsupported curve");
        il.Emit(OpCodes.Newobj, _types.ArgumentExceptionCtorString);
        il.Emit(OpCodes.Throw);

        // Create ECDsa key
        il.MarkLabel(createKeyLabel);
        var ecdsaLocal = il.DeclareLocal(typeof(ECDsa));
        il.Emit(OpCodes.Ldloc, curveLocal);
        il.Emit(OpCodes.Call, typeof(ECDsa).GetMethod("Create", [typeof(ECCurve)])!);
        il.Emit(OpCodes.Stloc, ecdsaLocal);

        il.BeginExceptionBlock();

        // var publicKey = ecdsa.ExportSubjectPublicKeyInfoPem()
        var publicKeyLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        il.Emit(OpCodes.Callvirt, typeof(ECDsa).GetMethod("ExportSubjectPublicKeyInfoPem", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, publicKeyLocal);

        // var privateKey = ecdsa.ExportPkcs8PrivateKeyPem()
        var privateKeyLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        il.Emit(OpCodes.Callvirt, typeof(ECDsa).GetMethod("ExportPkcs8PrivateKeyPem", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, privateKeyLocal);

        il.BeginFinallyBlock();
        var skipDisposeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        il.Emit(OpCodes.Brfalse, skipDisposeLabel);
        il.Emit(OpCodes.Ldloc, ecdsaLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IDisposable, "Dispose"));
        il.MarkLabel(skipDisposeLabel);
        il.EndExceptionBlock();

        // return (publicKey, privateKey)
        il.Emit(OpCodes.Ldloc, publicKeyLocal);
        il.Emit(OpCodes.Ldloc, privateKeyLocal);
        il.Emit(OpCodes.Newobj, tupleType.GetConstructor([typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Ret);
    }

    #endregion

    #region RSA Encryption/Decryption

    /// <summary>
    /// Emits: public static object CryptoPublicEncrypt(object key, byte[] buffer)
    /// Encrypts data using RSA-OAEP with SHA-1.
    /// Uses pure IL - no SharpTS.dll dependency.
    /// </summary>
    private void EmitCryptoPublicEncrypt(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoPublicEncrypt",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.MakeArrayType(_types.Byte)]);
        runtime.CryptoPublicEncrypt = method;

        var il = method.GetILGenerator();

        // var pem = ExtractKeyPem(key);
        il.Emit(OpCodes.Ldarg_0);  // key
        il.Emit(OpCodes.Call, runtime.ExtractKeyPem);

        // var result = RsaEncryptRaw(pem, buffer, true);  // true = use OAEP
        il.Emit(OpCodes.Ldarg_1);  // buffer
        il.Emit(OpCodes.Ldc_I4_1);  // useOaep = true
        il.Emit(OpCodes.Call, runtime.RsaEncryptRaw);

        // Wrap result in $Buffer
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoPrivateDecrypt(object key, byte[] buffer)
    /// Decrypts data using RSA-OAEP with SHA-1.
    /// Uses pure IL - no SharpTS.dll dependency.
    /// </summary>
    private void EmitCryptoPrivateDecrypt(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoPrivateDecrypt",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.MakeArrayType(_types.Byte)]);
        runtime.CryptoPrivateDecrypt = method;

        var il = method.GetILGenerator();

        // var pem = ExtractKeyPem(key);
        il.Emit(OpCodes.Ldarg_0);  // key
        il.Emit(OpCodes.Call, runtime.ExtractKeyPem);

        // var result = RsaDecryptRaw(pem, buffer, true);  // true = use OAEP
        il.Emit(OpCodes.Ldarg_1);  // buffer
        il.Emit(OpCodes.Ldc_I4_1);  // useOaep = true
        il.Emit(OpCodes.Call, runtime.RsaDecryptRaw);

        // Wrap result in $Buffer
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoPrivateEncrypt(object key, byte[] buffer)
    /// Encrypts data using private key (PKCS#1 v1.5 signing primitive).
    /// Note: privateEncrypt in Node.js actually uses Decrypt with PKCS#1 padding.
    /// Uses pure IL - no SharpTS.dll dependency.
    /// </summary>
    private void EmitCryptoPrivateEncrypt(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoPrivateEncrypt",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.MakeArrayType(_types.Byte)]);
        runtime.CryptoPrivateEncrypt = method;

        var il = method.GetILGenerator();

        // var pem = ExtractKeyPem(key);
        il.Emit(OpCodes.Ldarg_0);  // key
        il.Emit(OpCodes.Call, runtime.ExtractKeyPem);

        // var result = RsaDecryptRaw(pem, buffer, false);  // false = use PKCS#1
        // Note: privateEncrypt uses RSA Decrypt with PKCS#1 padding
        il.Emit(OpCodes.Ldarg_1);  // buffer
        il.Emit(OpCodes.Ldc_I4_0);  // useOaep = false (PKCS#1)
        il.Emit(OpCodes.Call, runtime.RsaDecryptRaw);

        // Wrap result in $Buffer
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoPublicDecrypt(object key, byte[] buffer)
    /// Decrypts data using public key (PKCS#1 v1.5 verification primitive).
    /// Note: publicDecrypt in Node.js actually uses Encrypt with PKCS#1 padding.
    /// Uses pure IL - no SharpTS.dll dependency.
    /// </summary>
    private void EmitCryptoPublicDecrypt(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoPublicDecrypt",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.MakeArrayType(_types.Byte)]);
        runtime.CryptoPublicDecrypt = method;

        var il = method.GetILGenerator();

        // var pem = ExtractKeyPem(key);
        il.Emit(OpCodes.Ldarg_0);  // key
        il.Emit(OpCodes.Call, runtime.ExtractKeyPem);

        // var result = RsaEncryptRaw(pem, buffer, false);  // false = use PKCS#1
        // Note: publicDecrypt uses RSA Encrypt with PKCS#1 padding
        il.Emit(OpCodes.Ldarg_1);  // buffer
        il.Emit(OpCodes.Ldc_I4_0);  // useOaep = false (PKCS#1)
        il.Emit(OpCodes.Call, runtime.RsaEncryptRaw);

        // Wrap result in $Buffer
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    #endregion

    #region HKDF

    /// <summary>
    /// Emits: public static object CryptoHkdfSync(string digest, byte[] ikm, byte[] salt, byte[] info, int keylen)
    /// HKDF key derivation (RFC 5869).
    /// Pure IL emission - no SharpTS.dll dependency.
    /// </summary>
    private void EmitCryptoHkdfSync(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoHkdfSync",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.MakeArrayType(_types.Byte), _types.MakeArrayType(_types.Byte),
             _types.MakeArrayType(_types.Byte), _types.Int32]);
        runtime.CryptoHkdfSync = method;

        var il = method.GetILGenerator();

        var hashAlgLocal = il.DeclareLocal(_types.HashAlgorithmName);
        var lowerDigestLocal = il.DeclareLocal(_types.String);

        // Labels for hash algorithm selection
        var sha1Label = il.DefineLabel();
        var sha256Label = il.DefineLabel();
        var sha384Label = il.DefineLabel();
        var sha512Label = il.DefineLabel();
        var unsupportedLabel = il.DefineLabel();
        var deriveKeyLabel = il.DefineLabel();
        var keylenNegativeLabel = il.DefineLabel();
        var keylenZeroLabel = il.DefineLabel();
        var afterKeylenCheckLabel = il.DefineLabel();

        // if (keylen < 0) throw ArgumentException
        il.Emit(OpCodes.Ldarg, 4);  // keylen
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, keylenNegativeLabel);

        // if (keylen == 0) return new $Buffer(Array.Empty<byte>())
        il.Emit(OpCodes.Ldarg, 4);  // keylen
        il.Emit(OpCodes.Brfalse, keylenZeroLabel);
        il.Emit(OpCodes.Br, afterKeylenCheckLabel);

        il.MarkLabel(keylenNegativeLabel);
        il.Emit(OpCodes.Ldstr, "crypto.hkdfSync: keylen must be non-negative");
        il.Emit(OpCodes.Newobj, _types.ArgumentExceptionCtorString);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(keylenZeroLabel);
        // Return new $Buffer(Array.Empty<byte>())
        il.Emit(OpCodes.Call, typeof(Array).GetMethod("Empty")!.MakeGenericMethod(_types.Byte));
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(afterKeylenCheckLabel);

        // lowerDigest = digest.ToLowerInvariant()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("ToLowerInvariant")!);
        il.Emit(OpCodes.Stloc, lowerDigestLocal);

        // Switch on digest name
        // if (lowerDigest == "sha1") goto sha1Label
        il.Emit(OpCodes.Ldloc, lowerDigestLocal);
        il.Emit(OpCodes.Ldstr, "sha1");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, sha1Label);

        // if (lowerDigest == "sha256") goto sha256Label
        il.Emit(OpCodes.Ldloc, lowerDigestLocal);
        il.Emit(OpCodes.Ldstr, "sha256");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, sha256Label);

        // if (lowerDigest == "sha384") goto sha384Label
        il.Emit(OpCodes.Ldloc, lowerDigestLocal);
        il.Emit(OpCodes.Ldstr, "sha384");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, sha384Label);

        // if (lowerDigest == "sha512") goto sha512Label
        il.Emit(OpCodes.Ldloc, lowerDigestLocal);
        il.Emit(OpCodes.Ldstr, "sha512");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, sha512Label);

        // else goto unsupported
        il.Emit(OpCodes.Br, unsupportedLabel);

        // sha1: hashAlg = HashAlgorithmName.SHA1
        il.MarkLabel(sha1Label);
        il.Emit(OpCodes.Call, _types.HashAlgorithmName.GetProperty("SHA1")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, hashAlgLocal);
        il.Emit(OpCodes.Br, deriveKeyLabel);

        // sha256: hashAlg = HashAlgorithmName.SHA256
        il.MarkLabel(sha256Label);
        il.Emit(OpCodes.Call, _types.HashAlgorithmName.GetProperty("SHA256")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, hashAlgLocal);
        il.Emit(OpCodes.Br, deriveKeyLabel);

        // sha384: hashAlg = HashAlgorithmName.SHA384
        il.MarkLabel(sha384Label);
        il.Emit(OpCodes.Call, _types.HashAlgorithmName.GetProperty("SHA384")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, hashAlgLocal);
        il.Emit(OpCodes.Br, deriveKeyLabel);

        // sha512: hashAlg = HashAlgorithmName.SHA512
        il.MarkLabel(sha512Label);
        il.Emit(OpCodes.Call, _types.HashAlgorithmName.GetProperty("SHA512")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, hashAlgLocal);
        il.Emit(OpCodes.Br, deriveKeyLabel);

        // unsupported: throw ArgumentException
        il.MarkLabel(unsupportedLabel);
        il.Emit(OpCodes.Ldstr, "crypto.hkdfSync: unsupported digest algorithm '");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "'. Supported: sha1, sha256, sha384, sha512");
        il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", [typeof(string), typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Newobj, _types.ArgumentExceptionCtorString);
        il.Emit(OpCodes.Throw);

        // deriveKey: return new $Buffer(HKDF.DeriveKey(hashAlg, ikm, keylen, salt, info))
        il.MarkLabel(deriveKeyLabel);
        il.Emit(OpCodes.Ldloc, hashAlgLocal);   // hashAlgorithm
        il.Emit(OpCodes.Ldarg_1);               // ikm
        il.Emit(OpCodes.Ldarg, 4);              // keylen (outputLength)
        il.Emit(OpCodes.Ldarg_2);               // salt
        il.Emit(OpCodes.Ldarg_3);               // info
        il.Emit(OpCodes.Call, _types.HKDF.GetMethod("DeriveKey",
            [_types.HashAlgorithmName, typeof(byte[]), typeof(int), typeof(byte[]), typeof(byte[])])!);
        il.Emit(OpCodes.Newobj, runtime.TSBufferCtor);
        il.Emit(OpCodes.Ret);
    }

    #endregion

    #region Sign/Verify Helpers (Standalone)

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

    #endregion

    #region KeyObject

    /// <summary>
    /// Emits: public static object CryptoCreateSecretKey(object key, object? encoding)
    /// Creates a secret (symmetric) KeyObject using pure IL (no SharpTS.dll dependency).
    /// </summary>
    private void EmitCryptoCreateSecretKey(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoCreateSecretKey",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]);
        runtime.CryptoCreateSecretKey = method;

        var il = method.GetILGenerator();

        var keyBytesLocal = il.DeclareLocal(_types.ByteArray);
        var encodingLocal = il.DeclareLocal(_types.String);

        // Check if key is string
        var notStringLabel = il.DefineLabel();
        var createKeyObjectLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, notStringLabel);

        // key is string - get encoding
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Dup);
        var useDefaultEncodingLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, useDefaultEncodingLabel);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        var encodingSetLabel = il.DefineLabel();
        il.Emit(OpCodes.Br, encodingSetLabel);
        il.MarkLabel(useDefaultEncodingLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "utf8");
        il.MarkLabel(encodingSetLabel);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("ToLowerInvariant")!);
        il.Emit(OpCodes.Stloc, encodingLocal);

        // Convert string to bytes based on encoding
        var hexLabel = il.DefineLabel();
        var base64Label = il.DefineLabel();
        var latin1Label = il.DefineLabel();
        var stringConvertedLabel = il.DefineLabel();

        // Check for "hex"
        il.Emit(OpCodes.Ldloc, encodingLocal);
        il.Emit(OpCodes.Ldstr, "hex");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, hexLabel);

        // Check for "base64"
        il.Emit(OpCodes.Ldloc, encodingLocal);
        il.Emit(OpCodes.Ldstr, "base64");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Brtrue, base64Label);

        // Check for "latin1" or "binary"
        il.Emit(OpCodes.Ldloc, encodingLocal);
        il.Emit(OpCodes.Ldstr, "latin1");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Ldloc, encodingLocal);
        il.Emit(OpCodes.Ldstr, "binary");
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Brtrue, latin1Label);

        // Default: UTF8
        il.Emit(OpCodes.Call, typeof(System.Text.Encoding).GetProperty("UTF8")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, typeof(System.Text.Encoding).GetMethod("GetBytes", [_types.String])!);
        il.Emit(OpCodes.Stloc, keyBytesLocal);
        il.Emit(OpCodes.Br, stringConvertedLabel);

        il.MarkLabel(hexLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Call, typeof(Convert).GetMethod("FromHexString", [_types.String])!);
        il.Emit(OpCodes.Stloc, keyBytesLocal);
        il.Emit(OpCodes.Br, stringConvertedLabel);

        il.MarkLabel(base64Label);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Call, typeof(Convert).GetMethod("FromBase64String", [_types.String])!);
        il.Emit(OpCodes.Stloc, keyBytesLocal);
        il.Emit(OpCodes.Br, stringConvertedLabel);

        il.MarkLabel(latin1Label);
        il.Emit(OpCodes.Call, typeof(System.Text.Encoding).GetProperty("Latin1")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Callvirt, typeof(System.Text.Encoding).GetMethod("GetBytes", [_types.String])!);
        il.Emit(OpCodes.Stloc, keyBytesLocal);

        il.MarkLabel(stringConvertedLabel);
        il.Emit(OpCodes.Br, createKeyObjectLabel);

        // Not a string - try to get bytes from Buffer or byte[]
        il.MarkLabel(notStringLabel);

        // Try byte[] first
        var tryBufferLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ByteArray);
        il.Emit(OpCodes.Brfalse, tryBufferLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ByteArray);
        il.Emit(OpCodes.Stloc, keyBytesLocal);
        il.Emit(OpCodes.Br, createKeyObjectLabel);

        // Try $Buffer - call GetData() method
        il.MarkLabel(tryBufferLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSBufferType);
        var trySharpTSBufferLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, trySharpTSBufferLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSBufferType);
        il.Emit(OpCodes.Call, runtime.TSBufferGetData);
        il.Emit(OpCodes.Stloc, keyBytesLocal);
        il.Emit(OpCodes.Br, createKeyObjectLabel);

        // Standalone-only behavior: no interpreter SharpTSBuffer reflection fallback.
        il.MarkLabel(trySharpTSBufferLabel);
        il.Emit(OpCodes.Ldstr, "crypto.createSecretKey: key must be a Buffer or string");
        il.Emit(OpCodes.Newobj, typeof(ArgumentException).GetConstructor([_types.String])!);
        il.Emit(OpCodes.Throw);

        // Create and return new $TSKeyObject(keyBytes)
        il.MarkLabel(createKeyObjectLabel);
        il.Emit(OpCodes.Ldloc, keyBytesLocal);
        il.Emit(OpCodes.Newobj, runtime.TSKeyObjectCtorSecret);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoCreatePublicKey(object key)
    /// Creates a public KeyObject from PEM using pure IL (no SharpTS.dll dependency).
    /// </summary>
    private void EmitCryptoCreatePublicKey(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoCreatePublicKey",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]);
        runtime.CryptoCreatePublicKey = method;

        var il = method.GetILGenerator();

        var pemLocal = il.DeclareLocal(_types.String);

        // Extract PEM from key
        var notStringLabel = il.DefineLabel();
        var createKeyObjectLabel = il.DefineLabel();

        // Check if key is string
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, notStringLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Stloc, pemLocal);
        il.Emit(OpCodes.Br, createKeyObjectLabel);

        // Not a string - require emitted $Object and read its "key" property.
        il.MarkLabel(notStringLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        var noGetPropertyLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, noGetPropertyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldstr, "key");
        il.Emit(OpCodes.Callvirt, runtime.TSObjectGetProperty);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Stloc, pemLocal);
        il.Emit(OpCodes.Br, createKeyObjectLabel);

        il.MarkLabel(noGetPropertyLabel);
        il.Emit(OpCodes.Ldstr, "crypto.createPublicKey: key must be a PEM string or object with 'key' property");
        il.Emit(OpCodes.Newobj, typeof(ArgumentException).GetConstructor([_types.String])!);
        il.Emit(OpCodes.Throw);

        // Create and return new $TSKeyObject(pem, isPrivate: false)
        il.MarkLabel(createKeyObjectLabel);
        il.Emit(OpCodes.Ldloc, pemLocal);
        il.Emit(OpCodes.Ldc_I4_0); // isPrivate = false
        il.Emit(OpCodes.Newobj, runtime.TSKeyObjectCtorAsym);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object CryptoCreatePrivateKey(object key)
    /// Creates a private KeyObject from PEM using pure IL (no SharpTS.dll dependency).
    /// </summary>
    private void EmitCryptoCreatePrivateKey(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CryptoCreatePrivateKey",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]);
        runtime.CryptoCreatePrivateKey = method;

        var il = method.GetILGenerator();

        var pemLocal = il.DeclareLocal(_types.String);

        // Extract PEM from key
        var notStringLabel = il.DefineLabel();
        var createKeyObjectLabel = il.DefineLabel();

        // Check if key is string
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, notStringLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Stloc, pemLocal);
        il.Emit(OpCodes.Br, createKeyObjectLabel);

        // Not a string - require emitted $Object and read its "key" property.
        il.MarkLabel(notStringLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        var noGetPropertyLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, noGetPropertyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Ldstr, "key");
        il.Emit(OpCodes.Callvirt, runtime.TSObjectGetProperty);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Stloc, pemLocal);
        il.Emit(OpCodes.Br, createKeyObjectLabel);

        il.MarkLabel(noGetPropertyLabel);
        il.Emit(OpCodes.Ldstr, "crypto.createPrivateKey: key must be a PEM string or object with 'key' property");
        il.Emit(OpCodes.Newobj, typeof(ArgumentException).GetConstructor([_types.String])!);
        il.Emit(OpCodes.Throw);

        // Create and return new $TSKeyObject(pem, isPrivate: true)
        il.MarkLabel(createKeyObjectLabel);
        il.Emit(OpCodes.Ldloc, pemLocal);
        il.Emit(OpCodes.Ldc_I4_1); // isPrivate = true
        il.Emit(OpCodes.Newobj, runtime.TSKeyObjectCtorAsym);
        il.Emit(OpCodes.Ret);
    }

    #endregion
}

/// <summary>
/// Static implementation of scrypt key derivation (RFC 7914).
/// Used by both interpreter and compiled code.
/// </summary>
public static class ScryptImpl
{
    /// <summary>
    /// Derives a key with options parsing (for compiled mode).
    /// </summary>
    public static byte[] DeriveWithOptions(byte[] password, byte[] salt, int dkLen, object? options)
    {
        // Default scrypt parameters (Node.js defaults)
        int N = 16384;  // cost parameter (must be power of 2)
        int r = 8;      // block size
        int p = 1;      // parallelization

        // Parse options if provided
        if (options != null)
        {
            N = GetOptionInt(options, "N", N);
            N = GetOptionInt(options, "cost", N);
            r = GetOptionInt(options, "r", r);
            r = GetOptionInt(options, "blockSize", r);
            p = GetOptionInt(options, "p", p);
            p = GetOptionInt(options, "parallelization", p);
        }

        // Validate N is a power of 2
        if (N < 2 || (N & (N - 1)) != 0)
            throw new ArgumentException("scryptSync: N must be a power of 2 greater than 1");

        return DeriveBytes(password, salt, N, r, p, dkLen);
    }

    /// <summary>
    /// Gets an integer option from an object (supports both SharpTSObject and $Object).
    /// </summary>
    private static int GetOptionInt(object options, string name, int defaultValue)
    {
        var type = options.GetType();

        // Try GetProperty method first (for $Object)
        var getPropertyMethod = type.GetMethod("GetProperty", [typeof(string)]);
        if (getPropertyMethod != null)
        {
            var value = getPropertyMethod.Invoke(options, [name]);
            if (value is double d)
                return (int)d;
            return defaultValue;
        }

        // Try Fields property (for SharpTSObject)
        var fieldsProperty = type.GetProperty("Fields");
        if (fieldsProperty != null)
        {
            var fields = fieldsProperty.GetValue(options) as System.Collections.Generic.IReadOnlyDictionary<string, object?>;
            if (fields != null && fields.TryGetValue(name, out var val) && val is double dVal)
                return (int)dVal;
        }

        return defaultValue;
    }

    /// <summary>
    /// Derives a key using the scrypt key derivation function.
    /// </summary>
    public static byte[] DeriveBytes(byte[] password, byte[] salt, int N, int r, int p, int dkLen)
    {
        // Validate parameters
        if (N < 2 || (N & (N - 1)) != 0)
            throw new ArgumentException("N must be a power of 2 greater than 1", nameof(N));
        if (r < 1)
            throw new ArgumentException("r must be at least 1", nameof(r));
        if (p < 1)
            throw new ArgumentException("p must be at least 1", nameof(p));

        // Step 1: Generate initial data B using PBKDF2-HMAC-SHA256
        int blockSize = 128 * r;
        byte[] B = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * blockSize);

        // Step 2: Apply scryptROMix to each block
        for (int i = 0; i < p; i++)
        {
            byte[] block = new byte[blockSize];
            Array.Copy(B, i * blockSize, block, 0, blockSize);
            ScryptROMix(block, N, r);
            Array.Copy(block, 0, B, i * blockSize, blockSize);
        }

        // Step 3: Derive final key using PBKDF2-HMAC-SHA256
        return Rfc2898DeriveBytes.Pbkdf2(password, B, 1, HashAlgorithmName.SHA256, dkLen);
    }

    private static void ScryptROMix(byte[] B, int N, int r)
    {
        int blockSize = 128 * r;
        byte[][] V = new byte[N][];

        // Step 1: Store intermediate values in V
        for (int i = 0; i < N; i++)
        {
            V[i] = (byte[])B.Clone();
            ScryptBlockMix(B, r);
        }

        // Step 2: Mix with random lookups
        for (int i = 0; i < N; i++)
        {
            // Get last 64 bits as little-endian integer mod N
            long j = BitConverter.ToInt64(B, blockSize - 64) & (N - 1);
            if (j < 0) j += N;

            // XOR B with V[j]
            for (int k = 0; k < blockSize; k++)
                B[k] ^= V[j][k];

            ScryptBlockMix(B, r);
        }
    }

    private static void ScryptBlockMix(byte[] B, int r)
    {
        int blockSize = 128 * r;
        byte[] X = new byte[64];
        byte[] Y = new byte[blockSize];

        // Copy last 64-byte block to X
        Array.Copy(B, blockSize - 64, X, 0, 64);

        // Process 2r blocks
        for (int i = 0; i < 2 * r; i++)
        {
            // XOR X with current block
            for (int j = 0; j < 64; j++)
                X[j] ^= B[i * 64 + j];

            // Apply Salsa20/8 core
            Salsa20Core(X);

            // Copy to Y (even blocks first, then odd blocks)
            int destOffset = (i / 2) * 64 + (i % 2) * r * 64;
            Array.Copy(X, 0, Y, destOffset, 64);
        }

        Array.Copy(Y, 0, B, 0, blockSize);
    }

    private static void Salsa20Core(byte[] block)
    {
        // Convert bytes to uint32 array (little-endian)
        uint[] x = new uint[16];
        for (int i = 0; i < 16; i++)
            x[i] = BitConverter.ToUInt32(block, i * 4);

        uint[] original = (uint[])x.Clone();

        // 8 rounds (4 double-rounds)
        for (int i = 0; i < 4; i++)
        {
            // Column round
            x[4] ^= RotateLeft(x[0] + x[12], 7);
            x[8] ^= RotateLeft(x[4] + x[0], 9);
            x[12] ^= RotateLeft(x[8] + x[4], 13);
            x[0] ^= RotateLeft(x[12] + x[8], 18);

            x[9] ^= RotateLeft(x[5] + x[1], 7);
            x[13] ^= RotateLeft(x[9] + x[5], 9);
            x[1] ^= RotateLeft(x[13] + x[9], 13);
            x[5] ^= RotateLeft(x[1] + x[13], 18);

            x[14] ^= RotateLeft(x[10] + x[6], 7);
            x[2] ^= RotateLeft(x[14] + x[10], 9);
            x[6] ^= RotateLeft(x[2] + x[14], 13);
            x[10] ^= RotateLeft(x[6] + x[2], 18);

            x[3] ^= RotateLeft(x[15] + x[11], 7);
            x[7] ^= RotateLeft(x[3] + x[15], 9);
            x[11] ^= RotateLeft(x[7] + x[3], 13);
            x[15] ^= RotateLeft(x[11] + x[7], 18);

            // Row round
            x[1] ^= RotateLeft(x[0] + x[3], 7);
            x[2] ^= RotateLeft(x[1] + x[0], 9);
            x[3] ^= RotateLeft(x[2] + x[1], 13);
            x[0] ^= RotateLeft(x[3] + x[2], 18);

            x[6] ^= RotateLeft(x[5] + x[4], 7);
            x[7] ^= RotateLeft(x[6] + x[5], 9);
            x[4] ^= RotateLeft(x[7] + x[6], 13);
            x[5] ^= RotateLeft(x[4] + x[7], 18);

            x[11] ^= RotateLeft(x[10] + x[9], 7);
            x[8] ^= RotateLeft(x[11] + x[10], 9);
            x[9] ^= RotateLeft(x[8] + x[11], 13);
            x[10] ^= RotateLeft(x[9] + x[8], 18);

            x[12] ^= RotateLeft(x[15] + x[14], 7);
            x[13] ^= RotateLeft(x[12] + x[15], 9);
            x[14] ^= RotateLeft(x[13] + x[12], 13);
            x[15] ^= RotateLeft(x[14] + x[13], 18);
        }

        // Add original to result
        for (int i = 0; i < 16; i++)
            x[i] += original[i];

        // Convert back to bytes
        for (int i = 0; i < 16; i++)
        {
            byte[] bytes = BitConverter.GetBytes(x[i]);
            Array.Copy(bytes, 0, block, i * 4, 4);
        }
    }

    private static uint RotateLeft(uint value, int count)
    {
        return (value << count) | (value >> (32 - count));
    }
}
