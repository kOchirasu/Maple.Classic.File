namespace Maple.Classic.File.IO.Archive;

public sealed record WzHeader(ulong FSize, uint FStart, string Copyright, ushort EncVer = 0);
