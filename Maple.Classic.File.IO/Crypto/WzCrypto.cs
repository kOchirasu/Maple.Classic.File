using System.Globalization;
using System.Numerics;

namespace Maple.Classic.File.IO.Crypto;

/// <summary>
/// Version hashing, offset encryption/decryption, and checksum utilities for MapleStory Classic World WZ archives.
/// </summary>
public static class WzCrypto {
    /// <summary>
    /// The game version used by the MapleStory Classic World (<c>mscw</c>) client.
    /// </summary>
    public const ushort ClientVersion = 779;

    /// <summary>
    /// The version hash corresponding to <see cref="ClientVersion"/> (<c>779</c> -> <c>0x0000E73A</c>).
    /// </summary>
    public const uint ClientVersionHash = 0x0000_E73Au;

    /// <summary>
    /// The encrypted version header word corresponding to <see cref="ClientVersion"/> (<c>34</c> / <c>0x22</c>).
    /// </summary>
    public const ushort ClientEncVer = 34;

    /// <summary>
    /// Constant subtracted during WZ offset encryption/decryption across all known WZ versions.
    /// </summary>
    public const uint OffsetMagic = 0x581C_3F6Du;

    /// <summary>
    /// Starting XOR mask for ASCII/Latin-1 string obfuscation (zero key).
    /// </summary>
    public const byte AsciiMask = 0xAA;

    /// <summary>
    /// Starting XOR mask for UTF-16LE string obfuscation (zero key).
    /// </summary>
    public const ushort UnicodeMask = 0xAAAA;

    /// <summary>
    /// Standard PKG1 copyright string in WZ headers.
    /// </summary>
    public const string Copyright = "Package file v1.0 Copyright 2002 Wizet, ZMS";

    /// <summary>
    /// Standard content start offset (<c>0x3C</c>) when using <see cref="Copyright"/>.
    /// </summary>
    public const uint DefaultFStart = 0x3Cu;

    /// <summary>
    /// 4-byte file header magic (<c>"PKG1"</c>).
    /// </summary>
    public static ReadOnlySpan<byte> Magic => "PKG1"u8;

    /// <summary>
    /// Computes the full 32-bit version hash: <c>sum = sum * 32 + ch + 1</c> over the decimal digits of <paramref name="version"/>.
    /// </summary>
    public static uint VersionHash(ushort version) {
        uint sum = 0;
        foreach (char ch in version.ToString(CultureInfo.InvariantCulture)) {
            sum = unchecked(sum * 32u + ch + 1u);
        }
        return sum;
    }

    /// <summary>
    /// Computes the <c>encVer</c> 16-bit value a given <paramref name="version"/> produces for matching against the file header.
    /// </summary>
    public static ushort EncVersion(ushort version) {
        uint h = VersionHash(version);
        uint x = 0xFFu
            ^ ((h >> 24) & 0xFFu)
            ^ ((h >> 16) & 0xFFu)
            ^ ((h >> 8) & 0xFFu)
            ^ (h & 0xFFu);
        return (ushort) x;
    }

    /// <summary>
    /// Decrypts a directory entry offset located at <paramref name="fieldPos"/>.
    /// </summary>
    public static uint DecryptOffset(uint fieldPos, uint fstart, uint versionHash, uint encrypted) {
        uint o = unchecked(fieldPos - fstart) ^ uint.MaxValue;
        o = unchecked(o * versionHash);
        o = unchecked(o - OffsetMagic);
        o = BitOperations.RotateLeft(o, (int) (o & 0x1Fu));
        o ^= encrypted;
        o = unchecked(o + unchecked(fstart * 2u));
        return o;
    }

    /// <summary>
    /// Encrypts an image or directory offset for a directory entry whose offset field sits at <paramref name="fieldPos"/>.
    /// Exact inverse of <see cref="DecryptOffset"/>.
    /// </summary>
    public static uint EncryptOffset(uint fieldPos, uint fstart, uint versionHash, uint real) {
        uint o = unchecked(fieldPos - fstart) ^ uint.MaxValue;
        o = unchecked(o * versionHash);
        o = unchecked(o - OffsetMagic);
        o = BitOperations.RotateLeft(o, (int) (o & 0x1Fu));
        return o ^ unchecked(real - unchecked(fstart * 2u));
    }

    /// <summary>
    /// Computes the directory entry checksum: the wrapping byte sum of the image payload as a 32-bit signed integer.
    /// </summary>
    public static int Checksum(ReadOnlySpan<byte> bytes) {
        int sum = 0;
        foreach (byte b in bytes) {
            sum = unchecked(sum + b);
        }
        return sum;
    }
}
