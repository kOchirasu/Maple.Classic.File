using System.Buffers.Binary;
using Maple.Classic.File.IO;
using Maple.Classic.File.IO.Crypto;
using Maple.Classic.File.IO.Serialization;
using Xunit;

namespace Maple.Classic.File.Tests;

public class WzReaderTests {
    [Fact]
    public void Version779_MatchesClient() {
        // Values confirmed against the shipped archives; see docs/wz-format.md.
        Assert.Equal(0x0000_E73Au, WzCrypto.VersionHash(779));
        Assert.Equal((ushort) 34, WzCrypto.EncVersion(779));
    }

    [Fact]
    public void AsciiString_DecodesWithoutKey() {
        // "Character" as it appears in Base.wz.
        byte[] raw = [0xF7, 0xE9, 0xC3, 0xCD, 0xDF, 0xCF, 0xCC, 0xC4, 0xD4, 0xC0];
        var r = new WzReader(raw, 0x3C, WzCrypto.VersionHash(779));
        Assert.Equal("Character", r.ReadString());
    }

    [Fact]
    public void Utf16String_EscapesAt127() {
        // A 130-char UTF-16 string must escape via tag 127 + i32 length.
        const int n = 130;
        string text = new('A', n);
        var raw = new List<byte> { 127 };

        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBytes, n);
        raw.AddRange(lenBytes);

        Span<byte> charBytes = stackalloc byte[2];
        for (int k = 0; k < n; k++) {
            ushort enc = (ushort) (text[k] ^ unchecked((ushort) (0xAAAA + k)));
            BinaryPrimitives.WriteUInt16LittleEndian(charBytes, enc);
            raw.AddRange(charBytes);
        }

        var r = new WzReader(raw.ToArray(), 0, 0);
        Assert.Equal(text, r.ReadString());
    }

    [Fact]
    public void AsciiString_EscapesAtNeg128() {
        const int n = 200;
        string text = new('x', n);
        var raw = new List<byte> { 0x80 };

        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBytes, n);
        raw.AddRange(lenBytes);

        for (int k = 0; k < n; k++) {
            raw.Add((byte) ((byte) 'x' ^ unchecked((byte) (0xAA + k))));
        }

        var r = new WzReader(raw.ToArray(), 0, 0);
        Assert.Equal(text, r.ReadString());
    }

    [Fact]
    public void CompressedInt_EscapesToI32() {
        byte[] raw = [0x05, 0x80, 0x01, 0x02, 0x03, 0x04];
        var r = new WzReader(raw, 0, 0);
        Assert.Equal(5, r.ReadCompressedInt32());
        Assert.Equal(0x0403_0201, r.ReadCompressedInt32());
    }

    [Fact]
    public void CompressedLongAndFloat_DecodeCorrectly() {
        var w = new WzWriter();
        w.CompressedI64(42);
        w.CompressedI64(0x0102_0304_0506_0708L);
        w.CompressedF32(0.0f);
        w.CompressedF32(3.5f);

        var r = new WzReader(w.ToArray(), 0, 0);
        Assert.Equal(42L, r.ReadCompressedInt64());
        Assert.Equal(0x0102_0304_0506_0708L, r.ReadCompressedInt64());
        Assert.Equal(0.0f, r.ReadCompressedSingle());
        Assert.Equal(3.5f, r.ReadCompressedSingle());
    }

    [Fact]
    public void StringAt_InlineAndDereferenced_DecodesAndRestoresCursor() {
        var w = new WzWriter();
        w.String("SharedName"); // at offset 0
        int afterShared = w.Length;
        w.U8(0x00); // inline tag
        w.String("InlineName");
        w.U8(0x01); // deref tag
        w.U32(0);   // offset 0 -> "SharedName"
        w.U8(0x2A); // trailing byte to verify cursor restoration

        var r = new WzReader(w.ToArray(), 0, 0);
        r.Seek(afterShared);
        Assert.Equal("InlineName", r.ReadStringAt(0, WzImage.NameInline, WzImage.NameDeref));
        Assert.Equal("SharedName", r.ReadStringAt(0, WzImage.NameInline, WzImage.NameDeref));
        Assert.Equal((byte) 0x2A, r.ReadByte());
    }

    [Fact]
    public void InvalidUtf16_ThrowsBadUtf16WzException() {
        // 1 UTF-16 code unit that decodes to an unpaired high surrogate (0xD800).
        ushort encoded = (ushort) (0xD800 ^ 0xAAAA);
        byte[] raw = [0x01, (byte) (encoded & 0xFF), (byte) (encoded >> 8)];

        WzException ex = Assert.Throws<WzException>(() => {
            var r = new WzReader(raw, 0, 0);
            _ = r.ReadString();
        });
        Assert.Equal(WzErrorKind.BadUtf16, ex.Kind);
    }

    [Fact]
    public void UnexpectedEof_ThrowsUnexpectedEofWzException() {
        byte[] raw = [0x01, 0x02];
        WzException ex = Assert.Throws<WzException>(() => {
            var r = new WzReader(raw, 0, 0);
            _ = r.ReadUInt32();
        });
        Assert.Equal(WzErrorKind.UnexpectedEof, ex.Kind);
        Assert.Equal(0, ex.Offset);
    }
}
