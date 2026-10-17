using System.Text;
using Maple.Classic.File.IO;
using Maple.Classic.File.IO.Crypto;
using Xunit;

namespace Maple.Classic.File.Tests;

public class WzArchiveTests {
    [Fact]
    public void ParseHeader_BadMagic_ThrowsBadMagicException() {
        byte[] bad = Encoding.ASCII.GetBytes("NOPE0123456789ABCDEF");
        WzException ex = Assert.Throws<WzException>(() => WzArchive.ParseHeader(bad));
        Assert.Equal(WzErrorKind.BadMagic, ex.Kind);
    }

    [Fact]
    public void EmptyStubArchive_DetectsClientVersion779() {
        // 63-byte stub archive with entry count = 0 (like String/String.wz).
        byte[] archiveBytes = WzWriter.WriteArchive(WzCrypto.ClientVersion, []);
        Assert.Equal(63, archiveBytes.Length);

        using WzArchive ar = WzArchive.FromBytes(archiveBytes, "String");
        Assert.Equal(WzCrypto.ClientVersion, ar.Version);
        Assert.Equal(WzCrypto.ClientVersionHash, ar.VersionHash);
        Assert.Equal((ushort) 34, ar.Header.EncVer);
        Assert.Equal(0x3Cu, ar.Header.FStart);
        Assert.Empty(ar.Root.Children);
        Assert.Equal((0, 0), ar.Root.CountRecursive());
    }

    [Fact]
    public void DirectoryTree_SupportsNestedDirectoriesPaddingAndType2Dereference() {
        // Build a synthetic archive containing:
        // - type 1 padding entry (10 skipped bytes)
        // - type 3 subdirectory "SubDir" containing "Child.img"
        // - type 2 dereferenced entry pointing to an earlier type-4 "Ref.img" name
        const uint fstart = 0x3C;
        uint vh = WzCrypto.VersionHash(779);

        var rootImage = new WzOwnedValue.Object();
        rootImage.SetPath("info/id", new WzOwnedValue.Int(100));
        byte[] imgBytes = rootImage.SerializeImage();

        // Build header up to fstart (60 bytes)
        var writer = new WzWriter();
        writer.WriteBytes(WzCrypto.Magic);
        writer.U64(0); // placeholder fsize
        writer.U32(fstart);
        writer.WriteBytes(Encoding.UTF8.GetBytes(WzCrypto.Copyright));
        writer.U8(0);
        Assert.Equal((int) fstart, writer.Length);

        // At fstart: encVer (2 bytes)
        writer.U16(WzCrypto.EncVersion(779));

        // Root directory: 3 entries (1 padding, 1 subdir, 1 type-4 image)
        writer.CompressedI32(3);

        // Entry 0: type 1 (skip 10 bytes)
        writer.U8(1);
        writer.WriteBytes(new byte[10]);

        // Entry 1: type 3 ("SubDir")
        writer.U8(3);
        writer.String("SubDir");
        writer.CompressedI32(0); // size
        writer.CompressedI32(0); // checksum
        int subDirOffsetFieldPos = writer.Length;
        writer.U32(0); // placeholder offset

        // Entry 2: type 4 ("Direct.img") - we'll also reference this name via type 2 in SubDir!
        int directTypePos = writer.Length; // points to the `4` byte
        writer.U8(4);
        writer.String("Direct.img");
        writer.CompressedI32(imgBytes.Length);
        writer.CompressedI32(WzCrypto.Checksum(imgBytes));
        int directOffsetFieldPos = writer.Length;
        writer.U32(0); // placeholder offset

        // SubDir content starts here
        int subDirStart = writer.Length;
        writer.CompressedI32(1); // 1 child in SubDir
        // Use type 2 dereference pointing back to "Direct.img":
        // Archive reader computes: at = fstart + 1 + rel, and reads real type at `data[at - 1]` and string at `at`.
        // Since `directTypePos` is the position of the type byte `4`, `at` should be `directTypePos + 1`.
        int rel = (directTypePos + 1) - ((int) fstart + 1);
        writer.U8(2);
        writer.I32(rel);
        writer.CompressedI32(imgBytes.Length);
        writer.CompressedI32(WzCrypto.Checksum(imgBytes));
        int childOffsetFieldPos = writer.Length;
        writer.U32(0);

        // Image payload starts here
        int imgStart = writer.Length;
        writer.WriteBytes(imgBytes);

        // Patch fsize and encrypted offsets
        Span<byte> span = writer.AsMutableSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            span.Slice(4, 8),
            (ulong) (writer.Length - (int) fstart));

        uint encSubDir = WzCrypto.EncryptOffset((uint) subDirOffsetFieldPos, fstart, vh, (uint) subDirStart);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(subDirOffsetFieldPos, 4), encSubDir);

        uint encDirect = WzCrypto.EncryptOffset((uint) directOffsetFieldPos, fstart, vh, (uint) imgStart);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(directOffsetFieldPos, 4), encDirect);

        uint encChild = WzCrypto.EncryptOffset((uint) childOffsetFieldPos, fstart, vh, (uint) imgStart);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(childOffsetFieldPos, 4), encChild);

        using WzArchive ar = WzArchive.FromBytes(writer.ToArray(), "Test");
        Assert.Equal(779, ar.Version);
        Assert.Equal((1, 2), ar.Root.CountRecursive());

        WzNode? subDir = ar.Root.Get("SubDir");
        Assert.NotNull(subDir);
        Assert.True(subDir.IsDir);

        WzNode? derefChild = ar.Root.Get("SubDir/Direct.img");
        Assert.NotNull(derefChild);
        Assert.True(derefChild.IsImage);

        WzValue parsed = ar.ParseImage("SubDir/Direct.img");
        Assert.Equal(100L, parsed.Get("info/id")?.AsInt64());
    }

    [Fact]
    public void ImageBytes_OnDirectoryOrOutOfBounds_ThrowsOutOfBoundsException() {
        byte[] archiveBytes = WzWriter.WriteArchive(779, []);
        using WzArchive ar = WzArchive.FromBytes(archiveBytes, "Stub");

        WzException dirEx = Assert.Throws<WzException>(() => ar.ImageBytes(ar.Root));
        Assert.Equal(WzErrorKind.OutOfBounds, dirEx.Kind);

        var badNode = new WzNode("Bad.img", WzNodeKind.Image, offset: 9999, size: 10);
        WzException oobEx = Assert.Throws<WzException>(() => ar.ImageBytes(badNode));
        Assert.Equal(WzErrorKind.OutOfBounds, oobEx.Kind);
    }

    [Fact]
    public void TreeArchive_ReturnsExpectedFilePath() {
        string path = WzArchive.TreeArchive("/data/mscw", "String");
        Assert.Equal(Path.Combine("/data/mscw", "String", "String_000.wz"), path);
    }
}
