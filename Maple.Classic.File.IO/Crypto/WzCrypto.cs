using System.Globalization;

namespace Maple.Classic.File.IO.Crypto;

/// <summary>
/// Internal version hashing, offset encryption/decryption, and checksum utilities for WZ archives.
/// </summary>
internal static class WzCrypto {
    public const ushort ClientVersion = 779;
    public const uint ClientVersionHash = 0x0000_E73Au;
    public const ushort ClientEncVer = 34;
    public const uint OffsetMagic = 0x581C_3F6Du;
    public const byte AsciiMask = 0xAA;
    public const ushort UnicodeMask = 0xAAAA;
    public const string Copyright = "Package file v1.0 Copyright 2002 Wizet, ZMS";
    public const uint DefaultFStart = 0x3Cu;

    public static ReadOnlySpan<byte> Magic => "PKG1"u8;

    public static uint VersionHash(ushort version) {
        Span<char> digits = stackalloc char[8];
        version.TryFormat(digits, out int written, provider: CultureInfo.InvariantCulture);

        uint sum = 0;
        for (int i = 0; i < written; i++) {
            sum = unchecked(sum * 32u + digits[i] + 1u);
        }
        return sum;
    }

    public static ushort EncVersion(ushort version) {
        uint h = VersionHash(version);
        uint x = 0xFFu
            ^ ((h >> 24) & 0xFFu)
            ^ ((h >> 16) & 0xFFu)
            ^ ((h >> 8) & 0xFFu)
            ^ (h & 0xFFu);
        return (ushort) x;
    }

    public static uint DecryptOffset(uint fieldPos, uint fstart, uint versionHash, uint encrypted) {
        uint o = unchecked(fieldPos - fstart) ^ uint.MaxValue;
        o = unchecked(o * versionHash);
        o = unchecked(o - OffsetMagic);
        o = uint.RotateLeft(o, (int) (o & 0x1Fu));
        o ^= encrypted;
        o = unchecked(o + unchecked(fstart * 2u));
        return o;
    }

    public static uint EncryptOffset(uint fieldPos, uint fstart, uint versionHash, uint real) {
        uint o = unchecked(fieldPos - fstart) ^ uint.MaxValue;
        o = unchecked(o * versionHash);
        o = unchecked(o - OffsetMagic);
        o = uint.RotateLeft(o, (int) (o & 0x1Fu));
        return o ^ unchecked(real - unchecked(fstart * 2u));
    }

    public static int Checksum(ReadOnlySpan<byte> bytes) {
        int sum = 0;
        foreach (byte b in bytes) {
            sum = unchecked(sum + b);
        }
        return sum;
    }
}
