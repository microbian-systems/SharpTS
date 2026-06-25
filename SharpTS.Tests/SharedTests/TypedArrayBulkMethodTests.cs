using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Coverage for the in-place / copying TypedArray bulk methods — fill, set, copyWithin,
/// reverse, slice — which previously had no dedicated tests. These exercise the
/// byte[]-level fast paths in <c>SharpTSTypedArray</c> (#928 ②): the methods convert/copy
/// at the raw-buffer level (Buffer.BlockCopy / Span memmove) instead of boxing every
/// element through the <c>object?</c> indexer, and must observe identical JS semantics.
///
/// These run interpreter-only on purpose: the compiled emitter (<c>$TypedArray</c>) does
/// not implement these bulk methods at all — calling them on a compiled typed array throws
/// "object is not a function" — so a dual-mode theory would fail in compiled mode. Closing
/// that compiled gap is separate, larger work.
/// </summary>
public class TypedArrayBulkMethodTests
{
    private static string Run(string source) => TestHarness.Run(source, ExecutionMode.Interpreted);

    // ----- fill -----

    [Fact]
    public void Fill_WholeArray()
    {
        Assert.Equal("2.5,2.5,2.5,2.5\n", Run(@"
            let a = new Float64Array(4);
            a.fill(2.5);
            console.log(a.join(','));"));
    }

    [Fact]
    public void Fill_Range_LeavesOthersZero()
    {
        Assert.Equal("0,7,7,7,0\n", Run(@"
            let a = new Int32Array(5);
            a.fill(7, 1, 4);
            console.log(a.join(','));"));
    }

    [Fact]
    public void Fill_Int32_TruncatesTowardZero()
    {
        Assert.Equal("3,3,3\n", Run(@"
            let a = new Int32Array(3);
            a.fill(3.9);
            console.log(a.join(','));"));
    }

    [Fact]
    public void Fill_Uint8Clamped_ClampsHigh()
    {
        Assert.Equal("255,255,255\n", Run(@"
            let a = new Uint8ClampedArray(3);
            a.fill(300);
            console.log(a.join(','));"));
    }

    [Fact]
    public void Fill_Uint8Clamped_ClampsLow()
    {
        Assert.Equal("0,0,0\n", Run(@"
            let a = new Uint8ClampedArray(3);
            a.fill(-5);
            console.log(a.join(','));"));
    }

    // ----- set -----

    [Fact]
    public void Set_SameType_AtOffset()
    {
        // Same element type → Buffer.BlockCopy fast path.
        Assert.Equal("0,1.5,2.5,0\n", Run(@"
            let a = new Float64Array(2);
            a[0] = 1.5; a[1] = 2.5;
            let b = new Float64Array(4);
            b.set(a, 1);
            console.log(b.join(','));"));
    }

    [Fact]
    public void Set_CrossType_ConvertsPerElement()
    {
        // Different element type → value-converting element-wise path (truncates to int8).
        Assert.Equal("1,2\n", Run(@"
            let f = new Float64Array(2);
            f[0] = 1.9; f[1] = 2.1;
            let i = new Int8Array(2);
            i.set(f);
            console.log(i.join(','));"));
    }

    [Fact]
    public void Set_FromRegularArray()
    {
        Assert.Equal("10,20,30\n", Run(@"
            let a = new Int32Array(3);
            a.set([10, 20, 30]);
            console.log(a.join(','));"));
    }

    [Fact]
    public void Set_SameType_NegativeOffset_Throws()
    {
        // The same-type fast path must NOT silently accept a negative offset — it falls
        // through to the element setter, which raises a RangeError as before.
        Assert.Equal("threw\n", Run(@"
            let src = new Int32Array(2);
            src[0] = 5; src[1] = 6;
            let dst = new Int32Array(4);
            try { dst.set(src, -1); console.log('no-error'); }
            catch (e) { console.log('threw'); }"));
    }

    [Fact]
    public void Set_SameType_TooLarge_Throws()
    {
        Assert.Equal("threw\n", Run(@"
            let src = new Int32Array(3);
            let dst = new Int32Array(2);
            try { dst.set(src); console.log('no-error'); }
            catch (e) { console.log('threw'); }"));
    }

    // ----- copyWithin -----

    [Fact]
    public void CopyWithin_Forward()
    {
        Assert.Equal("3,4,2,3,4\n", Run(@"
            let a = new Int32Array(5);
            for (let i = 0; i < 5; i++) { a[i] = i; }
            a.copyWithin(0, 3);
            console.log(a.join(','));"));
    }

    [Fact]
    public void CopyWithin_OverlappingRegions()
    {
        // src [0..3) copied onto [2..5): overlapping; memmove must read-before-overwrite.
        Assert.Equal("0,1,0,1,2\n", Run(@"
            let a = new Int32Array(5);
            for (let i = 0; i < 5; i++) { a[i] = i; }
            a.copyWithin(2, 0, 3);
            console.log(a.join(','));"));
    }

    // ----- reverse -----

    [Fact]
    public void Reverse_EvenLength()
    {
        Assert.Equal("4,3,2,1\n", Run(@"
            let a = new Int32Array(4);
            for (let i = 0; i < 4; i++) { a[i] = i + 1; }
            a.reverse();
            console.log(a.join(','));"));
    }

    [Fact]
    public void Reverse_OddLength_MiddleFixed()
    {
        Assert.Equal("3,2,1\n", Run(@"
            let a = new Int32Array(3);
            for (let i = 0; i < 3; i++) { a[i] = i + 1; }
            a.reverse();
            console.log(a.join(','));"));
    }

    [Fact]
    public void Reverse_Float64_PreservesValues()
    {
        Assert.Equal("3.5,2.5,1.5\n", Run(@"
            let a = new Float64Array(3);
            a[0] = 1.5; a[1] = 2.5; a[2] = 3.5;
            a.reverse();
            console.log(a.join(','));"));
    }

    // ----- slice -----

    [Fact]
    public void Slice_Range()
    {
        Assert.Equal("1,2,3\n", Run(@"
            let a = new Int32Array(5);
            for (let i = 0; i < 5; i++) { a[i] = i; }
            console.log(a.slice(1, 4).join(','));"));
    }

    [Fact]
    public void Slice_NegativeBegin()
    {
        Assert.Equal("3,4\n", Run(@"
            let a = new Int32Array(5);
            for (let i = 0; i < 5; i++) { a[i] = i; }
            console.log(a.slice(-2).join(','));"));
    }

    [Fact]
    public void Slice_IsIndependentCopy()
    {
        // Mutating the slice must not affect the source (slice copies, subarray would alias).
        Assert.Equal("9,1,2|0,1,2\n", Run(@"
            let a = new Int32Array(3);
            for (let i = 0; i < 3; i++) { a[i] = i; }
            let s = a.slice(0, 3);
            s[0] = 9;
            console.log(s.join(',') + '|' + a.join(','));"));
    }
}
