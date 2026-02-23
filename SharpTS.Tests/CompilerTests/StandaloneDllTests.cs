using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using SharpTS.Compilation;
using SharpTS.Modules;
using SharpTS.Parsing;
using SharpTS.TypeSystem;
using Xunit;

namespace SharpTS.Tests.CompilerTests;

/// <summary>
/// Tests that ensure compiled DLLs remain standalone (no SharpTS.dll dependency).
/// </summary>
public class StandaloneDllTests
{
    // Allowlist of files that contain intentional SharpTS late-binding patterns.
    // These use graceful fallback: emit tries emitted types first, falls back to
    // interpreter types via Type.GetType() if available. This allows compiled code
    // to work standalone while maintaining compatibility when running with SharpTS.dll.
    //
    // Note: These patterns do NOT create hard dependencies - they're runtime lookups
    // that return null if SharpTS.dll is absent. The compiled code works regardless.
    private static readonly HashSet<string> LateBindingAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compilation/ConstantFolder.cs",              // SharpTSUndefined detection
        "Compilation/PropertyDescriptorStore.cs",     // SharpTSPropertyDescriptor/Object fallback
        "Compilation/RuntimeEmitter.Worker.cs",       // TypedArray/Worker/Atomics interpreter fallback
        "Compilation/RuntimeEmitter.Proxy.cs",        // Proxy CreateProxy/CreateRevocableProxy (requires SharpTS.dll at runtime)
        "Compilation/RuntimeEmitter.Intl.cs",         // Intl.NumberFormat runtime dispatch via RuntimeTypes
        "Compilation/RuntimeEmitter.AbortController.cs", // AbortSignal.any() via RuntimeTypes.AbortSignalAnyCompiled
    };

    /// <summary>
    /// Scans Compilation/ source files to ensure no direct typeof() references to
    /// SharpTS types that would embed assembly references in emitted IL.
    ///
    /// WRONG: typeof(RuntimeTypes).GetMethod(...) - embeds SharpTS.dll reference
    /// RIGHT: EmitReflectionCall(il, "SharpTS.Compilation.RuntimeTypes, SharpTS", ...) - runtime lookup
    /// </summary>
    [Fact]
    public void CompilationFiles_ShouldNotUseTypeofForEmittedIL()
    {
        var repoRoot = FindRepoRoot();
        var compilationDir = Path.Combine(repoRoot, "Compilation");
        var violations = new List<string>();

        // Types that must NOT be referenced via typeof() in emitted IL
        var forbiddenPatterns = new[]
        {
            "typeof(RuntimeTypes)",
            "typeof(PropertyDescriptorStore)",
            "typeof(ObjectBuiltIns)",
        };

        foreach (var file in Directory.GetFiles(compilationDir, "*.cs", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);

            // Skip the RuntimeTypes files themselves - they define the types, not emit IL referencing them
            if (fileName.StartsWith("RuntimeTypes."))
                continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // Skip comments
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*"))
                    continue;

                foreach (var pattern in forbiddenPatterns)
                {
                    if (line.Contains(pattern))
                    {
                        violations.Add($"{fileName}:{i + 1}: {trimmed}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} typeof() references that create SharpTS.dll dependencies in emitted IL.\n" +
            $"Use EmitReflectionCall/EmitReflectionCallVoid instead.\n\n" +
            string.Join("\n", violations.Take(20)));
    }

    [Fact]
    public void CompilationFiles_ShouldNotIntroduceNewSharpTsLateBindingOutsideAllowlist()
    {
        var repoRoot = FindRepoRoot();
        var compilationDir = Path.Combine(repoRoot, "Compilation");
        var violations = new List<string>();

        // Any of these patterns indicates runtime coupling to SharpTS via late binding.
        var pattern = new Regex(
            "Type\\.GetType\\(\"SharpTS\\.|\"[^\"]+,\\s*SharpTS\"",
            RegexOptions.Compiled);

        foreach (var file in Directory.GetFiles(compilationDir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (LateBindingAllowlist.Contains(relative))
                continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*"))
                    continue;

                if (pattern.IsMatch(lines[i]))
                {
                    violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Found SharpTS late-binding patterns outside the current allowlist.\n" +
            "Either migrate those call sites to emitted runtime types or explicitly add a temporary allowlist entry.\n\n" +
            string.Join("\n", violations.Take(50)));
    }

    [Fact]
    public void CompiledDll_ShouldNotReferenceSharpTsAssembly()
    {
        var source = """
            const obj = { a: 1 };
            console.log(obj.a);
            """;

        var (tempDir, dllPath) = CompileStandalone(source);
        try
        {
            var refs = GetAssemblyReferences(dllPath);
            Assert.DoesNotContain(refs, r => r == "SharpTS");
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_HttpCreateServer_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as http from "http";
                const server = http.createServer((req: any, res: any) => { res.end("ok"); });
                console.log(typeof server);
                server.close();
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("object\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_Atomics_ShouldExecuteWithoutSharpTsDll()
    {
        var source = """
            const view = new Int32Array(1);
            Atomics.store(view, 0, 123);
            console.log(Atomics.load(view, 0));
            """;

        var (tempDir, dllPath) = CompileStandalone(source);
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("123\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_DataView_ShouldExecuteWithoutSharpTsDll()
    {
        var source = """
            const ab = new ArrayBuffer(8);
            const dv = new DataView(ab);
            dv.setUint32(0, 0xffffffff);
            console.log(dv.getUint32(0));
            """;

        var (tempDir, dllPath) = CompileStandalone(source);
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("4294967295\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_CryptoSignVerify_ShouldExecuteWithoutSharpTsDll()
    {
        // Use a valid RSA private key (same as in CryptoSignVerifyTests)
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as crypto from "crypto";
                const privateKey = `-----BEGIN PRIVATE KEY-----
                MIIEvAIBADANBgkqhkiG9w0BAQEFAASCBKYwggSiAgEAAoIBAQDQ9wlSXsD5OUJ3
                vIhuDuW1J6YN5dvbxAxqZRe4Cm2BXbY1l+tC//x9iZKrkKBbtsBjl3U2WXpwpcWf
                rpqdMrQmxeb1gVHwHUrOamdO5+SG8Zy0ojKWxPUIkahHA5wRacsApLetHAS5N5US
                vlzymEhs/jfhGpcl21fAH72kJuxoRF19tNWACSSb+DpeOHXWHm/PqenFI1ZShK+X
                qkxeFBN8hHUUhhTxdd+LH7n9ImhhDxb6y9Nlio5lZrfHzPLtZ/EHTIyaxV80wHZ0
                H4FER0kybl5Jso0VYBKMEZkWrOto9LlN/EmXmCdkJIZhGjVR/noH1v2jC/xDZ5n9
                8khHsi/lAgMBAAECggEAEC9SFYMpRyRcNZHwrzWQLRvJDMKE6NyiaYsy7xo/qQlt
                F3GQ0zuofsCtD4TAJtpcxFnyxibgCOGOEPQhHZPTyD0DyngdtI9QP/SV09K6LImC
                LatyZ6MRp3xAoF9zMxYSlxYq88l7xCy96xm7cT7CPU7jXRgGJPR8M3FB6vjozpp5
                GYfByegTEzyBiZ689H9Q2syhU+F9MJnQzZsyJLZ1RsLcKOnqCifhWWiDMjqt+EKU
                WQc72dxaveekhp/ISNzo0iCMGCxDi9i2ModAg9wURB7aB7MTIiChzHEQ6hmSeXy0
                feiTLTsvG7e99uSiV/GBV2FtTQ+kqpaUQtZ8uezggQKBgQDvjp7UtrjdNJBQmRPP
                fbGg8uwXoKpVkeZt1F1u0TPJx6UPYqwMQcHGJCVaHhUQ8sI8Sppf+ySc3onR9ix0
                Oai7CGR3hM/Nhy8bf+0h8qUbuSuItlrF+J7lwsLymlMQkN/X3w/bnudN7sLSjTsu
                oKB8rbLI2q62lalBVEAHtBIozQKBgQDfTt3A9GP/0r/UexfPiaJmqTl6KSwWos/Q
                A6dBNhmugwmdLtJru+mVZ0mgFgfiepQJG1W38ynL4/GNv6g3QYFlP/9fFfDxfvRN
                rYRTDtRnKNBXxE712pkInzPAWYcDpEcDMVdLLqLppxDslWeJlr71cJiSdp1WG7n3
                WxVTUSyDeQKBgEsnIwz4heZfpyah32UouaEUlJyU+tr9epzaErXBS83xpAa/ndn6
                hx/yFwW+ij1W6zie7u9Nip7r8bC82hVcQWLrrxkPwWFpF445A9uyk7muzcmF69RP
                uwm5oA8b+xMnYBIJGKB9qXL5hIUpaXenTLHQjFYWxNji+sZT+AJyq3/BAoGAN3qG
                iVuuRG59jjKOtdcB6/N6/iigdXc5nfpqYT8pnjub9dseF/n1jFK+7fDLQK8nfCO4
                Zh0ZczhMWOUWy7OQjDEcJulylOzvkSTczS3QA1kWedehrl8CyiuTVeRoMLVtlxN5
                FoqdmuMQx1ZPBNXY122D2k9xw2TcDOIqKCrwnjECgYAv24vgAfSQRZLd3MrK41El
                xGWRu1CAYmdC2UXV92cJpGAB4irVxs+E7u9qTu1zqZA5ZzbRzz1uxgnhKenC62uh
                cWwcgaiz9LOlCil1gb3bazz0V6HiWrmi++soWhPPNMYSgE002KFHq58G2e1nwBmU
                pYlA6+GlII/hM4c3iRjCyQ==
                -----END PRIVATE KEY-----`;
                const sign = crypto.createSign("sha256");
                sign.update("hello");
                const sig = sign.sign(privateKey, "hex");
                console.log(typeof sig);
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 20000);
            Assert.Equal("string\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_FsBasic_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as fs from "fs";
                console.log(fs.existsSync("missing_file_for_guard.txt"));
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("false\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Phase 23 guardrail: Verifies TypedArray operations work standalone.
    /// </summary>
    [Fact]
    public void Isolated_TypedArray_ShouldExecuteWithoutSharpTsDll()
    {
        var source = """
            const i8 = new Int8Array(4);
            i8[0] = 127;
            i8[1] = -128;
            const f32 = new Float32Array(2);
            f32[0] = 3.14;
            console.log(i8[0], i8[1], i8.length, f32[0].toFixed(2));
            """;

        var (tempDir, dllPath) = CompileStandalone(source);
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("127 -128 4 3.14\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Phase 23 guardrail: Verifies SharedArrayBuffer works standalone.
    /// </summary>
    [Fact]
    public void Isolated_SharedArrayBuffer_ShouldExecuteWithoutSharpTsDll()
    {
        var source = """
            const sab = new SharedArrayBuffer(16);
            const view = new Int32Array(sab);
            view[0] = 42;
            console.log(view[0], sab.byteLength);
            """;

        var (tempDir, dllPath) = CompileStandalone(source);
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("42 16\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Phase 23 guardrail: Verifies util module works standalone.
    /// </summary>
    [Fact]
    public void Isolated_UtilModule_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as util from "util";
                console.log(util.format("Hello %s", "World"));
                console.log(util.types.isDate(new Date()));
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("Hello World\ntrue\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Phase 23 guardrail: Verifies path module works standalone.
    /// </summary>
    [Fact]
    public void Isolated_PathModule_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as path from "path";
                const p = path.join("a", "b", "c");
                console.log(path.basename(p));
                console.log(path.extname("file.txt"));
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("c\n.txt\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Phase 23 guardrail: Verifies crypto hash operations work standalone.
    /// </summary>
    [Fact]
    public void Isolated_CryptoHash_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as crypto from "crypto";
                const hash = crypto.createHash("sha256");
                hash.update("hello");
                const digest = hash.digest("hex");
                console.log(digest.substring(0, 8));
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            // SHA256 of "hello" starts with "2cf24dba"
            Assert.Equal("2cf24dba\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Phase 23 guardrail: Scans compiled DLL for forbidden SharpTS late-binding strings.
    /// These strings should NOT appear in standalone output as they indicate runtime dependency.
    /// </summary>
    [Fact]
    public void CompiledDll_ShouldNotContainForbiddenSharpTsStrings()
    {
        var source = """
            // Test various features that historically had SharpTS dependencies
            const arr = [1, 2, 3];
            const obj = { a: 1, b: 2 };
            const buf = new ArrayBuffer(8);
            const view = new Int32Array(buf);
            view[0] = 42;
            console.log(arr.length, Object.keys(obj).length, view[0]);
            """;

        var (tempDir, dllPath) = CompileStandalone(source);
        try
        {
            // Read the DLL as binary and scan for forbidden strings
            var dllBytes = File.ReadAllBytes(dllPath);
            var dllContent = System.Text.Encoding.UTF8.GetString(dllBytes);

            // Patterns that indicate SharpTS runtime coupling
            var forbiddenPatterns = new[]
            {
                "SharpTS.Runtime.Types.SharpTSArray, SharpTS",
                "SharpTS.Runtime.Types.SharpTSObject, SharpTS",
                "SharpTS.Runtime.Types.SharpTSBuffer, SharpTS",
                "SharpTS.Runtime.Types.SharpTSArrayBuffer, SharpTS",
                "SharpTS.Runtime.Types.SharpTSInt32Array, SharpTS",
                "SharpTS.Runtime.Types.SharpTSUndefined, SharpTS",
                "SharpTS.Compilation.PropertyDescriptorStore, SharpTS",
                "SharpTS.Runtime.BuiltIns.ObjectBuiltIns, SharpTS",
            };

            var foundPatterns = forbiddenPatterns
                .Where(p => dllContent.Contains(p))
                .ToList();

            Assert.True(
                foundPatterns.Count == 0,
                $"Compiled DLL contains forbidden SharpTS late-binding strings:\n" +
                string.Join("\n", foundPatterns.Select(p => $"  - {p}")));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Phase 23 guardrail: Comprehensive test of core standalone features.
    /// Covers objects, arrays, typed arrays, string operations, and more.
    /// </summary>
    [Fact]
    public void Isolated_Comprehensive_ShouldExecuteWithoutSharpTsDll()
    {
        var source = """
            // Object operations
            const obj = { a: 1, b: 2 };
            const keys = Object.keys(obj);
            const values = Object.values(obj);

            // Array operations
            const arr = [1, 2, 3, 4, 5];
            const mapped = arr.map(x => x * 2);
            const filtered = arr.filter(x => x > 2);

            // TypedArray operations
            const ta = new Int32Array(3);
            ta[0] = 10; ta[1] = 20; ta[2] = 30;

            // String operations
            const str = "hello world";
            const upper = str.toUpperCase();

            // Math operations
            const max = Math.max(1, 5, 3);

            // Date operations
            const d = new Date(0);
            const year = d.getFullYear();

            console.log(keys.length, values.length);
            console.log(mapped[0], filtered.length);
            console.log(ta.length, ta[1]);
            console.log(upper.substring(0, 5));
            console.log(max, year);
            """;

        var (tempDir, dllPath) = CompileStandalone(source);
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(5, lines.Length);
            Assert.Equal("2 2", lines[0]);
            Assert.Equal("2 3", lines[1]);
            Assert.Equal("3 20", lines[2]);
            Assert.Equal("HELLO", lines[3]);
            // Year could be 1969 or 1970 depending on timezone
            Assert.StartsWith("5 19", lines[4]);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_AssertModule_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { ok, strictEqual, deepStrictEqual } from 'assert';
                ok(true);
                strictEqual(42, 42);
                deepStrictEqual([1, 2], [1, 2]);
                console.log('assertions passed');
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("assertions passed\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_BufferModule_ShouldExecuteWithoutSharpTsDll()
    {
        var source = """
            const buf = Buffer.from('hello');
            console.log(buf.toString());
            console.log(buf.length);
            """;

        var (tempDir, dllPath) = CompileStandalone(source);
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("hello\n5\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_EventsModule_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { EventEmitter } from 'events';
                const emitter = new EventEmitter();
                let count = 0;
                emitter.on('test', () => { count++; });
                emitter.emit('test');
                emitter.emit('test');
                console.log(count);
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("2\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_OsModule_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as os from 'os';
                console.log(typeof os.platform());
                console.log(os.homedir().length > 0);
                console.log(typeof os.tmpdir());
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("string\ntrue\nstring\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_QuerystringModule_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { parse, stringify } from 'querystring';
                const parsed = parse('foo=bar&baz=qux');
                console.log(parsed.foo);
                console.log(stringify({ a: '1', b: '2' }));
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("bar\na=1&b=2\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_StreamModule_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { PassThrough } from 'stream';
                const pt = new PassThrough();
                pt.write('hello');
                pt.end();
                const data = pt.read();
                console.log(data);
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("hello\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_StringDecoderModule_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { StringDecoder } from 'string_decoder';
                const decoder = new StringDecoder('utf8');
                const result = decoder.write(Buffer.from('hello'));
                console.log(result);
                console.log(decoder.encoding);
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("hello\nutf8\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_TimersModule_ShouldExecuteWithoutSharpTsDll()
    {
        var source = """
            let called = false;
            const handle = setTimeout(() => { called = true; }, 10);
            clearTimeout(handle);
            console.log(called);
            console.log(typeof handle);
            """;

        var (tempDir, dllPath) = CompileStandalone(source);
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("false\nobject\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_UrlModule_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { parse, format } from 'url';
                const parsed = parse('https://example.com:8080/path?key=value');
                console.log(parsed.hostname);
                console.log(parsed.port);
                console.log(parsed.pathname);
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("example.com\n8080\n/path\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_ZlibModule_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as zlib from 'zlib';
                const input = Buffer.from('hello world');
                const compressed = zlib.deflateSync(input);
                const decompressed = zlib.inflateSync(compressed);
                console.log(decompressed.toString());
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("hello world\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_DnsModule_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { lookup } from 'dns';
                const result = lookup('localhost');
                console.log(typeof result.address);
                console.log(result.family === 4 || result.family === 6);
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("string\ntrue\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_PerfHooksModule_ShouldExecuteWithoutSharpTsDll()
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { performance } from 'perf_hooks';
                const start = performance.now();
                let sum = 0;
                for (let i = 0; i < 1000; i++) sum += i;
                const elapsed = performance.now() - start;
                console.log(elapsed >= 0);
                console.log(typeof elapsed);
                """
        };

        var (tempDir, dllPath) = CompileStandaloneModule(files, "main.ts");
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("true\nnumber\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Isolated_ProcessModule_ShouldExecuteWithoutSharpTsDll()
    {
        var source = """
            console.log(typeof process.platform);
            console.log(typeof process.pid);
            console.log(process.cwd().length > 0);
            """;

        var (tempDir, dllPath) = CompileStandalone(source);
        try
        {
            var output = ExecuteCompiledDllIsolated(dllPath, timeoutMs: 15000);
            Assert.Equal("string\nnumber\ntrue\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    private static (string tempDir, string dllPath) CompileStandalone(string source)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sharpts_standalone_guard_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        var dllPath = Path.Combine(tempDir, "standalone_test.dll");

        var lexer = new Lexer(source);
        var tokens = lexer.ScanTokens();
        var parser = new Parser(tokens);
        var statements = parser.ParseOrThrow();

        var checker = new TypeChecker();
        var typeMap = checker.Check(statements);
        var deadCodeInfo = new DeadCodeAnalyzer(typeMap).Analyze(statements);

        var compiler = new ILCompiler("standalone_test");
        compiler.Compile(statements, typeMap, deadCodeInfo);
        compiler.Save(dllPath);

        File.WriteAllText(
            Path.Combine(tempDir, "standalone_test.runtimeconfig.json"),
            """
            {
              "runtimeOptions": {
                "tfm": "net10.0",
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "10.0.0"
                }
              }
            }
            """);

        return (tempDir, dllPath);
    }

    private static (string tempDir, string dllPath) CompileStandaloneModule(
        Dictionary<string, string> files,
        string entryPoint)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sharpts_standalone_guard_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        var dllPath = Path.Combine(tempDir, "standalone_test.dll");

        foreach (var (path, content) in files)
        {
            var fullPath = Path.Combine(tempDir, path);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, content);
        }

        var entryPath = Path.Combine(tempDir, entryPoint);
        var resolver = new ModuleResolver(entryPath);
        var entryModule = resolver.LoadModule(entryPath);
        var modules = resolver.GetModulesInOrder(entryModule);

        var checker = new TypeChecker();
        var typeMap = checker.CheckModules(modules, resolver);
        var deadCodeInfo = new DeadCodeAnalyzer(typeMap).Analyze(modules.SelectMany(m => m.Statements).ToList());

        var compiler = new ILCompiler("standalone_test");
        compiler.CompileModules(modules, resolver, typeMap, deadCodeInfo);
        compiler.Save(dllPath);

        File.WriteAllText(
            Path.Combine(tempDir, "standalone_test.runtimeconfig.json"),
            """
            {
              "runtimeOptions": {
                "tfm": "net10.0",
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "10.0.0"
                }
              }
            }
            """);

        return (tempDir, dllPath);
    }

    private static string ExecuteCompiledDllIsolated(string dllPath, int timeoutMs)
    {
        var workingDir = Path.GetDirectoryName(dllPath)!;
        var psi = new ProcessStartInfo("dotnet", dllPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir
        };

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill();
            throw new TimeoutException("Compiled standalone probe timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new Exception(
                $"Compiled standalone probe exited with code {process.ExitCode}. Stderr: {error}");
        }

        return output.Replace("\r\n", "\n");
    }

    private static List<string> GetAssemblyReferences(string dllPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        var refs = new List<string>();
        foreach (var refHandle in metadataReader.AssemblyReferences)
        {
            var reference = metadataReader.GetAssemblyReference(refHandle);
            refs.Add(metadataReader.GetString(reference.Name));
        }
        return refs;
    }

    private static void CleanupTempDir(string tempDir)
    {
        try
        {
            Directory.Delete(tempDir, true);
        }
        catch
        {
            // Ignore cleanup failures in tests.
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "Compilation")) &&
                File.Exists(Path.Combine(dir, "SharpTS.csproj")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find repository root");
    }
}
