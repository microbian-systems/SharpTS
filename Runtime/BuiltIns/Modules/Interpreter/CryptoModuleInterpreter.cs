using System.Security.Cryptography;
using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the Node.js 'crypto' module.
/// </summary>
/// <remarks>
/// Provides cryptographic functionality including:
/// - createHash() - create hash objects for MD5, SHA1, SHA256, SHA512
/// - createHmac() - create HMAC objects for keyed-hash message authentication
/// - randomBytes() - generate cryptographically secure random bytes
/// - randomUUID() - generate a random UUID
/// - randomInt() - generate a random integer in a range
/// </remarks>
public static class CryptoModuleInterpreter
{
    /// <summary>
    /// Gets all exported values for the crypto module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            ["createHash"] = BuiltInMethod.CreateV2("createHash", 1, CreateHash),
            ["createHmac"] = BuiltInMethod.CreateV2("createHmac", 2, CreateHmac),
            ["createCipheriv"] = BuiltInMethod.CreateV2("createCipheriv", 3, CreateCipheriv),
            ["createDecipheriv"] = BuiltInMethod.CreateV2("createDecipheriv", 3, CreateDecipheriv),
            ["randomBytes"] = BuiltInMethod.CreateV2("randomBytes", 1, RandomBytes),
            ["randomFillSync"] = BuiltInMethod.CreateV2("randomFillSync", 1, 3, RandomFillSync),
            ["randomUUID"] = BuiltInMethod.CreateV2("randomUUID", 0, RandomUUID),
            ["randomInt"] = BuiltInMethod.CreateV2("randomInt", 1, 2, RandomInt),
            ["pbkdf2Sync"] = BuiltInMethod.CreateV2("pbkdf2Sync", 5, Pbkdf2Sync),
            ["scryptSync"] = BuiltInMethod.CreateV2("scryptSync", 3, 4, ScryptSync),
            ["timingSafeEqual"] = BuiltInMethod.CreateV2("timingSafeEqual", 2, TimingSafeEqual),
            ["createSign"] = BuiltInMethod.CreateV2("createSign", 1, CreateSign),
            ["createVerify"] = BuiltInMethod.CreateV2("createVerify", 1, CreateVerify),
            ["getHashes"] = BuiltInMethod.CreateV2("getHashes", 0, GetHashes),
            ["getCiphers"] = BuiltInMethod.CreateV2("getCiphers", 0, GetCiphers),
            ["generateKeyPairSync"] = BuiltInMethod.CreateV2("generateKeyPairSync", 1, 2, GenerateKeyPairSync),
            ["createDiffieHellman"] = BuiltInMethod.CreateV2("createDiffieHellman", 1, 2, CreateDiffieHellman),
            ["getDiffieHellman"] = BuiltInMethod.CreateV2("getDiffieHellman", 1, GetDiffieHellman),
            ["createECDH"] = BuiltInMethod.CreateV2("createECDH", 1, CreateECDH),
            // RSA encryption/decryption
            ["publicEncrypt"] = BuiltInMethod.CreateV2("publicEncrypt", 2, PublicEncrypt),
            ["privateDecrypt"] = BuiltInMethod.CreateV2("privateDecrypt", 2, PrivateDecrypt),
            ["privateEncrypt"] = BuiltInMethod.CreateV2("privateEncrypt", 2, PrivateEncrypt),
            ["publicDecrypt"] = BuiltInMethod.CreateV2("publicDecrypt", 2, PublicDecrypt),
            // HKDF key derivation
            ["hkdfSync"] = BuiltInMethod.CreateV2("hkdfSync", 5, HkdfSync),
            // KeyObject API
            ["createSecretKey"] = BuiltInMethod.CreateV2("createSecretKey", 1, 2, CreateSecretKey),
            ["createPublicKey"] = BuiltInMethod.CreateV2("createPublicKey", 1, CreatePublicKey),
            ["createPrivateKey"] = BuiltInMethod.CreateV2("createPrivateKey", 1, CreatePrivateKey),
            // Async (callback-based) key derivation
            ["pbkdf2"] = BuiltInMethod.CreateV2("pbkdf2", 6, Pbkdf2Async),
            ["scrypt"] = BuiltInMethod.CreateV2("scrypt", 4, 5, ScryptAsync),
            ["generateKeyPair"] = BuiltInMethod.CreateV2("generateKeyPair", 2, 3, GenerateKeyPairAsync),
            ["hkdf"] = BuiltInMethod.CreateV2("hkdf", 6, HkdfAsync)
        };
    }

    private static RuntimeValue CreateSign(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || !args[0].IsString)
            throw new Exception("crypto.createSign requires an algorithm name");
        var algorithm = args[0].AsStringUnsafe();

        return RuntimeValue.FromObject(new SharpTSSign(algorithm));
    }

    private static RuntimeValue CreateVerify(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || !args[0].IsString)
            throw new Exception("crypto.createVerify requires an algorithm name");
        var algorithm = args[0].AsStringUnsafe();

        return RuntimeValue.FromObject(new SharpTSVerify(algorithm));
    }

    private static RuntimeValue CreateHash(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || !args[0].IsString)
            throw new Exception("crypto.createHash requires an algorithm name");
        var algorithm = args[0].AsStringUnsafe();

        return RuntimeValue.FromObject(new SharpTSHash(algorithm));
    }

    private static RuntimeValue CreateHmac(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length < 2 || !args[0].IsString)
            throw new Exception("crypto.createHmac requires an algorithm name and a key");
        var algorithm = args[0].AsStringUnsafe();

        var key = args[1].ToObject() ?? throw new Exception("crypto.createHmac requires a key");
        return RuntimeValue.FromObject(new SharpTSHmac(algorithm, key));
    }

    private static RuntimeValue CreateCipheriv(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length < 3 || !args[0].IsString)
            throw new Exception("crypto.createCipheriv requires algorithm, key, and iv arguments");
        var algorithm = args[0].AsStringUnsafe();

        var key = ConvertToBytes(args[1].ToObject()) ?? throw new Exception("crypto.createCipheriv requires a key");
        var iv = ConvertToBytes(args[2].ToObject()) ?? throw new Exception("crypto.createCipheriv requires an iv");

        return RuntimeValue.FromObject(new SharpTSCipher(algorithm, key, iv));
    }

    private static RuntimeValue CreateDecipheriv(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length < 3 || !args[0].IsString)
            throw new Exception("crypto.createDecipheriv requires algorithm, key, and iv arguments");
        var algorithm = args[0].AsStringUnsafe();

        var key = ConvertToBytes(args[1].ToObject()) ?? throw new Exception("crypto.createDecipheriv requires a key");
        var iv = ConvertToBytes(args[2].ToObject()) ?? throw new Exception("crypto.createDecipheriv requires an iv");

        return RuntimeValue.FromObject(new SharpTSDecipher(algorithm, key, iv));
    }

    /// <summary>
    /// Converts a value to a byte array for crypto operations.
    /// </summary>
    private static byte[]? ConvertToBytes(object? value)
    {
        return value switch
        {
            null => null,
            string s => System.Text.Encoding.UTF8.GetBytes(s),
            SharpTSBuffer buf => buf.Data,
            byte[] bytes => bytes,
            _ => throw new Exception("Value must be a string or Buffer")
        };
    }

    private static RuntimeValue RandomBytes(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || !args[0].IsNumber)
            throw new Exception("crypto.randomBytes requires a size argument");
        var size = args[0].AsNumberUnsafe();

        var byteCount = (int)size;
        var bytes = RandomNumberGenerator.GetBytes(byteCount);

        // Return as Buffer (matching Node.js behavior)
        return RuntimeValue.FromObject(new SharpTSBuffer(bytes));
    }

    private static RuntimeValue RandomFillSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || args[0].ToObject() is not SharpTSBuffer buffer)
            throw new Exception("crypto.randomFillSync requires a Buffer argument");

        var data = buffer.Data;

        // Optional offset and size parameters
        int offset = 0;
        int size = data.Length;

        if (args.Length > 1 && args[1].IsNumber)
        {
            offset = (int)args[1].AsNumberUnsafe();
            if (offset < 0 || offset > data.Length)
                throw new Exception($"crypto.randomFillSync: offset out of range (0-{data.Length})");
        }

        if (args.Length > 2 && args[2].IsNumber)
        {
            size = (int)args[2].AsNumberUnsafe();
        }
        else if (args.Length > 1)
        {
            // If only offset is provided, size is rest of buffer
            size = data.Length - offset;
        }

        if (size < 0 || offset + size > data.Length)
            throw new Exception($"crypto.randomFillSync: size out of range");

        // Fill the specified range with random bytes
        var randomBytes = RandomNumberGenerator.GetBytes(size);
        Array.Copy(randomBytes, 0, data, offset, size);

        // Return the buffer (same reference)
        return RuntimeValue.FromObject(buffer);
    }

    private static RuntimeValue RandomUUID(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        return RuntimeValue.FromString(Guid.NewGuid().ToString());
    }

    private static RuntimeValue RandomInt(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0)
            throw new Exception("crypto.randomInt requires at least one argument");

        int min, max;

        if (args.Length == 1)
        {
            // randomInt(max) - range is [0, max)
            min = 0;
            max = args[0].IsNumber ? (int)args[0].AsNumberUnsafe() : throw new Exception("crypto.randomInt argument must be a number");
        }
        else
        {
            // randomInt(min, max) - range is [min, max)
            min = args[0].IsNumber ? (int)args[0].AsNumberUnsafe() : throw new Exception("crypto.randomInt min must be a number");
            max = args[1].IsNumber ? (int)args[1].AsNumberUnsafe() : throw new Exception("crypto.randomInt max must be a number");
        }

        return RuntimeValue.FromNumber(RandomNumberGenerator.GetInt32(min, max));
    }

    private static RuntimeValue Pbkdf2Sync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        // pbkdf2Sync(password, salt, iterations, keylen, digest)
        if (args.Length < 5)
            throw new Exception("crypto.pbkdf2Sync requires password, salt, iterations, keylen, and digest arguments");

        var password = ConvertToBytes(args[0].ToObject()) ?? throw new Exception("crypto.pbkdf2Sync requires a password");
        var salt = ConvertToBytes(args[1].ToObject()) ?? throw new Exception("crypto.pbkdf2Sync requires a salt");
        var iterations = args[2].IsNumber ? (int)args[2].AsNumberUnsafe() : throw new Exception("crypto.pbkdf2Sync iterations must be a number");
        var keylen = args[3].IsNumber ? (int)args[3].AsNumberUnsafe() : throw new Exception("crypto.pbkdf2Sync keylen must be a number");
        var digest = args[4].ToObject() as string ?? throw new Exception("crypto.pbkdf2Sync digest must be a string");

        if (iterations < 1)
            throw new Exception("crypto.pbkdf2Sync iterations must be at least 1");
        if (keylen < 0)
            throw new Exception("crypto.pbkdf2Sync keylen must be non-negative");

        var hashAlgorithm = digest.ToLowerInvariant() switch
        {
            "sha1" => HashAlgorithmName.SHA1,
            "sha256" => HashAlgorithmName.SHA256,
            "sha384" => HashAlgorithmName.SHA384,
            "sha512" => HashAlgorithmName.SHA512,
            // Note: MD5 is not supported for PBKDF2 in .NET - use SHA family instead
            _ => throw new Exception($"crypto.pbkdf2Sync: unsupported digest algorithm '{digest}'. Supported: sha1, sha256, sha384, sha512")
        };

        var derivedKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, hashAlgorithm, keylen);
        return RuntimeValue.FromObject(new SharpTSBuffer(derivedKey));
    }

    private static RuntimeValue ScryptSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        // scryptSync(password, salt, keylen[, options])
        if (args.Length < 3)
            throw new Exception("crypto.scryptSync requires password, salt, and keylen arguments");

        var password = ConvertToBytes(args[0].ToObject()) ?? throw new Exception("crypto.scryptSync requires a password");
        var salt = ConvertToBytes(args[1].ToObject()) ?? throw new Exception("crypto.scryptSync requires a salt");
        var keylen = args[2].IsNumber ? (int)args[2].AsNumberUnsafe() : throw new Exception("crypto.scryptSync keylen must be a number");

        if (keylen < 0)
            throw new Exception("crypto.scryptSync keylen must be non-negative");

        // Default scrypt parameters (Node.js defaults)
        int N = 16384;  // cost parameter (must be power of 2)
        int r = 8;      // block size
        int p = 1;      // parallelization

        // Parse options if provided
        if (args.Length > 3 && args[3].ToObject() is SharpTSObject options)
        {
            var fields = options.Fields;
            if (fields.TryGetValue("N", out var costObj) && costObj is double costVal)
                N = (int)costVal;
            if (fields.TryGetValue("cost", out var cost2Obj) && cost2Obj is double cost2Val)
                N = (int)cost2Val;
            if (fields.TryGetValue("r", out var rObj) && rObj is double rVal)
                r = (int)rVal;
            if (fields.TryGetValue("blockSize", out var bsObj) && bsObj is double bsVal)
                r = (int)bsVal;
            if (fields.TryGetValue("p", out var pObj) && pObj is double pVal)
                p = (int)pVal;
            if (fields.TryGetValue("parallelization", out var parObj) && parObj is double parVal)
                p = (int)parVal;
        }

        // Validate N is a power of 2
        if (N < 2 || (N & (N - 1)) != 0)
            throw new Exception("crypto.scryptSync: N must be a power of 2 greater than 1");

        // Use shared scrypt implementation
        var derivedKey = SharpTS.Compilation.ScryptImpl.DeriveBytes(password, salt, N, r, p, keylen);
        return RuntimeValue.FromObject(new SharpTSBuffer(derivedKey));
    }

    private static RuntimeValue TimingSafeEqual(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        // timingSafeEqual(a, b)
        if (args.Length < 2)
            throw new Exception("crypto.timingSafeEqual requires two arguments");

        var a = ConvertToBytes(args[0].ToObject()) ?? throw new Exception("crypto.timingSafeEqual: first argument must be a Buffer or string");
        var b = ConvertToBytes(args[1].ToObject()) ?? throw new Exception("crypto.timingSafeEqual: second argument must be a Buffer or string");

        // Node.js throws if lengths don't match
        if (a.Length != b.Length)
            throw new Exception($"crypto.timingSafeEqual: Input buffers must have the same byte length. Received {a.Length} and {b.Length}");

        // Use .NET's constant-time comparison
        return RuntimeValue.FromBoolean(CryptographicOperations.FixedTimeEquals(a, b));
    }

    /// <summary>
    /// Returns an array of supported hash algorithm names.
    /// </summary>
    private static RuntimeValue GetHashes(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        return RuntimeValue.FromObject(new SharpTSArray(new List<object?> { "md5", "sha1", "sha256", "sha384", "sha512" }));
    }

    /// <summary>
    /// Returns an array of supported cipher algorithm names.
    /// </summary>
    private static RuntimeValue GetCiphers(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        return RuntimeValue.FromObject(new SharpTSArray(new List<object?>
        {
            "aes-128-cbc", "aes-192-cbc", "aes-256-cbc",
            "aes-128-gcm", "aes-192-gcm", "aes-256-gcm"
        }));
    }

    /// <summary>
    /// Generates a key pair synchronously for RSA or EC algorithms.
    /// </summary>
    private static RuntimeValue GenerateKeyPairSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || !args[0].IsString)
            throw new Exception("crypto.generateKeyPairSync requires a key type argument");
        var keyType = args[0].AsStringUnsafe();

        var options = args.Length > 1 ? args[1].ToObject() as SharpTSObject : null;

        return keyType.ToLowerInvariant() switch
        {
            "rsa" => RuntimeValue.FromObject(GenerateRsaKeyPair(options)),
            "ec" => RuntimeValue.FromObject(GenerateEcKeyPair(options)),
            _ => throw new Exception($"crypto.generateKeyPairSync: unsupported key type '{keyType}'")
        };
    }

    private static SharpTSObject GenerateRsaKeyPair(SharpTSObject? options)
    {
        int modulusLength = 2048;
        if (options?.Fields.TryGetValue("modulusLength", out var ml) == true && ml is double d)
            modulusLength = (int)d;

        using var rsa = RSA.Create(modulusLength);
        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["publicKey"] = rsa.ExportSubjectPublicKeyInfoPem(),
            ["privateKey"] = rsa.ExportPkcs8PrivateKeyPem()
        });
    }

    private static SharpTSObject GenerateEcKeyPair(SharpTSObject? options)
    {
        var curveName = "prime256v1";
        if (options?.Fields.TryGetValue("namedCurve", out var nc) == true && nc is string s)
            curveName = s;

        var curve = curveName.ToLowerInvariant() switch
        {
            "prime256v1" or "secp256r1" or "p-256" => ECCurve.NamedCurves.nistP256,
            "secp384r1" or "p-384" => ECCurve.NamedCurves.nistP384,
            "secp521r1" or "p-521" => ECCurve.NamedCurves.nistP521,
            _ => throw new Exception($"crypto.generateKeyPairSync: unsupported curve '{curveName}'")
        };

        using var ecdsa = ECDsa.Create(curve);
        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["publicKey"] = ecdsa.ExportSubjectPublicKeyInfoPem(),
            ["privateKey"] = ecdsa.ExportPkcs8PrivateKeyPem()
        });
    }

    /// <summary>
    /// Creates a Diffie-Hellman key exchange object.
    /// </summary>
    private static RuntimeValue CreateDiffieHellman(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0)
            throw new Exception("crypto.createDiffieHellman requires at least one argument");

        // Check if first arg is a number (prime length) or Buffer/string (prime)
        if (args[0].IsNumber)
        {
            return RuntimeValue.FromObject(new SharpTSDiffieHellman((int)args[0].AsNumberUnsafe()));
        }

        var prime = ConvertToBytes(args[0].ToObject()) ?? throw new Exception("crypto.createDiffieHellman: prime must be a number, Buffer, or string");
        byte[]? generator = null;
        if (args.Length > 1 && !args[1].IsNull)
        {
            generator = ConvertToBytes(args[1].ToObject());
        }

        return RuntimeValue.FromObject(new SharpTSDiffieHellman(prime, generator));
    }

    /// <summary>
    /// Gets a predefined Diffie-Hellman group by name.
    /// </summary>
    private static RuntimeValue GetDiffieHellman(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || !args[0].IsString)
            throw new Exception("crypto.getDiffieHellman requires a group name");
        var groupName = args[0].AsStringUnsafe();

        return RuntimeValue.FromObject(new SharpTSDiffieHellman(groupName, isGroup: true));
    }

    /// <summary>
    /// Creates an Elliptic Curve Diffie-Hellman key exchange object.
    /// </summary>
    private static RuntimeValue CreateECDH(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0 || !args[0].IsString)
            throw new Exception("crypto.createECDH requires a curve name");
        var curveName = args[0].AsStringUnsafe();

        return RuntimeValue.FromObject(new SharpTSECDH(curveName));
    }

    #region RSA Encryption/Decryption

    /// <summary>
    /// Encrypts data using a public key with RSA-OAEP padding (SHA-1 by default, matching Node.js).
    /// </summary>
    private static RuntimeValue PublicEncrypt(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length < 2)
            throw new Exception("crypto.publicEncrypt requires key and buffer arguments");

        var keyPem = ExtractKeyPem(args[0].ToObject());
        var data = ConvertToBytes(args[1].ToObject()) ?? throw new Exception("crypto.publicEncrypt: buffer must be a Buffer or string");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(keyPem);

        // Node.js default is OAEP with SHA-1
        var encrypted = rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA1);
        return RuntimeValue.FromObject(new SharpTSBuffer(encrypted));
    }

    /// <summary>
    /// Decrypts data using a private key with RSA-OAEP padding (SHA-1 by default, matching Node.js).
    /// </summary>
    private static RuntimeValue PrivateDecrypt(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length < 2)
            throw new Exception("crypto.privateDecrypt requires key and buffer arguments");

        var keyPem = ExtractKeyPem(args[0].ToObject());
        var data = ConvertToBytes(args[1].ToObject()) ?? throw new Exception("crypto.privateDecrypt: buffer must be a Buffer or string");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(keyPem);

        // Node.js default is OAEP with SHA-1
        var decrypted = rsa.Decrypt(data, RSAEncryptionPadding.OaepSHA1);
        return RuntimeValue.FromObject(new SharpTSBuffer(decrypted));
    }

    /// <summary>
    /// Encrypts data using a private key with PKCS#1 v1.5 padding (signing primitive).
    /// This is the inverse of publicDecrypt and is used for digital signatures.
    /// </summary>
    private static RuntimeValue PrivateEncrypt(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length < 2)
            throw new Exception("crypto.privateEncrypt requires key and buffer arguments");

        var keyPem = ExtractKeyPem(args[0].ToObject());
        var data = ConvertToBytes(args[1].ToObject()) ?? throw new Exception("crypto.privateEncrypt: buffer must be a Buffer or string");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(keyPem);

        // privateEncrypt uses PKCS#1 v1.5 padding (raw RSA operation with padding)
        // In .NET, we can use Decrypt with Pkcs1 padding as a workaround
        // This performs: result = data^d mod n (the private key operation)
        var encrypted = rsa.Decrypt(data, RSAEncryptionPadding.Pkcs1);
        return RuntimeValue.FromObject(new SharpTSBuffer(encrypted));
    }

    /// <summary>
    /// Decrypts data using a public key with PKCS#1 v1.5 padding (verification primitive).
    /// This is the inverse of privateEncrypt and is used for digital signatures.
    /// </summary>
    private static RuntimeValue PublicDecrypt(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length < 2)
            throw new Exception("crypto.publicDecrypt requires key and buffer arguments");

        var keyPem = ExtractKeyPem(args[0].ToObject());
        var data = ConvertToBytes(args[1].ToObject()) ?? throw new Exception("crypto.publicDecrypt: buffer must be a Buffer or string");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(keyPem);

        // publicDecrypt uses PKCS#1 v1.5 padding (raw RSA operation with padding)
        // In .NET, we can use Encrypt with Pkcs1 padding as a workaround
        // This performs: result = data^e mod n (the public key operation)
        var decrypted = rsa.Encrypt(data, RSAEncryptionPadding.Pkcs1);
        return RuntimeValue.FromObject(new SharpTSBuffer(decrypted));
    }

    /// <summary>
    /// Extracts PEM key string from various input formats.
    /// </summary>
    private static string ExtractKeyPem(object? key)
    {
        return key switch
        {
            string pem => pem,
            SharpTSKeyObject keyObj => keyObj.RsaKey != null
                ? (keyObj.Type == KeyObjectType.Private
                    ? keyObj.RsaKey.ExportPkcs8PrivateKeyPem()
                    : keyObj.RsaKey.ExportSubjectPublicKeyInfoPem())
                : throw new Exception("KeyObject must contain an RSA key"),
            SharpTSObject obj when obj.Fields.TryGetValue("key", out var k) && k is string keyStr => keyStr,
            _ => throw new Exception("Key must be a PEM string, KeyObject, or object with 'key' property")
        };
    }

    #endregion

    #region HKDF Key Derivation

    /// <summary>
    /// Synchronous HKDF key derivation (RFC 5869).
    /// </summary>
    private static RuntimeValue HkdfSync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        // hkdfSync(digest, ikm, salt, info, keylen)
        if (args.Length < 5)
            throw new Exception("crypto.hkdfSync requires digest, ikm, salt, info, and keylen arguments");

        var digest = args[0].ToObject() as string ?? throw new Exception("crypto.hkdfSync: digest must be a string");
        var ikm = ConvertToBytes(args[1].ToObject()) ?? throw new Exception("crypto.hkdfSync: ikm must be a Buffer or string");
        var salt = ConvertToBytes(args[2].ToObject()) ?? []; // Empty salt is valid
        var info = ConvertToBytes(args[3].ToObject()) ?? []; // Empty info is valid
        var keylen = args[4].IsNumber ? (int)args[4].AsNumberUnsafe() : throw new Exception("crypto.hkdfSync: keylen must be a number");

        if (keylen < 0)
            throw new Exception("crypto.hkdfSync: keylen must be non-negative");

        // Handle zero key length specially - .NET doesn't allow 0 but Node.js does
        if (keylen == 0)
            return RuntimeValue.FromObject(new SharpTSBuffer([]));

        var hashAlgorithm = digest.ToLowerInvariant() switch
        {
            "sha1" => HashAlgorithmName.SHA1,
            "sha256" => HashAlgorithmName.SHA256,
            "sha384" => HashAlgorithmName.SHA384,
            "sha512" => HashAlgorithmName.SHA512,
            _ => throw new Exception($"crypto.hkdfSync: unsupported digest algorithm '{digest}'. Supported: sha1, sha256, sha384, sha512")
        };

        var derivedKey = HKDF.DeriveKey(hashAlgorithm, ikm, keylen, salt, info);
        return RuntimeValue.FromObject(new SharpTSBuffer(derivedKey));
    }

    #endregion

    #region KeyObject API

    /// <summary>
    /// Creates a secret (symmetric) KeyObject from a key buffer.
    /// </summary>
    private static RuntimeValue CreateSecretKey(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0)
            throw new Exception("crypto.createSecretKey requires a key argument");

        byte[] keyBytes;

        if (args[0].IsString)
        {
            var keyStr = args[0].AsStringUnsafe();
            // If encoding is provided, use it; otherwise default to utf8
            var encoding = args.Length > 1 && args[1].IsString ? args[1].AsStringUnsafe() : "utf8";
            keyBytes = encoding.ToLowerInvariant() switch
            {
                "utf8" or "utf-8" => System.Text.Encoding.UTF8.GetBytes(keyStr),
                "hex" => Convert.FromHexString(keyStr),
                "base64" => Convert.FromBase64String(keyStr),
                "latin1" or "binary" => System.Text.Encoding.Latin1.GetBytes(keyStr),
                _ => throw new Exception($"crypto.createSecretKey: unsupported encoding '{encoding}'")
            };
        }
        else
        {
            keyBytes = ConvertToBytes(args[0].ToObject()) ?? throw new Exception("crypto.createSecretKey: key must be a Buffer or string");
        }

        return RuntimeValue.FromObject(new SharpTSKeyObject(keyBytes));
    }

    /// <summary>
    /// Creates a public KeyObject from a PEM-encoded public key.
    /// </summary>
    private static RuntimeValue CreatePublicKey(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0)
            throw new Exception("crypto.createPublicKey requires a key argument");

        string pem;

        if (args[0].IsString)
        {
            pem = args[0].AsStringUnsafe();
        }
        else if (args[0].ToObject() is SharpTSObject obj && obj.Fields.TryGetValue("key", out var keyVal) && keyVal is string keyPem)
        {
            pem = keyPem;
        }
        else if (args[0].ToObject() is SharpTSBuffer buf)
        {
            // PEM as buffer
            pem = System.Text.Encoding.UTF8.GetString(buf.Data);
        }
        else
        {
            throw new Exception("crypto.createPublicKey: key must be a PEM string, Buffer, or object with 'key' property");
        }

        return RuntimeValue.FromObject(SharpTSKeyObject.CreatePublicKey(pem));
    }

    /// <summary>
    /// Creates a private KeyObject from a PEM-encoded private key.
    /// </summary>
    private static RuntimeValue CreatePrivateKey(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        if (args.Length == 0)
            throw new Exception("crypto.createPrivateKey requires a key argument");

        string pem;

        if (args[0].IsString)
        {
            pem = args[0].AsStringUnsafe();
        }
        else if (args[0].ToObject() is SharpTSObject obj && obj.Fields.TryGetValue("key", out var keyVal) && keyVal is string keyPem)
        {
            pem = keyPem;
        }
        else if (args[0].ToObject() is SharpTSBuffer buf)
        {
            // PEM as buffer
            pem = System.Text.Encoding.UTF8.GetString(buf.Data);
        }
        else
        {
            throw new Exception("crypto.createPrivateKey: key must be a PEM string, Buffer, or object with 'key' property");
        }

        return RuntimeValue.FromObject(SharpTSKeyObject.CreatePrivateKey(pem));
    }

    #endregion

    #region Async (Callback-based) Key Derivation

    /// <summary>
    /// Extracts the callback function from the last argument.
    /// </summary>
    private static ISharpTSCallable GetCallback(ReadOnlySpan<RuntimeValue> args)
    {
        var callback = args[^1].ToObject() as ISharpTSCallable
            ?? throw new Exception("Runtime Error: callback is required");
        return callback;
    }

    /// <summary>
    /// Schedules an async callback on the interpreter's event loop and decrements the handle count.
    /// </summary>
    private static void ScheduleCallbackAndUnref(Interp interpreter, ISharpTSCallable callback, object? error, object? result)
    {
        interpreter.ScheduleTimer(0, 0, () =>
        {
            try
            {
                interpreter.InvokeGuestCallback(callback, [error, result]);
            }
            finally
            {
                interpreter.Unref();
            }
        }, isInterval: false);
    }

    /// <summary>
    /// Creates a Node.js-style error object for async callbacks.
    /// </summary>
    private static SharpTSObject CreateCryptoError(Exception ex, string method)
    {
        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["code"] = "ERR_CRYPTO_INVALID_STATE",
            ["message"] = $"crypto.{method}: {ex.Message}"
        });
    }

    /// <summary>
    /// crypto.pbkdf2(password, salt, iterations, keylen, digest, callback)
    /// </summary>
    private static RuntimeValue Pbkdf2Async(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var callback = GetCallback(args);
        var password = ConvertToBytes(args[0].ToObject());
        var salt = ConvertToBytes(args[1].ToObject());
        var iterations = args[2].IsNumber ? (int)args[2].AsNumberUnsafe() : 0;
        var keylen = args[3].IsNumber ? (int)args[3].AsNumberUnsafe() : 0;
        var digest = args[4].ToObject() as string;

        interpreter.Ref(); // Keep event loop alive until callback fires
        _ = Task.Run(() =>
        {
            try
            {
                if (password == null) throw new Exception("password is required");
                if (salt == null) throw new Exception("salt is required");
                if (digest == null) throw new Exception("digest must be a string");
                if (iterations < 1) throw new Exception("iterations must be at least 1");

                var hashAlgorithm = digest.ToLowerInvariant() switch
                {
                    "sha1" => HashAlgorithmName.SHA1,
                    "sha256" => HashAlgorithmName.SHA256,
                    "sha384" => HashAlgorithmName.SHA384,
                    "sha512" => HashAlgorithmName.SHA512,
                    _ => throw new Exception($"unsupported digest algorithm '{digest}'")
                };

                var derivedKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, hashAlgorithm, keylen);
                ScheduleCallbackAndUnref(interpreter, callback, null, new SharpTSBuffer(derivedKey));
            }
            catch (Exception ex)
            {
                ScheduleCallbackAndUnref(interpreter, callback, CreateCryptoError(ex, "pbkdf2"), null);
            }
        });

        return RuntimeValue.Undefined;
    }

    /// <summary>
    /// crypto.scrypt(password, salt, keylen[, options], callback)
    /// </summary>
    private static RuntimeValue ScryptAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var callback = GetCallback(args);
        var password = ConvertToBytes(args[0].ToObject());
        var salt = ConvertToBytes(args[1].ToObject());
        var keylen = args[2].IsNumber ? (int)args[2].AsNumberUnsafe() : 0;

        // Options are between keylen and callback
        SharpTSObject? options = null;
        if (args.Length > 4 && args[3].ToObject() is SharpTSObject opt)
            options = opt;

        interpreter.Ref();
        _ = Task.Run(() =>
        {
            try
            {
                if (password == null) throw new Exception("password is required");
                if (salt == null) throw new Exception("salt is required");

                int N = 16384, r = 8, p = 1;
                if (options != null)
                {
                    var fields = options.Fields;
                    if (fields.TryGetValue("N", out var costObj) && costObj is double costVal) N = (int)costVal;
                    if (fields.TryGetValue("cost", out var cost2Obj) && cost2Obj is double cost2Val) N = (int)cost2Val;
                    if (fields.TryGetValue("r", out var rObj) && rObj is double rVal) r = (int)rVal;
                    if (fields.TryGetValue("blockSize", out var bsObj) && bsObj is double bsVal) r = (int)bsVal;
                    if (fields.TryGetValue("p", out var pObj) && pObj is double pVal) p = (int)pVal;
                    if (fields.TryGetValue("parallelization", out var parObj) && parObj is double parVal) p = (int)parVal;
                }

                if (N < 2 || (N & (N - 1)) != 0)
                    throw new Exception("N must be a power of 2 greater than 1");

                var derivedKey = SharpTS.Compilation.ScryptImpl.DeriveBytes(password, salt, N, r, p, keylen);
                ScheduleCallbackAndUnref(interpreter, callback, null, new SharpTSBuffer(derivedKey));
            }
            catch (Exception ex)
            {
                ScheduleCallbackAndUnref(interpreter, callback, CreateCryptoError(ex, "scrypt"), null);
            }
        });

        return RuntimeValue.Undefined;
    }

    /// <summary>
    /// crypto.generateKeyPair(type[, options], callback)
    /// </summary>
    private static RuntimeValue GenerateKeyPairAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var callback = GetCallback(args);
        var keyType = args[0].ToObject() as string;
        SharpTSObject? options = null;
        if (args.Length > 2 && args[1].ToObject() is SharpTSObject opt)
            options = opt;

        interpreter.Ref();
        _ = Task.Run(() =>
        {
            try
            {
                if (keyType == null) throw new Exception("key type is required");
                var result = keyType.ToLowerInvariant() switch
                {
                    "rsa" => GenerateRsaKeyPair(options),
                    "ec" => GenerateEcKeyPair(options),
                    _ => throw new Exception($"unsupported key type '{keyType}'")
                };
                // Node.js generateKeyPair callback is (err, publicKey, privateKey)
                interpreter.ScheduleTimer(0, 0, () =>
                {
                    callback.Call(interpreter, [null, result.GetProperty("publicKey"), result.GetProperty("privateKey")]);
                    interpreter.Unref();
                }, isInterval: false);
            }
            catch (Exception ex)
            {
                ScheduleCallbackAndUnref(interpreter, callback, CreateCryptoError(ex, "generateKeyPair"), null);
            }
        });

        return RuntimeValue.Undefined;
    }

    /// <summary>
    /// crypto.hkdf(digest, ikm, salt, info, keylen, callback)
    /// </summary>
    private static RuntimeValue HkdfAsync(Interp interpreter, RuntimeValue receiver, ReadOnlySpan<RuntimeValue> args)
    {
        var callback = GetCallback(args);
        var digest = args[0].ToObject() as string;
        var ikm = ConvertToBytes(args[1].ToObject());
        var salt = ConvertToBytes(args[2].ToObject()) ?? [];
        var info = ConvertToBytes(args[3].ToObject()) ?? [];
        var keylen = args[4].IsNumber ? (int)args[4].AsNumberUnsafe() : 0;

        interpreter.Ref();
        _ = Task.Run(() =>
        {
            try
            {
                if (digest == null) throw new Exception("digest must be a string");
                if (ikm == null) throw new Exception("ikm must be a Buffer or string");

                if (keylen == 0)
                {
                    ScheduleCallbackAndUnref(interpreter, callback, null, new SharpTSBuffer([]));
                    return;
                }

                var hashAlgorithm = digest.ToLowerInvariant() switch
                {
                    "sha1" => HashAlgorithmName.SHA1,
                    "sha256" => HashAlgorithmName.SHA256,
                    "sha384" => HashAlgorithmName.SHA384,
                    "sha512" => HashAlgorithmName.SHA512,
                    _ => throw new Exception($"unsupported digest algorithm '{digest}'")
                };

                var derivedKey = HKDF.DeriveKey(hashAlgorithm, ikm, keylen, salt, info);
                ScheduleCallbackAndUnref(interpreter, callback, null, new SharpTSBuffer(derivedKey));
            }
            catch (Exception ex)
            {
                ScheduleCallbackAndUnref(interpreter, callback, CreateCryptoError(ex, "hkdf"), null);
            }
        });

        return RuntimeValue.Undefined;
    }

    #endregion
}
