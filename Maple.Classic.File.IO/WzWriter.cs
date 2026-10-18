using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Maple.Classic.File.IO.Crypto;

namespace Maple.Classic.File.IO;

internal sealed record WzImageEntry(string Name, ReadOnlyMemory<byte> Bytes);

/// <summary>
/// Internal contiguous-buffer WZ binary encoder and archive builder.
/// </summary>
internal class WzWriter(int capacity = 256) {
    private byte[] buf = new byte[Math.Max(capacity, 64)];
    private int pos;

    public int Length => pos;

    public ReadOnlySpan<byte> AsSpan() => buf.AsSpan(0, pos);

    public Span<byte> AsMutableSpan() => buf.AsSpan(0, pos);

    public byte[] ToArray() => buf.AsSpan(0, pos).ToArray();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> AllocateSpan(int count) {
        int required = pos + count;
        if (required > buf.Length) {
            Grow(required);
        }
        Span<byte> slice = buf.AsSpan(pos, count);
        pos = required;
        return slice;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int required) {
        uint nextPow2 = BitOperations.RoundUpToPowerOf2((uint) required);
        int newSize = (int) Math.Max(nextPow2, (uint) (buf.Length * 2));
        Array.Resize(ref buf, newSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte v) {
        if (pos >= buf.Length) {
            Grow(pos + 1);
        }
        buf[pos++] = v;
    }

    public void U8(byte v) => WriteByte(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBytes(ReadOnlySpan<byte> bytes) {
        if (!bytes.IsEmpty) {
            bytes.CopyTo(AllocateSpan(bytes.Length));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteZeroBytes(int count) {
        if (count > 0) {
            AllocateSpan(count).Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(AllocateSpan(2), v);

    public void U16(ushort v) => WriteUInt16(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt16(short v) => BinaryPrimitives.WriteInt16LittleEndian(AllocateSpan(2), v);

    public void I16(short v) => WriteInt16(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(AllocateSpan(4), v);

    public void U32(uint v) => WriteUInt32(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt32(int v) => BinaryPrimitives.WriteInt32LittleEndian(AllocateSpan(4), v);

    public void I32(int v) => WriteInt32(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(AllocateSpan(8), v);

    public void U64(ulong v) => WriteUInt64(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteDouble(double v) => WriteUInt64(BitConverter.DoubleToUInt64Bits(v));

    public void F64(double v) => WriteDouble(v);

    /// <summary>
    /// Reserves 4 bytes in the buffer for a length or offset field to be patched later via <see cref="PatchU32"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReserveU32() {
        int offset = pos;
        AllocateSpan(4).Clear();
        return offset;
    }

    /// <summary>
    /// Patches a 4-byte unsigned integer at a previously reserved <paramref name="offset"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchU32(int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset, 4), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteCompressedInt32(int v) {
        if (v is > sbyte.MinValue and <= sbyte.MaxValue) {
            WriteByte(unchecked((byte) (sbyte) v));
        } else {
            Span<byte> dst = AllocateSpan(5);
            dst[0] = 0x80;
            BinaryPrimitives.WriteInt32LittleEndian(dst[1..], v);
        }
    }

    public void CompressedI32(int v) => WriteCompressedInt32(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteCompressedInt64(long v) {
        if (v is > sbyte.MinValue and <= sbyte.MaxValue) {
            WriteByte(unchecked((byte) (sbyte) v));
        } else {
            Span<byte> dst = AllocateSpan(9);
            dst[0] = 0x80;
            BinaryPrimitives.WriteUInt64LittleEndian(dst[1..], unchecked((ulong) v));
        }
    }

    public void CompressedI64(long v) => WriteCompressedInt64(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteCompressedSingle(float v) {
        if (v == 0.0f) {
            WriteByte(0);
        } else {
            Span<byte> dst = AllocateSpan(5);
            dst[0] = 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(dst[1..], BitConverter.SingleToUInt32Bits(v));
        }
    }

    public void CompressedF32(float v) => WriteCompressedSingle(v);

    public void WriteString(ReadOnlySpan<char> s) {
        if (s.IsEmpty) {
            WriteByte(0);
            return;
        }

        if (Ascii.IsValid(s)) {
            int n = s.Length;
            if (n >= 128) {
                Span<byte> header = AllocateSpan(5);
                header[0] = 0x80;
                BinaryPrimitives.WriteInt32LittleEndian(header[1..], n);
            } else {
                WriteByte(unchecked((byte) (sbyte) (-n)));
            }

            Span<byte> payload = AllocateSpan(n);
            for (int k = 0; k < n; k++) {
                payload[k] = (byte) ((byte) s[k] ^ unchecked((byte) (WzCrypto.ASCII_MASK + (byte) k)));
            }
        } else {
            int n = s.Length;
            if (n >= 127) {
                Span<byte> header = AllocateSpan(5);
                header[0] = 127;
                BinaryPrimitives.WriteInt32LittleEndian(header[1..], n);
            } else {
                WriteByte((byte) n);
            }

            Span<byte> payload = AllocateSpan(n * 2);
            for (int k = 0; k < n; k++) {
                ushort u = (ushort) (s[k] ^ unchecked((ushort) (WzCrypto.UNICODE_MASK + (ushort) k)));
                BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(k * 2, 2), u);
            }
        }
    }

    public void String(ReadOnlySpan<char> s) => WriteString(s);

    public void WriteImageString(ReadOnlySpan<char> s) {
        WriteByte(0x00);
        WriteString(s);
    }

    public void ImageString(ReadOnlySpan<char> s) => WriteImageString(s);

    public void WriteTypeString(ReadOnlySpan<char> s) {
        WriteByte(0x73);
        WriteString(s);
    }

    public void TypeString(ReadOnlySpan<char> s) => WriteTypeString(s);

    public static byte[] WriteArchive(ushort version, IReadOnlyList<WzImageEntry> images) {
        ArgumentNullException.ThrowIfNull(images);

        for (int i = 0; i < images.Count; i++) {
            if (images[i].Name.Contains('/')) {
                return WriteHierarchicalArchive(version, images);
            }
        }

        uint vh = WzCrypto.VersionHash(version);
        byte[] copyrightBytes = Encoding.UTF8.GetBytes(WzCrypto.COPYRIGHT);
        uint fstart = (uint) (4 + 8 + 4 + copyrightBytes.Length + 1); // 0x3C

        var dir = new WzWriter(images.Count * 24 + 16);
        dir.U16(WzCrypto.EncVersion(version));
        dir.CompressedI32(images.Count);

        var offsetFields = new int[images.Count];
        for (int i = 0; i < images.Count; i++) {
            WzImageEntry img = images[i];
            ReadOnlySpan<byte> imgSpan = img.Bytes.Span;
            dir.U8(4);
            dir.String(img.Name);
            dir.CompressedI32(imgSpan.Length);
            dir.CompressedI32(WzCrypto.Checksum(imgSpan));
            offsetFields[i] = dir.ReserveU32();
        }

        int pos = (int) fstart + dir.Length;
        for (int i = 0; i < images.Count; i++) {
            uint fieldPos = (uint) ((int) fstart + offsetFields[i]);
            uint enc = WzCrypto.EncryptOffset(fieldPos, fstart, vh, (uint) pos);
            dir.PatchU32(offsetFields[i], enc);
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
            ReadOnlySpan<byte> imgSpan = images[i].Bytes.Span;
            imgSpan.CopyTo(outSpan.Slice(cursor, imgSpan.Length));
            cursor += imgSpan.Length;
        }

        return output;
    }

    private sealed class DirBuilderNode(string name) {
        public string Name { get; } = name;
        public List<DirBuilderNode> SubDirs { get; } = [];
        public List<int> ImageIndices { get; } = [];
        public int OffsetFieldPos { get; set; }
        public int TableStartPos { get; set; }
    }

    private static byte[] WriteHierarchicalArchive(ushort version, IReadOnlyList<WzImageEntry> images) {
        uint vh = WzCrypto.VersionHash(version);
        byte[] copyrightBytes = Encoding.UTF8.GetBytes(WzCrypto.COPYRIGHT);
        uint fstart = (uint) (4 + 8 + 4 + copyrightBytes.Length + 1); // 0x3C

        var rootDir = new DirBuilderNode("");
        for (int i = 0; i < images.Count; i++) {
            string path = images[i].Name.Trim('/');
            if (string.IsNullOrEmpty(path)) {
                continue;
            }
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            DirBuilderNode cur = rootDir;
            for (int p = 0; p < parts.Length - 1; p++) {
                string dirName = parts[p];
                DirBuilderNode? next = cur.SubDirs.Find(d => d.Name == dirName);
                if (next is null) {
                    next = new DirBuilderNode(dirName);
                    cur.SubDirs.Add(next);
                }
                cur = next;
            }
            cur.ImageIndices.Add(i);
        }

        var dir = new WzWriter(images.Count * 32 + 32);
        dir.U16(WzCrypto.EncVersion(version));

        var imgOffsetFields = new int[images.Count];
        WriteDirTableRecursive(dir, rootDir, images, imgOffsetFields);

        PatchDirOffsetsRecursive(dir, rootDir, fstart, vh);

        int pos = (int) fstart + dir.Length;
        for (int i = 0; i < images.Count; i++) {
            uint fieldPos = (uint) ((int) fstart + imgOffsetFields[i]);
            uint enc = WzCrypto.EncryptOffset(fieldPos, fstart, vh, (uint) pos);
            dir.PatchU32(imgOffsetFields[i], enc);
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
            ReadOnlySpan<byte> imgSpan = images[i].Bytes.Span;
            imgSpan.CopyTo(outSpan.Slice(cursor, imgSpan.Length));
            cursor += imgSpan.Length;
        }

        return output;

        static void WriteDirTableRecursive(
            WzWriter w,
            DirBuilderNode node,
            IReadOnlyList<WzImageEntry> imgs,
            int[] imgFields) {
            node.TableStartPos = w.Length;
            int totalEntries = node.SubDirs.Count + node.ImageIndices.Count;
            w.CompressedI32(totalEntries);

            foreach (DirBuilderNode sub in node.SubDirs) {
                w.U8(3);
                w.String(sub.Name);
                w.CompressedI32(0);
                w.CompressedI32(0);
                sub.OffsetFieldPos = w.ReserveU32();
            }

            foreach (int idx in node.ImageIndices) {
                WzImageEntry img = imgs[idx];
                int lastSlash = img.Name.LastIndexOf('/');
                string leafName = lastSlash >= 0 ? img.Name[(lastSlash + 1)..] : img.Name;
                ReadOnlySpan<byte> imgSpan = img.Bytes.Span;
                w.U8(4);
                w.String(leafName);
                w.CompressedI32(imgSpan.Length);
                w.CompressedI32(WzCrypto.Checksum(imgSpan));
                imgFields[idx] = w.ReserveU32();
            }

            foreach (DirBuilderNode sub in node.SubDirs) {
                WriteDirTableRecursive(w, sub, imgs, imgFields);
            }
        }

        static void PatchDirOffsetsRecursive(WzWriter w, DirBuilderNode node, uint fstart, uint vh) {
            foreach (DirBuilderNode sub in node.SubDirs) {
                uint fieldPos = (uint) ((int) fstart + sub.OffsetFieldPos);
                uint targetPos = (uint) ((int) fstart + sub.TableStartPos);
                uint enc = WzCrypto.EncryptOffset(fieldPos, fstart, vh, targetPos);
                w.PatchU32(sub.OffsetFieldPos, enc);
                PatchDirOffsetsRecursive(w, sub, fstart, vh);
            }
        }
    }

    public static List<WzImageEntry> ArchiveImages(WzArchive archive) {
        ArgumentNullException.ThrowIfNull(archive);
        var output = new List<WzImageEntry>(archive.Root.Children.Count);
        CollectImages(archive, archive.Root, "", output);
        return output;

        static void CollectImages(WzArchive archive, WzNode parent, string prefix, List<WzImageEntry> acc) {
            foreach (WzNode node in parent.Children) {
                string fullPath = string.IsNullOrEmpty(prefix) ? node.Name : $"{prefix}/{node.Name}";
                if (node.IsDir) {
                    CollectImages(archive, node, fullPath, acc);
                } else {
                    acc.Add(new WzImageEntry(fullPath, archive.GetImageMemory(node)));
                }
            }
        }
    }

    public static List<WzImageEntry> MergeImages(
        IEnumerable<WzImageEntry> baseImages,
        IEnumerable<WzImageEntry> additions,
        IReadOnlySet<string>? removals = null) {
        ArgumentNullException.ThrowIfNull(baseImages);
        ArgumentNullException.ThrowIfNull(additions);

        var byName = new SortedDictionary<string, WzImageEntry>(StringComparer.Ordinal);
        foreach (WzImageEntry a in additions) {
            if (removals is null || !removals.Contains(a.Name)) {
                byName[a.Name] = a;
            }
        }

        var output = new List<WzImageEntry>();
        foreach (WzImageEntry b in baseImages) {
            if (removals is not null && removals.Contains(b.Name)) {
                byName.Remove(b.Name);
                continue;
            }
            if (byName.Remove(b.Name, out WzImageEntry? replacement)) {
                output.Add(replacement);
            } else {
                output.Add(b);
            }
        }

        output.AddRange(byName.Values);
        return output;
    }

    public static void VerifyArchive(ReadOnlySpan<byte> bytes, ushort version, IReadOnlyList<WzImageEntry> images) {
        ArgumentNullException.ThrowIfNull(images);

        for (int i = 0; i < images.Count; i++) {
            if (images[i].Name.Contains('/')) {
                using WzArchive ar = WzArchive.FromBytes(bytes.ToArray(), "Verify", version);
                foreach (WzImageEntry img in images) {
                    WzNode? node = ar[img.Name];
                    if (node is null || !node.IsImage || !ar.GetImageSpan(node).SequenceEqual(img.Bytes.Span)) {
                        throw WzException.OutOfBounds(img.Name, 0, img.Bytes.Length, bytes.Length);
                    }
                }
                return;
            }
        }

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
            ReadOnlySpan<byte> expectedSpan = img.Bytes.Span;

            if (name != img.Name || size != expectedSpan.Length || sum != WzCrypto.Checksum(expectedSpan)) {
                throw WzException.OutOfBounds(name, off, size, bytes.Length);
            }

            if (off + size > bytes.Length || !bytes.Slice((int) off, size).SequenceEqual(expectedSpan)) {
                throw WzException.OutOfBounds(name, off, size, bytes.Length);
            }
        }
    }
}
