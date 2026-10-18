using System.Text;
using Maple.Classic.File.IO;
using Maple.Classic.File.IO.Archive;
using Maple.Classic.File.IO.Crypto;
using Maple.Classic.File.IO.Serialization;
using Maple.Classic.File.IO.Values;
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
        byte[] archiveBytes = WzWriter.WriteArchive(WzCrypto.CLIENT_VERSION, []);
        Assert.Equal(63, archiveBytes.Length);

        using WzArchive ar = WzArchive.FromBytes(archiveBytes, "String");
        Assert.Equal(WzArchive.DEFAULT_VERSION, ar.Version);
        Assert.Equal(WzCrypto.CLIENT_VERSION_HASH, ar.VersionHash);
        Assert.Equal((ushort) 34, ar.Header.EncVer);
        Assert.Equal(0x3Cu, ar.Header.FStart);
        Assert.Empty(ar.Nodes);
        Assert.Equal((0, 0), ar.Root.CountRecursive());
    }

    [Fact]
    public void DirectoryTree_SupportsNestedDirectoriesPaddingAndType2Dereference() {
        const uint fstart = 0x3C;
        uint vh = WzCrypto.VersionHash(779);

        var rootImage = new WzValue.Object {
            ["info/id"] = 100,
        };
        byte[] imgBytes = rootImage.ToBytes();

        // Build header up to fstart (60 bytes)
        var writer = new WzWriter();
        writer.WriteBytes(WzCrypto.Magic);
        writer.U64(0); // placeholder fsize
        writer.U32(fstart);
        writer.WriteBytes(Encoding.UTF8.GetBytes(WzCrypto.COPYRIGHT));
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
        writer.CompressedI32(0);
        writer.CompressedI32(0);
        int subDirOffsetFieldPos = writer.Length;
        writer.U32(0);

        // Entry 2: type 4 ("Direct.img")
        int directTypePos = writer.Length;
        writer.U8(4);
        writer.String("Direct.img");
        writer.CompressedI32(imgBytes.Length);
        writer.CompressedI32(WzCrypto.Checksum(imgBytes));
        int directOffsetFieldPos = writer.Length;
        writer.U32(0);

        // SubDir content starts here
        int subDirStart = writer.Length;
        writer.CompressedI32(1);
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

        WzNode? subDir = ar["SubDir"];
        Assert.NotNull(subDir);
        Assert.True(subDir.IsDir);

        WzNode? derefChild = ar["SubDir/Direct.img"];
        Assert.NotNull(derefChild);
        Assert.True(derefChild.IsImage);

        // Parse directly from node or via sub-path into the image
        Assert.Equal(100, derefChild.ParseImage()["info/id"]?.AsInt());
        Assert.Equal(100, (int) ar.ParseImage("SubDir/Direct.img/info/id"));
    }

    [Fact]
    public void ImageBytes_OnDirectoryOrOutOfBounds_ThrowsOutOfBoundsException() {
        using var ar = new WzArchive();

        WzException dirEx = Assert.Throws<WzException>(() => ar.ImageBytes(ar.Root));
        Assert.Equal(WzErrorKind.OutOfBounds, dirEx.Kind);

        var badNode = new WzNode("Bad.img", WzNodeKind.Image, offset: 9999, size: 10);
        WzException oobEx = Assert.Throws<WzException>(() => ar.ImageBytes(badNode));
        Assert.Equal(WzErrorKind.OutOfBounds, oobEx.Kind);
    }

    [Fact]
    public void TreeArchivePath_ReturnsExpectedFilePath() {
        string path = WzArchive.TreeArchivePath("/data/mscw", "String");
        Assert.Equal(Path.Combine("/data/mscw", "String", "String_000.wz"), path);
    }

    [Fact]
    public void DirectoryOnlyIndexArchive_DetectsClientVersion779() {
        const uint fstart = 0x3C;
        uint vh = WzCrypto.VersionHash(WzCrypto.CLIENT_VERSION);
        string[] dirs = ["Cash", "Consume", "Etc", "Install", "Pet", "Special"];

        var writer = new WzWriter();
        writer.WriteBytes(WzCrypto.Magic);
        writer.U64(0);
        writer.U32(fstart);
        writer.WriteBytes(Encoding.UTF8.GetBytes(WzCrypto.COPYRIGHT));
        writer.U8(0);
        writer.U16(WzCrypto.EncVersion(WzCrypto.CLIENT_VERSION));
        writer.CompressedI32(dirs.Length);

        var offsetFields = new int[dirs.Length];
        for (int i = 0; i < dirs.Length; i++) {
            writer.U8(3);
            writer.String(dirs[i]);
            writer.CompressedI32(0);
            writer.CompressedI32(0);
            offsetFields[i] = writer.Length;
            writer.U32(0);
        }

        // In MSCW index .wz archives (such as Item.wz / Base.wz), the file ends immediately after the root table.
        uint eofOffset = (uint) writer.Length;
        Span<byte> span = writer.AsMutableSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            span.Slice(4, 8),
            (ulong) (writer.Length - (int) fstart));
        for (int i = 0; i < dirs.Length; i++) {
            uint enc = WzCrypto.EncryptOffset((uint) offsetFields[i], fstart, vh, eofOffset);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offsetFields[i], 4), enc);
        }

        using WzArchive ar = WzArchive.FromBytes(writer.ToArray(), "Item");
        Assert.Equal(WzCrypto.CLIENT_VERSION, ar.Version);
        Assert.Equal(6, ar.Nodes.Count);
        Assert.All(ar.Nodes, n => Assert.True(n.IsDir));
    }
}
