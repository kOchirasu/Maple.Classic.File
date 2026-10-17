namespace Maple.Classic.File.IO;

public enum WzErrorKind {
    Io,
    BadMagic,
    UnexpectedEof,
    BadEntryType,
    BadPropertyType,
    BadExtendedType,
    OutOfBounds,
    VersionNotFound,
    BadUtf16,
}

public class WzException : Exception {
    public WzErrorKind Kind { get; }
    public int? Offset { get; internal init; }
    public string? TargetName { get; internal init; }
    public string? FilePath { get; internal init; }
    internal byte? TypeByte { get; init; }
    internal ushort? EncVer { get; init; }

    internal WzException(WzErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException) {
        Kind = kind;
    }

    internal static WzException Io(string path, Exception inner) {
        return new WzException(WzErrorKind.Io, $"io error on {path}: {inner.Message}", inner) {
            FilePath = path,
        };
    }

    internal static WzException BadMagic(ReadOnlySpan<byte> found) {
        string hex = $"[{found[0]:X2}, {found[1]:X2}, {found[2]:X2}, {found[3]:X2}]";
        return new WzException(WzErrorKind.BadMagic, $"not a WZ archive: expected magic PKG1, found {hex}");
    }

    internal static WzException UnexpectedEof(int offset, int need, int have) {
        return new WzException(
            WzErrorKind.UnexpectedEof,
            $"unexpected end of data at offset {offset} (need {need} bytes, {have} available)") {
            Offset = offset,
        };
    }

    internal static WzException BadEntryType(byte kind, int offset) {
        return new WzException(
            WzErrorKind.BadEntryType,
            $"unknown directory entry type {kind} at offset {offset}") {
            TypeByte = kind,
            Offset = offset,
        };
    }

    internal static WzException BadPropertyType(byte kind, int offset) {
        return new WzException(
            WzErrorKind.BadPropertyType,
            $"unknown property type {kind} at offset {offset}") {
            TypeByte = kind,
            Offset = offset,
        };
    }

    internal static WzException BadExtendedType(string name, int offset) {
        return new WzException(
            WzErrorKind.BadExtendedType,
            $"unknown extended property \"{name}\" at offset {offset}") {
            TargetName = name,
            Offset = offset,
        };
    }

    internal static WzException OutOfBounds(string name, uint offset, int size, int len) {
        return new WzException(
            WzErrorKind.OutOfBounds,
            $"entry \"{name}\" points out of bounds (offset {offset}, size {size}, file {len})") {
            TargetName = name,
            Offset = (int) offset,
        };
    }

    internal static WzException VersionNotFound(ushort encVer) {
        return new WzException(
            WzErrorKind.VersionNotFound,
            $"could not determine version: no candidate matched encVer {encVer}") {
            EncVer = encVer,
        };
    }

    internal static WzException BadUtf16(int offset) {
        return new WzException(
            WzErrorKind.BadUtf16,
            $"invalid UTF-16 in string at offset {offset}") {
            Offset = offset,
        };
    }
}
