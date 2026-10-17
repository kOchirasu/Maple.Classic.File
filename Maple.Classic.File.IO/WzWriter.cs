using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Maple.Classic.File.IO.Crypto;

namespace Maple.Classic.File.IO;

/// <summary>
/// One image entry going into or extracted from a WZ archive.
/// </summary>
public sealed record WzImageEntry(string Name, byte[] Bytes);

/// <summary>
/// Byte-level WZ encoders and archive builder — the exact inverse of <see cref="WzReader"/>, <see cref="WzArchive"/>, and <see cref="WzImage"/>.
/// </summary>
public class WzWriter {
    private readonly List<byte> buf;

    public int Length => buf.Count;

    public WzWriter(int capacity = 256) {
        buf = new List<byte>(capacity);
    }

    public ReadOnlySpan<byte> AsSpan() => CollectionsMarshal.AsSpan(buf);

    public Span<byte> AsMutableSpan() => CollectionsMarshal.AsSpan(buf);

    public byte[] ToArray() => buf.ToArray();

    public void WriteByte(byte v) {
        buf.Add(v);
    }

    public void U8(byte v) => WriteByte(v);

    public void WriteBytes(ReadOnlySpan<byte> bytes) {
        buf.AddRange(bytes);
    }

    public void WriteUInt16(ushort v) {
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tmp, v);
        buf.AddRange(tmp);
    }

    public void U16(ushort v) => WriteUInt16(v);

    public void WriteInt16(short v) {
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(tmp, v);
        buf.AddRange(tmp);
    }

    public void I16(short v) => WriteInt16(v);

    public void WriteUInt32(uint v) {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, v);
        buf.AddRange(tmp);
    }

    public void U32(uint v) => WriteUInt32(v);

    public void WriteInt32(int v) {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tmp, v);
        buf.AddRange(tmp);
    }

    public void I32(int v) => WriteInt32(v);

    public void WriteUInt64(ulong v) {
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(tmp, v);
        buf.AddRange(tmp);
    }

    public void U64(ulong v) => WriteUInt64(v);

    public void WriteDouble(double v) {
        WriteUInt64(BitConverter.DoubleToUInt64Bits(v));
    }

    public void F64(double v) => WriteDouble(v);

    /// <summary>
    /// Compressed int: one signed byte unless it needs the <c>-128</c> (<c>0x80</c>) escape to a full <see cref="int"/>.
    /// </summary>
    public void WriteCompressedInt32(int v) {
        if (v > sbyte.MinValue && v <= sbyte.MaxValue) {
            WriteByte(unchecked((byte) (sbyte) v));
        } else {
            WriteByte(0x80);
            WriteInt32(v);
        }
    }

    public void CompressedI32(int v) => WriteCompressedInt32(v);

    /// <summary>
    /// Compressed long: one signed byte unless it needs the <c>-128</c> (<c>0x80</c>) escape to a full <see cref="long"/>.
    /// </summary>
    public void WriteCompressedInt64(long v) {
        if (v > sbyte.MinValue && v <= sbyte.MaxValue) {
            WriteByte(unchecked((byte) (sbyte) v));
        } else {
            WriteByte(0x80);
            WriteUInt64(unchecked((ulong) v));
        }
    }

    public void CompressedI64(long v) => WriteCompressedInt64(v);

    /// <summary>
    /// Compressed float: <c>0.0f</c> is one <c>0x00</c> byte; anything else is <c>0x80</c> + <see cref="float"/>.
    /// </summary>
    public void WriteCompressedSingle(float v) {
        if (v == 0.0f) {
            WriteByte(0);
        } else {
            WriteByte(0x80);
            WriteUInt32(BitConverter.SingleToUInt32Bits(v));
        }
    }

    public void CompressedF32(float v) => WriteCompressedSingle(v);

    /// <summary>
    /// Writes an obfuscated WZ string with zero key.
    /// Uses ASCII encoding only when every character is below <c>0x80</c>; non-ASCII strings use UTF-16LE.
    /// </summary>
    public void WriteString(string s) {
        if (string.IsNullOrEmpty(s)) {
            WriteByte(0);
            return;
        }

        bool isAscii = true;
        foreach (char c in s) {
            if (c >= 0x80) {
                isAscii = false;
                break;
            }
        }

        if (isAscii) {
            int n = s.Length;
            if (n >= 128) {
                WriteByte(0x80);
                WriteInt32(n);
            } else {
                WriteByte(unchecked((byte) (sbyte) (-n)));
            }

            for (int k = 0; k < n; k++) {
                byte b = (byte) s[k];
                WriteByte((byte) (b ^ unchecked((byte) (WzCrypto.AsciiMask + (byte) k))));
            }
        } else {
            int n = s.Length;
            if (n >= 127) {
                WriteByte(127);
                WriteInt32(n);
            } else {
                WriteByte((byte) n);
            }

            for (int k = 0; k < n; k++) {
                ushort u = s[k];
                WriteUInt16((ushort) (u ^ unchecked((ushort) (WzCrypto.UnicodeMask + (ushort) k))));
            }
        }
    }

    public void String(string s) => WriteString(s);

    /// <summary>
    /// Writes a name or string value inside an image inline with the <c>0x00</c> tag.
    /// </summary>
    public void WriteImageString(string s) {
        WriteByte(0x00);
        WriteString(s);
    }

    public void ImageString(string s) => WriteImageString(s);

    /// <summary>
    /// Writes an extended-type name inline with the <c>0x73</c> tag.
    /// </summary>
    public void WriteTypeString(string s) {
        WriteByte(0x73);
        WriteString(s);
    }

    public void TypeString(string s) => WriteTypeString(s);

    /// <summary>
    /// Builds a complete standalone WZ archive: PKG1 header, flat directory of images, and the image payloads.
    /// </summary>
    public static byte[] WriteArchive(ushort version, IReadOnlyList<WzImageEntry> images) {
        ArgumentNullException.ThrowIfNull(images);

        uint vh = WzCrypto.VersionHash(version);
        byte[] copyrightBytes = Encoding.UTF8.GetBytes(WzCrypto.Copyright);
        uint fstart = (uint) (4 + 8 + 4 + copyrightBytes.Length + 1); // 0x3C

        var dir = new WzWriter();
        dir.U16(WzCrypto.EncVersion(version));
        dir.CompressedI32(images.Count);

        var offsetFields = new int[images.Count];
        for (int i = 0; i < images.Count; i++) {
            WzImageEntry img = images[i];
            dir.U8(4);
            dir.String(img.Name);
            dir.CompressedI32(img.Bytes.Length);
            dir.CompressedI32(WzCrypto.Checksum(img.Bytes));
            offsetFields[i] = dir.Length;
            dir.U32(0);
        }

        int totalPayloadSize = 0;
        for (int i = 0; i < images.Count; i++) {
            totalPayloadSize += images[i].Bytes.Length;
        }

        int pos = (int) fstart + dir.Length;
        Span<byte> dirSpan = dir.AsMutableSpan();
        for (int i = 0; i < images.Count; i++) {
            uint fieldPos = (uint) ((int) fstart + offsetFields[i]);
            uint enc = WzCrypto.EncryptOffset(fieldPos, fstart, vh, (uint) pos);
            BinaryPrimitives.WriteUInt32LittleEndian(dirSpan.Slice(offsetFields[i], 4), enc);
            pos += images[i].Bytes.Length;
        }

        var output = new byte[pos];
        Span<byte> outSpan = output;

        WzCrypto.Magic.CopyTo(outSpan[..4]);
        BinaryPrimitives.WriteUInt64LittleEndian(outSpan.Slice(4, 8), (ulong) pos - fstart);
        BinaryPrimitives.WriteUInt32LittleEndian(outSpan.Slice(12, 4), fstart);
        copyrightBytes.CopyTo(outSpan.Slice(16, copyrightBytes.Length));
        outSpan[16 + copyrightBytes.Length] = 0;

        int cursor = (int) fstart;
        dir.AsSpan().CopyTo(outSpan.Slice(cursor, dir.Length));
        cursor += dir.Length;

        for (int i = 0; i < images.Count; i++) {
            byte[] imgBytes = images[i].Bytes;
            imgBytes.CopyTo(outSpan.Slice(cursor, imgBytes.Length));
            cursor += imgBytes.Length;
        }

        return output;
    }

    /// <summary>
    /// Extracts every image of a flat archive as owned bytes, in directory order.
    /// </summary>
    public static List<WzImageEntry> ArchiveImages(WzArchive archive) {
        ArgumentNullException.ThrowIfNull(archive);
        var output = new List<WzImageEntry>(archive.Root.Children.Count);
        foreach (WzNode node in archive.Root.Children) {
            if (node.IsDir) {
                throw WzException.BadEntryType(3, (int) node.Offset);
            }
            output.Add(new WzImageEntry(node.Name, archive.ImageBytes(node)));
        }
        return output;
    }

    /// <summary>
    /// Replaces or appends images by name, keeping the base order. Appended images are sorted by ordinal name.
    /// </summary>
    public static List<WzImageEntry> MergeImages(
        IEnumerable<WzImageEntry> baseImages,
        IEnumerable<WzImageEntry> additions) {
        ArgumentNullException.ThrowIfNull(baseImages);
        ArgumentNullException.ThrowIfNull(additions);

        var byName = new SortedDictionary<string, WzImageEntry>(StringComparer.Ordinal);
        foreach (WzImageEntry a in additions) {
            byName[a.Name] = a;
        }

        var output = new List<WzImageEntry>();
        foreach (WzImageEntry b in baseImages) {
            if (byName.Remove(b.Name, out WzImageEntry? replacement)) {
                output.Add(replacement);
            } else {
                output.Add(b);
            }
        }

        output.AddRange(byName.Values);
        return output;
    }

    /// <summary>
    /// Sanity-checks that a written archive parses and holds the exact images it was given.
    /// </summary>
    public static void VerifyArchive(byte[] bytes, ushort version, IReadOnlyList<WzImageEntry> images) {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(images);

        WzHeader header = WzArchive.ParseHeader(bytes);
        if (header.EncVer != WzCrypto.EncVersion(version)) {
            throw WzException.VersionNotFound(header.EncVer);
        }

        var r = new WzReader(bytes, header.FStart, WzCrypto.VersionHash(version));
        r.Seek(checked((int) header.FStart + 2));
        int count = r.ReadCompressedInt32();
        if (count != images.Count) {
            throw WzException.BadEntryType(0, r.Position);
        }

        foreach (WzImageEntry img in images) {
            byte tag = r.ReadByte();
            if (tag != 4) {
                throw WzException.BadEntryType(tag, r.Position);
            }

            string name = r.ReadString();
            int size = r.ReadCompressedInt32();
            int sum = r.ReadCompressedInt32();
            uint off = r.DecryptOffset();

            if (name != img.Name || size != img.Bytes.Length || sum != WzCrypto.Checksum(img.Bytes)) {
                throw WzException.OutOfBounds(name, off, size, bytes.Length);
            }

            if ((long) off + size > bytes.Length || !bytes.AsSpan((int) off, size).SequenceEqual(img.Bytes)) {
                throw WzException.OutOfBounds(name, off, size, bytes.Length);
            }
        }
    }
}
