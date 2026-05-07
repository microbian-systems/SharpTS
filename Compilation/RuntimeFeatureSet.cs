namespace SharpTS.Compilation;

/// <summary>
/// Records which categories of runtime helper types the compiled program needs,
/// so <see cref="RuntimeEmitter.EmitAll"/> can skip emitting unused machinery.
///
/// Default constructor sets every flag to <c>true</c> — i.e., "emit everything,"
/// matching pre-tree-shaking behavior. Callers that have run
/// <see cref="RuntimeFeatureDetector"/> against the AST get a set with most flags
/// flipped to <c>false</c>, and only the actually-used categories <c>true</c>.
///
/// Phase 1 covers Tier A categories from <c>docs/plans/runtime-tree-shaking.md</c>:
/// network/HTTP/TLS/DNS/dgram/cluster/fs/streams/crypto/zlib/typed-arrays/etc.
/// Tier B (Promise, RegExp, Date, Map/Set, iterator helpers) is not gated yet —
/// those flags don't exist on this set.
/// </summary>
public sealed class RuntimeFeatureSet
{
    // ── Network family ────────────────────────────────────────────────────
    public bool UsesNet { get; set; } = true;       // 'net' module / NetServer / NetSocket
    public bool UsesHttp { get; set; } = true;      // 'http'/'https' module / HttpServer
    public bool UsesTls { get; set; } = true;       // 'tls' module / TLSSocket
    public bool UsesDgram { get; set; } = true;     // 'dgram' module
    public bool UsesDns { get; set; } = true;       // 'dns'/'dns/promises'
    public bool UsesFetch { get; set; } = true;     // fetch() / Headers / Request / Response

    // ── Storage / I/O family ──────────────────────────────────────────────
    public bool UsesFs { get; set; } = true;        // 'fs'/'fs/promises'
    public bool UsesCrypto { get; set; } = true;    // 'crypto'/'crypto/promises'
    public bool UsesZlib { get; set; } = true;      // 'zlib'

    // ── Stream APIs ───────────────────────────────────────────────────────
    public bool UsesNodeStreams { get; set; } = true;  // 'stream'/'stream/promises'
    public bool UsesWebStreams { get; set; } = true;   // ReadableStream / WritableStream / TransformStream

    // ── Worker / multi-process ────────────────────────────────────────────
    public bool UsesCluster { get; set; } = true;       // 'cluster'
    public bool UsesBroadcastChannel { get; set; } = true;
    public bool UsesAsyncLocalStorage { get; set; } = true;

    // ── Misc emitted-runtime types ────────────────────────────────────────
    public bool UsesReadline { get; set; } = true;          // 'readline'
    public bool UsesUtilPromisify { get; set; } = true;     // util.promisify / util.callbackify / util.deprecate
    public bool UsesTextEncoding { get; set; } = true;      // TextEncoder / TextDecoder
    public bool UsesFinalizationRegistry { get; set; } = true;
    public bool UsesReflectMetadata { get; set; } = true;   // Reflect.metadata / Reflect.defineMetadata
    public bool UsesCjsRequire { get; set; } = true;        // require() / module.exports

    // ── Typed arrays ──────────────────────────────────────────────────────
    /// <summary>
    /// Bitset of typed-array kinds the program references. A test using only
    /// <c>Float32Array</c> shouldn't drag in <c>$BigInt64Array</c>, etc.
    /// </summary>
    public TypedArrayKinds TypedArrays { get; set; } = TypedArrayKinds.All;

    [Flags]
    public enum TypedArrayKinds
    {
        None = 0,
        Int8 = 1 << 0,
        Uint8 = 1 << 1,
        Uint8Clamped = 1 << 2,
        Int16 = 1 << 3,
        Uint16 = 1 << 4,
        Int32 = 1 << 5,
        Uint32 = 1 << 6,
        Float32 = 1 << 7,
        Float64 = 1 << 8,
        BigInt64 = 1 << 9,
        BigUint64 = 1 << 10,
        ArrayBuffer = 1 << 11,
        SharedArrayBuffer = 1 << 12,
        DataView = 1 << 13,
        TypedArrayBase = 1 << 14, // emitted whenever any concrete typed-array type is

        All = Int8 | Uint8 | Uint8Clamped | Int16 | Uint16 | Int32 | Uint32
            | Float32 | Float64 | BigInt64 | BigUint64
            | ArrayBuffer | SharedArrayBuffer | DataView | TypedArrayBase,
    }

    /// <summary>
    /// Returns a <see cref="RuntimeFeatureSet"/> with every flag set to <c>true</c>.
    /// Equivalent to the default constructor; named for clarity at call sites that
    /// want to opt out of tree-shaking ("emit all helper types").
    /// </summary>
    public static RuntimeFeatureSet EmitEverything() => new();
}
