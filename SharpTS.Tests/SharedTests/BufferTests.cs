using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for Buffer type - verifies that Buffer instance methods
/// and properties work correctly in both interpreter and compiler modes.
/// </summary>
public class BufferTests
{
    #region Type Annotation and Basic Operations

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_TypeAnnotation_Works(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.from("hello");
            console.log(buf.toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("hello\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_TypeInference_Works(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.from("test");
            console.log(buf.toString());
            console.log(buf.length);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("test\n4\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Alloc_Works(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.alloc(5);
            console.log(buf.length);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("5\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Length_Property(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.from("hello world");
            console.log(buf.length);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("11\n", output);
    }

    #endregion

    #region toString Method

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ToString_Default(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.from("hello");
            console.log(buf.toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("hello\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ToString_WithEncoding(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.from("hello");
            console.log(buf.toString("utf8"));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("hello\n", output);
    }

    #endregion

    #region slice Method

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Slice_WithBothArgs(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.from("hello world");
            const sliced = buf.slice(0, 5);
            console.log(sliced.toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("hello\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Slice_StartOnly(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.from("hello world");
            const sliced = buf.slice(6);
            console.log(sliced.toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("world\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Slice_NoArgs(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.from("test");
            const sliced = buf.slice();
            console.log(sliced.toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("test\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Slice_NegativeStart(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.from("hello");
            const sliced = buf.slice(-2);
            console.log(sliced.toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("lo\n", output);
    }

    #endregion

    #region copy Method

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Copy_Basic(ExecutionMode mode)
    {
        var source = """
            const src: Buffer = Buffer.from("hello");
            const dest: Buffer = Buffer.alloc(5);
            const copied = src.copy(dest);
            console.log(copied);
            console.log(dest.toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("5\nhello\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Copy_WithOffsets(ExecutionMode mode)
    {
        var source = """
            const src: Buffer = Buffer.from("hello world");
            const dest: Buffer = Buffer.alloc(5);
            const copied = src.copy(dest, 0, 6, 11);
            console.log(copied);
            console.log(dest.toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("5\nworld\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Copy_Partial(ExecutionMode mode)
    {
        var source = """
            const src: Buffer = Buffer.from("hello");
            const dest: Buffer = Buffer.alloc(3);
            const copied = src.copy(dest, 0, 0, 3);
            console.log(copied);
            console.log(dest.toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("3\nhel\n", output);
    }

    #endregion

    #region compare Method

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Compare_Less(ExecutionMode mode)
    {
        var source = """
            const a: Buffer = Buffer.from("abc");
            const b: Buffer = Buffer.from("abd");
            console.log(a.compare(b));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("-1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Compare_Equal(ExecutionMode mode)
    {
        var source = """
            const a: Buffer = Buffer.from("abc");
            const b: Buffer = Buffer.from("abc");
            console.log(a.compare(b));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Compare_Greater(ExecutionMode mode)
    {
        var source = """
            const a: Buffer = Buffer.from("abd");
            const b: Buffer = Buffer.from("abc");
            console.log(a.compare(b));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Compare_DifferentLengths(ExecutionMode mode)
    {
        var source = """
            const short: Buffer = Buffer.from("ab");
            const long: Buffer = Buffer.from("abc");
            console.log(short.compare(long));
            console.log(long.compare(short));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("-1\n1\n", output);
    }

    #endregion

    #region equals Method

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Equals_True(ExecutionMode mode)
    {
        var source = """
            const a: Buffer = Buffer.from("hello");
            const b: Buffer = Buffer.from("hello");
            console.log(a.equals(b));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Equals_False_DifferentContent(ExecutionMode mode)
    {
        var source = """
            const a: Buffer = Buffer.from("hello");
            const b: Buffer = Buffer.from("world");
            console.log(a.equals(b));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Equals_False_DifferentLength(ExecutionMode mode)
    {
        var source = """
            const a: Buffer = Buffer.from("hello");
            const b: Buffer = Buffer.from("hi");
            console.log(a.equals(b));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\n", output);
    }

    #endregion

    #region fill Method

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Fill_WithNumber(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.alloc(5);
            buf.fill(65);
            console.log(buf.toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("AAAAA\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Fill_WithString(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.alloc(6);
            buf.fill("XY");
            console.log(buf.toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("XYXYXY\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Fill_WithRange(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.alloc(5);
            buf.fill(88, 1, 4);
            console.log(buf.readUInt8(0));
            console.log(buf.readUInt8(1));
            console.log(buf.readUInt8(4));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n88\n0\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Fill_ReturnsThis(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.alloc(3).fill(66);
            console.log(buf.toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("BBB\n", output);
    }

    #endregion

    #region write Method

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Write_Basic(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.alloc(10);
            const written = buf.write("hello");
            console.log(written);
            console.log(buf.slice(0, 5).toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("5\nhello\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Write_WithOffset(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.alloc(10);
            buf.write("XX", 0);
            buf.write("YY", 5);
            console.log(buf.slice(0, 2).toString());
            console.log(buf.slice(5, 7).toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("XX\nYY\n", output);
    }

    #endregion

    #region readUInt8 Method

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadUInt8_Basic(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.from("AB");
            console.log(buf.readUInt8(0));
            console.log(buf.readUInt8(1));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("65\n66\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadUInt8_DefaultOffset(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.from("X");
            console.log(buf.readUInt8());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("88\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadUInt8_InExpression(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.from("AB");
            const sum = buf.readUInt8(0) + buf.readUInt8(1);
            console.log(sum);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("131\n", output);
    }

    #endregion

    #region writeUInt8 Method

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_WriteUInt8_Basic(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.alloc(2);
            const pos1 = buf.writeUInt8(255, 0);
            const pos2 = buf.writeUInt8(128, 1);
            console.log(pos1);
            console.log(pos2);
            console.log(buf.readUInt8(0));
            console.log(buf.readUInt8(1));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n255\n128\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_WriteUInt8_DefaultOffset(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.alloc(1);
            buf.writeUInt8(42);
            console.log(buf.readUInt8(0));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n", output);
    }

    #endregion

    #region toJSON Method

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ToJSON_Structure(ExecutionMode mode)
    {
        var source = """
            const buf: Buffer = Buffer.from("hi");
            const json = buf.toJSON();
            console.log(json.type);
            console.log(json.data.length);
            console.log(json.data[0]);
            console.log(json.data[1]);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("Buffer\n2\n104\n105\n", output);
    }

    #endregion

    #region Complex Scenarios

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_MethodChaining(ExecutionMode mode)
    {
        var source = """
            const result = Buffer.alloc(4).fill(68).slice(1, 3);
            console.log(result.toString());
            console.log(result.length);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("DD\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_AsFunctionParameter(ExecutionMode mode)
    {
        var source = """
            function getLength(buf: Buffer): number {
                return buf.length;
            }
            console.log(getLength(Buffer.from("test")));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("4\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_AsFunctionReturnType(ExecutionMode mode)
    {
        var source = """
            function createBuffer(): Buffer {
                return Buffer.alloc(3).fill(67);
            }
            console.log(createBuffer().toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("CCC\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_InArray(ExecutionMode mode)
    {
        var source = """
            const buffers: Buffer[] = [
                Buffer.from("one"),
                Buffer.from("two")
            ];
            console.log(buffers[0].toString());
            console.log(buffers[1].toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("one\ntwo\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_InConditional(ExecutionMode mode)
    {
        var source = """
            function getBuffer(useA: boolean): Buffer {
                if (useA) {
                    return Buffer.from("AAA");
                }
                return Buffer.from("BBB");
            }
            console.log(getBuffer(true).toString());
            console.log(getBuffer(false).toString());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("AAA\nBBB\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_CompareResultInExpression(ExecutionMode mode)
    {
        var source = """
            const a: Buffer = Buffer.from("a");
            const b: Buffer = Buffer.from("b");
            const isLess = a.compare(b) < 0;
            console.log(isLess);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    #endregion

    #region Multi-byte Read Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadInt8_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.from([127, 128, 255, 0]);
            console.log(buf.readInt8(0));   // 127
            console.log(buf.readInt8(1));   // -128
            console.log(buf.readInt8(2));   // -1
            console.log(buf.readInt8(3));   // 0
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("127\n-128\n-1\n0\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadUInt16LE_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(4);
            buf.writeUInt8(120, 0);
            buf.writeUInt8(86, 1);
            buf.writeUInt8(52, 2);
            buf.writeUInt8(18, 3);
            console.log(buf.readUInt16LE(0));  // 22136
            console.log(buf.readUInt16LE(2));  // 4660
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("22136\n4660\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadUInt16BE_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(4);
            buf.writeUInt8(18, 0);
            buf.writeUInt8(52, 1);
            buf.writeUInt8(86, 2);
            buf.writeUInt8(120, 3);
            console.log(buf.readUInt16BE(0));  // 4660
            console.log(buf.readUInt16BE(2));  // 22136
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("4660\n22136\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadUInt32LE_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(4);
            buf.writeUInt8(120, 0);
            buf.writeUInt8(86, 1);
            buf.writeUInt8(52, 2);
            buf.writeUInt8(18, 3);
            console.log(buf.readUInt32LE(0));  // 305419896
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("305419896\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadUInt32BE_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(4);
            buf.writeUInt8(18, 0);
            buf.writeUInt8(52, 1);
            buf.writeUInt8(86, 2);
            buf.writeUInt8(120, 3);
            console.log(buf.readUInt32BE(0));  // 305419896
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("305419896\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadInt16LE_Signed(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(2);
            buf.writeUInt8(255, 0);
            buf.writeUInt8(255, 1);
            console.log(buf.readInt16LE(0));  // -1
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("-1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadInt32LE_Signed(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(4);
            buf.writeUInt8(255, 0);
            buf.writeUInt8(255, 1);
            buf.writeUInt8(255, 2);
            buf.writeUInt8(255, 3);
            console.log(buf.readInt32LE(0));  // -1
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("-1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadFloatLE_RoundTrip(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(8);
            buf.writeFloatLE(3.14, 0);
            const val = buf.readFloatLE(0);
            console.log(Math.abs(val - 3.14) < 0.001);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadDoubleLE_RoundTrip(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(8);
            buf.writeDoubleLE(3.141592653589793, 0);
            const val = buf.readDoubleLE(0);
            console.log(Math.abs(val - 3.141592653589793) < 0.0000001);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    #endregion

    #region Multi-byte Write Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_WriteInt8_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(2);
            buf.writeInt8(-1, 0);
            buf.writeInt8(127, 1);
            console.log(buf.readUInt8(0));  // 255
            console.log(buf.readUInt8(1));  // 127
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("255\n127\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_WriteUInt16LE_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(4);
            const next = buf.writeUInt16LE(4660, 0);
            buf.writeUInt16LE(22136, next);
            console.log(buf.readUInt8(0));  // 52
            console.log(buf.readUInt8(1));  // 18
            console.log(buf.readUInt8(2));  // 120
            console.log(buf.readUInt8(3));  // 86
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("52\n18\n120\n86\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_WriteUInt16BE_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(4);
            buf.writeUInt16BE(4660, 0);
            buf.writeUInt16BE(22136, 2);
            console.log(buf.readUInt8(0));  // 18
            console.log(buf.readUInt8(1));  // 52
            console.log(buf.readUInt8(2));  // 86
            console.log(buf.readUInt8(3));  // 120
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("18\n52\n86\n120\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_WriteUInt32_RoundTrip(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(8);
            buf.writeUInt32LE(305419896, 0);  // 0x12345678
            buf.writeUInt32BE(305419896, 4);  // 0x12345678
            console.log(buf.readUInt32LE(0));
            console.log(buf.readUInt32BE(4));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("305419896\n305419896\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_WriteInt16_RoundTrip(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(4);
            buf.writeInt16LE(-1000, 0);
            buf.writeInt16BE(-1000, 2);
            console.log(buf.readInt16LE(0));
            console.log(buf.readInt16BE(2));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("-1000\n-1000\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_WriteInt32_RoundTrip(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(8);
            buf.writeInt32LE(-100000, 0);
            buf.writeInt32BE(-100000, 4);
            console.log(buf.readInt32LE(0));
            console.log(buf.readInt32BE(4));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("-100000\n-100000\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_WriteFloat_RoundTrip(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(8);
            buf.writeFloatLE(3.14, 0);
            buf.writeFloatBE(2.71, 4);
            const val1 = buf.readFloatLE(0);
            const val2 = buf.readFloatBE(4);
            console.log(Math.abs(val1 - 3.14) < 0.001);
            console.log(Math.abs(val2 - 2.71) < 0.001);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_WriteDouble_RoundTrip(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(16);
            buf.writeDoubleLE(Math.PI, 0);
            buf.writeDoubleBE(Math.E, 8);
            console.log(buf.readDoubleLE(0) === Math.PI);
            console.log(buf.readDoubleBE(8) === Math.E);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Write_ReturnsNextOffset(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(10);
            let offset = 0;
            offset = buf.writeUInt8(1, offset);
            offset = buf.writeUInt16LE(2, offset);
            offset = buf.writeUInt32LE(3, offset);
            console.log(offset);  // 1 + 2 + 4 = 7
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("7\n", output);
    }

    #endregion

    #region Search Method Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_IndexOf_ByteValue(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.from([1, 2, 3, 4, 5, 3, 6]);
            console.log(buf.indexOf(3));     // 2
            console.log(buf.indexOf(3, 3));  // 5
            console.log(buf.indexOf(99));    // -1
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("2\n5\n-1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Includes_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.from([1, 2, 3, 4, 5]);
            console.log(buf.includes(3));    // true
            console.log(buf.includes(99));   // false
            console.log(buf.includes(1, 1)); // false (starts after 1)
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nfalse\nfalse\n", output);
    }

    #endregion

    #region Swap Method Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Swap16_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.from([1, 2, 3, 4]);
            buf.swap16();
            console.log(buf.readUInt8(0));  // 2
            console.log(buf.readUInt8(1));  // 1
            console.log(buf.readUInt8(2));  // 4
            console.log(buf.readUInt8(3));  // 3
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("2\n1\n4\n3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Swap32_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.from([1, 2, 3, 4, 5, 6, 7, 8]);
            buf.swap32();
            console.log(buf.readUInt8(0));  // 4
            console.log(buf.readUInt8(1));  // 3
            console.log(buf.readUInt8(2));  // 2
            console.log(buf.readUInt8(3));  // 1
            console.log(buf.readUInt8(4));  // 8
            console.log(buf.readUInt8(5));  // 7
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("4\n3\n2\n1\n8\n7\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Swap64_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.from([1, 2, 3, 4, 5, 6, 7, 8]);
            buf.swap64();
            console.log(buf.readUInt8(0));  // 8
            console.log(buf.readUInt8(1));  // 7
            console.log(buf.readUInt8(6));  // 2
            console.log(buf.readUInt8(7));  // 1
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("8\n7\n2\n1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Swap_Chaining(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.from([1, 2, 3, 4]).swap16().swap16();
            console.log(buf.readUInt8(0));  // 1 (back to original)
            console.log(buf.readUInt8(1));  // 2
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n", output);
    }

    #endregion

    #region Endianness Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Endianness_Conversion(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(4);
            buf.writeUInt32LE(305419896, 0);
            console.log(buf.readUInt32BE(0).toString(16));  // 78563412
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("78563412\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_Mixed_Endianness(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(8);
            buf.writeUInt32LE(305419896, 0);
            buf.writeUInt32BE(287454020, 4);
            console.log(buf.readUInt32LE(0).toString(16));
            console.log(buf.readUInt32BE(0).toString(16));
            console.log(buf.readUInt32LE(4).toString(16));
            console.log(buf.readUInt32BE(4).toString(16));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("12345678\n78563412\n44332211\n11223344\n", output);
    }

    #endregion

    #region BigInt Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_ReadBigInt64LE_Basic(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(8);
            buf.writeUInt8(1, 0);
            buf.writeUInt8(0, 1);
            buf.writeUInt8(0, 2);
            buf.writeUInt8(0, 3);
            buf.writeUInt8(0, 4);
            buf.writeUInt8(0, 5);
            buf.writeUInt8(0, 6);
            buf.writeUInt8(0, 7);
            console.log(buf.readBigInt64LE(0));  // 1n
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1n\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_WriteBigInt64LE_RoundTrip(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(8);
            buf.writeBigInt64LE(12345n, 0);
            const val = buf.readBigInt64LE(0);
            console.log(val === 12345n);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Buffer_BigInt_RoundTrip(ExecutionMode mode)
    {
        var source = """
            const buf = Buffer.alloc(16);
            buf.writeBigInt64LE(12345n, 0);
            buf.writeBigInt64BE(67890n, 8);
            console.log(buf.readBigInt64LE(0));
            console.log(buf.readBigInt64BE(8));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("12345n\n67890n\n", output);
    }

    #endregion
}
