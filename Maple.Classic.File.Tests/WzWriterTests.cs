using System.Buffers.Binary;
using Maple.Classic.File.IO;
using Maple.Classic.File.IO.Archive;
using Maple.Classic.File.IO.Crypto;
using Maple.Classic.File.IO.Serialization;
using Maple.Classic.File.IO.Values;
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
        var img = new WzValue.Object {
            ["30/stand1/0/arm"] = 1,
            ["32"] = new WzValue.Uol("30"),
        };

        byte[] bytes = img.ToBytes();
        WzValue back = WzValue.Parse(bytes);

        var uol = Assert.IsType<WzValue.Uol>(back["32"]);
        Assert.Equal("30", uol.Value);
        Assert.IsType<WzValue.Object>(back["30/stand1"]);
        Assert.Equal("30", back["32"]?.AsString());
    }

    [Fact]
    public void OutlinkedStubs_AreInlinedAndDanglingLinkFails() {
        static WzValue.Canvas Stub(string link) => new(
            Width: 1,
            Height: 1,
            Format: WzCanvasFormat.Bgra4444,
            Payload: new byte[10],
            Properties: [
                new WzProperty("origin", (-35, 11)),
                new WzProperty("_outlink", link),
            ]);

        var img = new WzValue.Object {
            ["5002828/stand0/0"] = Stub("Character/PetEquip/_Canvas/01802653.img/5002828/stand0/0"),
            ["5002828/stand0/1"] = Stub("Character/PetEquip/_Canvas/01802653.img/5002828/stand0/0"),
            ["info/tuc"] = 8,
        };

        // Also verify inlining directly against a WzArchive!
        byte[] targetPayload = Enumerable.Repeat((byte) 7, 226).ToArray();
        var canvasImg = new WzValue.Object {
            ["5002828/stand0/0"] = new WzValue.Canvas(31, 24, WzCanvasFormat.Bgra4444, targetPayload),
        };
        using var canvasArchive = new WzArchive();
        canvasArchive.SetImage("01802653.img", canvasImg);
        using WzArchive builtCanvasArchive = WzArchive.FromBytes(canvasArchive.ToBytes());

        int n = img.InlineOutlinks(builtCanvasArchive);
        Assert.Equal(2, n);

        var canvas = Assert.IsType<WzValue.Canvas>(img["5002828/stand0/1"]);
        Assert.Equal((31, 24, 226), (canvas.Width, canvas.Height, canvas.Payload.Length));
        Assert.NotNull(canvas["origin"]);
        Assert.Null(canvas["_outlink"]);

        WzValue back = WzValue.Parse(img.ToBytes());
        var canvases = back.Canvases();
        Assert.Equal(2, canvases.Count);
        Assert.All(canvases, c => {
            Assert.Equal(31, c.Width);
            Assert.Equal(226, c.PayloadLength);
        });

        var dangling = new WzValue.Object {
            ["x/0"] = Stub("Character/PetEquip/_Canvas/nowhere.img/x/0"),
        };
        Assert.Throws<WzException>(() => dangling.InlineOutlinks(_ => null));
    }

    [Fact]
    public void CanvasPixels_AreReplacedInPlaceAndNothingElseBecomesACanvas() {
        var img = new WzValue.Object {
            ["info/icon"] = new WzValue.Canvas(
                1,
                1,
                WzCanvasFormat.Bgra4444,
                new byte[10],
                [new WzProperty("origin", (-1, 30))]),
            ["info/cash"] = 1,
        };

        byte[] newPayload = Enumerable.Repeat((byte) 9, 44).ToArray();
        Assert.True(img.ReplaceCanvasPixels("info/icon", 33, 33, WzCanvasFormat.Bgra8888, newPayload));

        var icon = Assert.IsType<WzValue.Canvas>(img["info/icon"]);
        Assert.Equal((33, WzCanvasFormat.Bgra8888, 44), (icon.Width, icon.Format, icon.Payload.Length));
        Assert.Equal((-1, 30), icon["origin"]?.AsVector());

        Assert.False(img.ReplaceCanvasPixels("info/cash", 1, 1, WzCanvasFormat.Bgra4444, ReadOnlyMemory<byte>.Empty));
        Assert.Equal(1, img["info/cash"]?.AsInt());
        Assert.False(img.ReplaceCanvasPixels("info/nothing", 1, 1, WzCanvasFormat.Bgra4444, ReadOnlyMemory<byte>.Empty));

        WzValue back = WzValue.Parse(img.ToBytes());
        Assert.Equal([("/info/icon", 33, 33, WzCanvasFormat.Bgra8888, 44)], back.Canvases());
    }

    [Fact]
    public void Remove_TakesOneLeafAndReportsWhetherItWasThere() {
        var img = new WzValue.Object {
            ["info/chatBalloon"] = 911,
            ["info/nameTag"] = 913,
            ["info/cash"] = 1,
        };

        Assert.True(img.Remove("info/chatBalloon"));
        Assert.False(img.Remove("info/chatBalloon"));
        Assert.False(img.Remove("nothing/here"));
        Assert.Null(img["info/chatBalloon"]);
        Assert.Equal(913, img["info/nameTag"]?.AsInt());
        Assert.Equal(1, img["info/cash"]?.AsInt());

        // Setting null via indexer also removes a property
        img["info/nameTag"] = null;
        Assert.Null(img["info/nameTag"]);
        Assert.Single(img["info"]!.Children!);
    }

    [Fact]
    public void IndexerSetter_AddsNestedStringLeaf() {
        var root = new WzValue.Object {
            ["ClassicWorld/Coat"] = new WzValue.Object(),
            ["ClassicWorld/Longcoat/1054555/name"] = "Frieren's Clothes",
            ["ClassicWorld/Coat/1040021/name"] = "Blue Sergeant",
        };

        byte[] bytes = root.ToBytes();
        WzValue back = WzValue.Parse(bytes);

        Assert.Equal("Frieren's Clothes", back["ClassicWorld/Longcoat/1054555/name"]?.AsString());
        Assert.NotNull(back["ClassicWorld/Coat/1040021"]);
    }

    [Fact]
    public void ArchiveSaveAndRoundTrip_OpensDetectsVersion779AndMatchesByteForByte() {
        var img1 = new WzValue.Object {
            ["info/islot"] = "Ma",
            ["info/reqLevel"] = 10,
            ["info/icon"] = new WzValue.Canvas(
                28,
                28,
                WzCanvasFormat.Bgra4444,
                Enumerable.Range(0, 120).Select(i => (byte) i).ToArray(),
                [new WzProperty("origin", (-2, 28))]),
        };

        var img2 = new WzValue.Object {
            ["01040000/name"] = "White Undershirt",
            ["01040000/desc"] = "Basic coat",
        };

        string tempDir = Path.Combine(Path.GetTempPath(), $"wz-writer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            string path = Path.Combine(tempDir, "Coat_000.wz");

            using (var archive = new WzArchive()) {
                archive.SetImage("01040000.img", img1);
                archive.SetImage("String.img", img2);
                archive.Save(path);
            }

            using var reopened = new WzArchive(path);
            Assert.Equal((ushort) 779, reopened.Version);
            Assert.Equal(0x3Cu, reopened.Header.FStart);
            Assert.Equal(2, reopened.Nodes.Count);

            foreach (WzNode node in reopened.Nodes) {
                WzValue parsed = node.ParseImage();
                byte[] reserialized = parsed.ToBytes();
                WzValue reparsed = WzValue.Parse(reserialized);
                Assert.Equal(parsed, reparsed);
                Assert.Equal(parsed.ToJson(), reparsed.ToJson());
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
        Assert.Equal("22"u8.ToArray(), merged[1].Bytes.ToArray());
    }
}
