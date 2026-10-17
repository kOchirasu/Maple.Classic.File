using System.Buffers.Binary;
using Maple.Classic.File.IO;
using Maple.Classic.File.IO.Crypto;
using Xunit;

namespace Maple.Classic.File.Tests;

public class WzWriterTests {
    [Fact]
    public void Offsets_RoundTripThroughReader() {
        uint vh = WzCrypto.VersionHash(779);
        (uint FieldPos, uint Real)[] cases = [
            (0x3Eu, 0x13BBu),
            (0x1000u, 0x8874u),
            (0x2_0000u, 0x1FF_FFFFu),
        ];

        foreach ((uint fieldPos, uint real) in cases) {
            uint enc = WzCrypto.EncryptOffset(fieldPos, 0x3C, vh, real);
            byte[] bytes = new byte[fieldPos + 4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int) fieldPos, 4), enc);

            var r = new WzReader(bytes, 0x3C, vh);
            r.Seek((int) fieldPos);
            Assert.Equal(real, r.DecryptOffset());
        }
    }

    [Fact]
    public void Strings_RoundTripAndUmlautsGoUtf16() {
        string[] cases = [
            string.Empty,
            "Property",
            new string('x', 200),
            "\u00dcbel's Clothes",
            new string('A', 130) + "\u00fc",
        ];

        foreach (string s in cases) {
            var w = new WzWriter();
            w.String(s);
            byte[] buf = w.ToArray();

            var r = new WzReader(buf, 0, 0);
            Assert.Equal(s, r.ReadString());

            if (s.Any(c => c >= 0x80)) {
                Assert.True((sbyte) buf[0] > 0 || buf[0] == 127, $"Non-ASCII must be UTF-16: {s}");
            }
        }

        int[] intCases = [0, 1, -1, 127, -127, -128, 128, 100_000, int.MinValue, int.MaxValue];
        foreach (int v in intCases) {
            var w = new WzWriter();
            w.CompressedI32(v);
            var r = new WzReader(w.ToArray(), 0, 0);
            Assert.Equal(v, r.ReadCompressedInt32());
        }
    }

    [Fact]
    public void UolLeaf_RoundTripsAsLink() {
        var img = new WzOwnedValue.Object();
        img.SetPath("30/stand1/0/arm", new WzOwnedValue.Int(1));
        img.SetPath("32", new WzOwnedValue.Uol("30"));

        byte[] bytes = img.SerializeImage();
        WzOwnedValue back = WzOwnedValue.Parse(bytes);

        var uol = Assert.IsType<WzOwnedValue.Uol>(back.Get("32"));
        Assert.Equal("30", uol.Value);
        Assert.IsType<WzOwnedValue.Object>(back.Get("30")?.Get("stand1"));

        WzValue node = WzImage.Parse(bytes);
        Assert.Equal("30", node.Get("32")?.AsString());
    }

    [Fact]
    public void OutlinkedStubs_AreInlinedAndDanglingLinkFails() {
        static WzOwnedValue.Canvas Stub(string link) => new(
            width: 1,
            height: 1,
            format: 1,
            payload: new byte[10],
            entries: [
                ("origin", new WzOwnedValue.Vector(35, 11)),
                ("_outlink", new WzOwnedValue.String(link)),
            ]);

        var img = new WzOwnedValue.Object();
        img.SetPath("5002828/stand0/0", Stub("Character/PetEquip/_Canvas/01802653.img/5002828/stand0/0"));
        img.SetPath("5002828/stand0/1", Stub("Character/PetEquip/_Canvas/01802653.img/5002828/stand0/0"));
        img.SetPath("info/tuc", new WzOwnedValue.Int(8));

        var asked = new List<string>();
        byte[] targetPayload = Enumerable.Repeat((byte) 7, 226).ToArray();
        int n = img.InlineOutlinks(link => {
            asked.Add(link);
            return new WzOwnedValue.Canvas(31, 24, 1, targetPayload);
        });

        Assert.Equal(2, n);
        Assert.Equal(2, asked.Count);

        var canvas = Assert.IsType<WzOwnedValue.Canvas>(img.GetPath("5002828/stand0/1"));
        Assert.Equal((31, 24, 226), (canvas.Width, canvas.Height, canvas.Payload.Length));
        Assert.Contains(canvas.Entries, e => e.Name == "origin");
        Assert.DoesNotContain(canvas.Entries, e => e.Name == "_outlink");

        WzOwnedValue back = WzOwnedValue.Parse(img.SerializeImage());
        var canvases = back.Canvases();
        Assert.Equal(2, canvases.Count);
        Assert.All(canvases, c => {
            Assert.Equal(31, c.Width);
            Assert.Equal(226, c.PayloadLength);
        });

        var dangling = new WzOwnedValue.Object();
        dangling.SetPath("x/0", Stub("Character/PetEquip/_Canvas/nowhere.img/x/0"));
        Assert.Throws<WzException>(() => dangling.InlineOutlinks(_ => null));
    }

    [Fact]
    public void CanvasPixels_AreReplacedInPlaceAndNothingElseBecomesACanvas() {
        var img = new WzOwnedValue.Object();
        img.SetPath(
            "info/icon",
            new WzOwnedValue.Canvas(
                1,
                1,
                1,
                new byte[10],
                [("origin", new WzOwnedValue.Vector(-1, 30))]));
        img.SetPath("info/cash", new WzOwnedValue.Int(1));

        byte[] newPayload = Enumerable.Repeat((byte) 9, 44).ToArray();
        Assert.True(img.ReplaceCanvasPixels("info/icon", 33, 33, 2, newPayload));

        var icon = Assert.IsType<WzOwnedValue.Canvas>(img.GetPath("info/icon"));
        Assert.Equal((33, 2, 44), (icon.Width, icon.Format, icon.Payload.Length));
        Assert.Single(icon.Entries);
        Assert.Equal(("origin", new WzOwnedValue.Vector(-1, 30)), icon.Entries[0]);

        Assert.False(img.ReplaceCanvasPixels("info/cash", 1, 1, 1, []));
        Assert.Equal(new WzOwnedValue.Int(1), img.GetPath("info/cash"));
        Assert.False(img.ReplaceCanvasPixels("info/nothing", 1, 1, 1, []));

        WzOwnedValue back = WzOwnedValue.Parse(img.SerializeImage());
        Assert.Equal([("/info/icon", 33, 33, 2, 44)], back.Canvases());
    }

    [Fact]
    public void RemovePath_TakesOneLeafAndReportsWhetherItWasThere() {
        var img = new WzOwnedValue.Object();
        img.SetPath("info/chatBalloon", new WzOwnedValue.Int(911));
        img.SetPath("info/nameTag", new WzOwnedValue.Int(913));
        img.SetPath("info/cash", new WzOwnedValue.Int(1));

        Assert.True(img.RemovePath("info/chatBalloon"));
        Assert.False(img.RemovePath("info/chatBalloon"));
        Assert.False(img.RemovePath("nothing/here"));
        Assert.Null(img.GetPath("info/chatBalloon"));
        Assert.Equal(new WzOwnedValue.Int(913), img.GetPath("info/nameTag"));
        Assert.Equal(new WzOwnedValue.Int(1), img.GetPath("info/cash"));
        Assert.Equal(2, img.GetPath("info")?.Children?.Count);
    }

    [Fact]
    public void SetPath_AddsNestedStringLeaf() {
        var root = new WzOwnedValue.Object([
            ("ClassicWorld", new WzOwnedValue.Object([
                ("Coat", new WzOwnedValue.Object()),
            ])),
        ]);
        root.SetPath("ClassicWorld/Longcoat/1054555/name", new WzOwnedValue.String("Frieren's Clothes"));
        root.SetPath("ClassicWorld/Coat/1040021/name", new WzOwnedValue.String("Blue Sergeant"));

        byte[] bytes = root.SerializeImage();
        WzOwnedValue back = WzOwnedValue.Parse(bytes);

        WzOwnedValue? name = back.Get("ClassicWorld")?.Get("Longcoat")?.Get("1054555")?.Get("name");
        Assert.Equal(new WzOwnedValue.String("Frieren's Clothes"), name);
        Assert.NotNull(back.Get("ClassicWorld")?.Get("Coat")?.Get("1040021"));
    }

    [Fact]
    public void WrittenArchive_OpensDetectsVersion779AndMatchesByteForByte() {
        var img1 = new WzOwnedValue.Object();
        img1.SetPath("info/islot", new WzOwnedValue.String("Ma"));
        img1.SetPath("info/reqLevel", new WzOwnedValue.Int(10));
        img1.SetPath(
            "info/icon",
            new WzOwnedValue.Canvas(
                28,
                28,
                1,
                Enumerable.Range(0, 120).Select(i => (byte) i).ToArray(),
                [("origin", new WzOwnedValue.Vector(-2, 28))]));

        var img2 = new WzOwnedValue.Object();
        img2.SetPath("01040000/name", new WzOwnedValue.String("White Undershirt"));
        img2.SetPath("01040000/desc", new WzOwnedValue.String("Basic coat"));

        List<WzImageEntry> images = [
            new WzImageEntry("01040000.img", img1.SerializeImage()),
            new WzImageEntry("String.img", img2.SerializeImage()),
        ];

        byte[] archiveBytes = WzWriter.WriteArchive(779, images);
        WzWriter.VerifyArchive(archiveBytes, 779, images);

        string tempDir = Path.Combine(Path.GetTempPath(), $"wz-writer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            string path = Path.Combine(tempDir, "Coat_000.wz");
            System.IO.File.WriteAllBytes(path, archiveBytes);

            using WzArchive again = WzArchive.Open(path);
            Assert.Equal((ushort) 779, again.Version);
            Assert.Equal(0x3Cu, again.Header.FStart);
            Assert.Equal((ulong) (archiveBytes.Length - 0x3C), again.Header.FSize);
            Assert.Equal(images.Count, again.Root.Children.Count);

            for (int i = 0; i < images.Count; i++) {
                WzNode node = again.Root.Children[i];
                WzImageEntry expected = images[i];
                Assert.Equal(expected.Name, node.Name);
                Assert.Equal(WzCrypto.Checksum(expected.Bytes), node.Checksum);
                Assert.Equal(expected.Bytes, again.ImageBytes(node));
            }

            // Round-trip through Owned -> Serialize -> Parse and compare JSON
            foreach (WzNode node in again.Root.Children) {
                byte[] origBytes = again.ImageBytes(node);
                WzOwnedValue owned = WzOwnedValue.Parse(origBytes);
                byte[] reserialized = owned.SerializeImage();
                WzOwnedValue reparsed = WzOwnedValue.Parse(reserialized);
                Assert.Equal(owned, reparsed);
                Assert.Equal(
                    WzImage.ToJson(WzImage.Parse(origBytes)),
                    WzImage.ToJson(WzImage.Parse(reserialized)));
            }
        } finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void MergeImages_ReplacesInPlaceAndAppendsSorted() {
        static WzImageEntry E(string n, string content) =>
            new(n, System.Text.Encoding.ASCII.GetBytes(content));

        List<WzImageEntry> baseImages = [E("b.img", "1"), E("a.img", "2"), E("c.img", "3")];
        List<WzImageEntry> add = [E("z.img", "9"), E("a.img", "22"), E("d.img", "4")];

        List<WzImageEntry> merged = WzWriter.MergeImages(baseImages, add);
        Assert.Equal(["b.img", "a.img", "c.img", "d.img", "z.img"], merged.Select(i => i.Name).ToArray());
        Assert.Equal("22"u8.ToArray(), merged[1].Bytes);
    }
}
