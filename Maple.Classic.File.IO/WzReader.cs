using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Maple.Classic.File.IO.Crypto;

namespace Maple.Classic.File.IO;

/// <summary>
/// Internal zero-allocation stack cursor over a WZ archive or image span.
/// </summary>
internal ref struct WzReader(ReadOnlySpan<byte> data, uint fstart = 0, uint versionHash = 0) {
    public ReadOnlySpan<byte> Data { get; } = data;
    public int Position { get; private set; }
    public uint FStart { get; } = fstart;
    public uint VersionHash { get; } = versionHash;

    public readonly int Remaining => Math.Max(0, Data.Length - Position);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Seek(int pos) => Position = pos;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void Need(int n) {
        if (n < 0 || Remaining < n) {
            throw WzException.UnexpectedEof(Position, Math.Max(0, n), Remaining);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> Take(int n) {
        Need(n);
        ReadOnlySpan<byte> slice = Data.Slice(Position, n);
        Position += n;
        return slice;
    }

    public byte[] ReadBytes(int n) => [.. Take(n)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte() {
        Need(1);
        return Data[Position++];
    }

    public byte U8() => ReadByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public sbyte ReadSByte() => unchecked((sbyte) ReadByte());

    public sbyte I8() => ReadSByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public ushort U16() => ReadUInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public short I16() => ReadInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public uint U32() => ReadUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public int I32() => ReadInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public ulong U64() => ReadUInt64();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public long I64() => ReadInt64();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadSingle() => BitConverter.UInt32BitsToSingle(ReadUInt32());

    public float F32() => ReadSingle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadUInt64());

    public double F64() => ReadDouble();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadCompressedInt32() {
        sbyte b = ReadSByte();
        return b == sbyte.MinValue ? ReadInt32() : b;
    }

    public int CompressedI32() => ReadCompressedInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadCompressedInt64() {
        sbyte b = ReadSByte();
        return b == sbyte.MinValue ? unchecked((long) ReadUInt64()) : b;
    }

    public long CompressedI64() => ReadCompressedInt64();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadCompressedSingle() {
        byte b = ReadByte();
        return b == 0x80 ? ReadSingle() : 0.0f;
    }

    public float CompressedF32() => ReadCompressedSingle();

    /// <summary>
    /// Validates and skips an obfuscated string without allocating a <see cref="string"/> on the heap.
    /// Used during fast directory probing in version detection.
    /// </summary>
    public void SkipString() {
        sbyte tag = ReadSByte();
        if (tag == 0) {
            return;
        }

        if (tag < 0) {
            int n = tag == sbyte.MinValue ? ReadInt32() : -tag;
            Take(n);
        } else {
            int n = tag == 127 ? ReadInt32() : tag;
            if (n is < 0 or > int.MaxValue / 2) {
                throw WzException.UnexpectedEof(Position, int.MaxValue, Remaining);
            }
            Take(n * 2);
        }
    }

    public string ReadString() {
        sbyte tag = ReadSByte();
        if (tag == 0) {
            return string.Empty;
        }

        if (tag < 0) {
            int n = tag == sbyte.MinValue ? ReadInt32() : -tag;
            if (n < 0) {
                throw WzException.UnexpectedEof(Position, int.MaxValue, Remaining);
            }

            ReadOnlySpan<byte> raw = Take(n);
            return string.Create(n, raw, static (chars, src) => {
                for (int k = 0; k < chars.Length; k++) {
                    chars[k] = (char) (src[k] ^ unchecked((byte) (WzCrypto.ASCII_MASK + (byte) k)));
                }
            });
        } else {
            int n = tag == 127 ? ReadInt32() : tag;
            if (n is < 0 or > int.MaxValue / 2) {
                throw WzException.UnexpectedEof(Position, int.MaxValue, Remaining);
            }

            int start = Position;
            ReadOnlySpan<byte> raw = Take(n * 2);
            string result = string.Create(n, raw, static (units, src) => {
                for (int k = 0; k < units.Length; k++) {
                    ushort u = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(k * 2, 2));
                    units[k] = (char) (u ^ unchecked((ushort) (WzCrypto.UNICODE_MASK + (ushort) k)));
                }
            });

            if (!IsValidUtf16(result)) {
                throw WzException.BadUtf16(start);
            }

            return result;
        }
    }

    public string String() => ReadString();

    public string ReadStringAt(int baseOffset, ReadOnlySpan<byte> inlineTags, ReadOnlySpan<byte> derefTags) {
        byte tag = ReadByte();
        if (inlineTags.Contains(tag)) {
            return ReadString();
        }

        if (derefTags.Contains(tag)) {
            int off = (int) ReadUInt32();
            int save = Position;
            Seek(baseOffset + off);
            string s = ReadString();
            Seek(save);
            return s;
        }

        Position -= 1;
        return ReadString();
    }

    public string StringAt(int baseOffset, ReadOnlySpan<byte> inlineTags, ReadOnlySpan<byte> derefTags) =>
        ReadStringAt(baseOffset, inlineTags, derefTags);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecryptOffset() {
        uint fieldPos = (uint) Position;
        uint encrypted = ReadUInt32();
        return WzCrypto.DecryptOffset(fieldPos, FStart, VersionHash, encrypted);
    }

    private static bool IsValidUtf16(ReadOnlySpan<char> chars) {
        for (int i = 0; i < chars.Length; i++) {
            char c = chars[i];
            if (char.IsHighSurrogate(c)) {
                if (i + 1 >= chars.Length || !char.IsLowSurrogate(chars[i + 1])) {
                    return false;
                }
                i++;
            } else if (char.IsLowSurrogate(c)) {
                return false;
            }
        }
        return true;
    }
}
