using Maple.Classic.File.IO;
using Xunit;

namespace Maple.Classic.File.Tests;

public class WzImageTests {
    [Fact]
    public void ParseImage_AllPropertyTypes_DeserializesAndRendersJson() {
        // Construct an image exercising all WZ property types:
        // Null (0x00), Short (0x02), Int (0x03), Long (0x14), Float (0x04), Double (0x05),
        // String (0x08), and Extended (0x09): Property, Canvas, Shape2D#Vector2D, Shape2D#Convex2D,
        // Sound_DX8, UOL, RawData.
        var w = new WzWriter();
        w.TypeString("Property");
        w.U16(0);
        w.CompressedI32(12);

        // 1. Null
        w.ImageString("nullProp");
        w.U8(0x00);

        // 2. Short
        w.ImageString("shortProp");
        w.U8(0x02);
        w.I16(-320);

        // 3. Int
        w.ImageString("intProp");
        w.U8(0x03);
        w.CompressedI32(123456);

        // 4. Long
        w.ImageString("longProp");
        w.U8(0x14);
        w.CompressedI64(9876543210L);

        // 5. Float
        w.ImageString("floatProp");
        w.U8(0x04);
        w.CompressedF32(1.5f);

        // 6. Double
        w.ImageString("doubleProp");
        w.U8(0x05);
        w.F64(42.0);

        // 7. String
        w.ImageString("strProp");
        w.U8(0x08);
        w.ImageString("Hello\nWorld");

        // 8. Vector2D
        WriteExtendedProp(w, "vecProp", body => {
            body.TypeString("Shape2D#Vector2D");
            body.CompressedI32(-15);
            body.CompressedI32(30);
        });

        // 9. UOL
        WriteExtendedProp(w, "uolProp", body => {
            body.TypeString("UOL");
            body.U8(0);
            body.ImageString("../stand1/0");
        });

        // 10. Convex2D
        WriteExtendedProp(w, "convexProp", body => {
            body.TypeString("Shape2D#Convex2D");
            body.CompressedI32(2);
            body.TypeString("Shape2D#Vector2D");
            body.CompressedI32(1);
            body.CompressedI32(2);
            body.TypeString("Shape2D#Vector2D");
            body.CompressedI32(3);
            body.CompressedI32(4);
        });

        // 11. Sound_DX8
        WriteExtendedProp(w, "soundProp", body => {
            body.TypeString("Sound_DX8");
            body.U8(0);
            body.CompressedI32(64);
            body.CompressedI32(1500);
            body.WriteBytes(new byte[64]);
        });

        // 12. RawData
        WriteExtendedProp(w, "rawProp", body => {
            body.TypeString("RawData");
            body.WriteBytes([1, 2, 3, 4, 5]);
        });

        WzValue root = WzImage.Parse(w.ToArray());

        Assert.IsType<WzValue.Null>(root["nullProp"]);
        Assert.Equal(-320L, root["shortProp"]?.AsInt64());
        Assert.Equal(123456, root["intProp"]?.AsInt32());
        Assert.Equal(9876543210L, root["longProp"]?.AsInt64());
        Assert.Equal(1.5, root["floatProp"]?.AsDouble());
        Assert.Equal(42.0, root["doubleProp"]?.AsDouble());
        Assert.Equal("Hello\nWorld", root["strProp"]?.AsString());
        Assert.Equal(new WzValue.Vector(-15, 30), root["vecProp"]);
        Assert.Equal("../stand1/0", root["uolProp"]?.AsString());

        var convex = Assert.IsType<WzValue.Convex>(root["convexProp"]);
        Assert.Equal(2, convex.Items.Count);
        Assert.Equal(new WzValue.Vector(1, 2), convex.Items[0]);
        Assert.Equal(new WzValue.Vector(3, 4), convex.Items[1]);

        Assert.Equal(new WzValue.Sound(64, 1500), root["soundProp"]);
        Assert.Equal(new WzValue.RawData(5), root["rawProp"]);

        string json = root.ToJson();
        Assert.Contains("\"nullProp\": null", json);
        Assert.Contains("\"shortProp\": -320", json);
        Assert.Contains("\"intProp\": 123456", json);
        Assert.Contains("\"floatProp\": 1.5", json);
        Assert.Contains("\"doubleProp\": 42.0", json);
        Assert.Contains("\"strProp\": \"Hello\\nWorld\"", json);
        Assert.Contains("\"vecProp\": { \"x\": -15, \"y\": 30 }", json);
        Assert.Contains("\"soundProp\": { \"_sound\": true, \"bytes\": 64, \"duration\": 1500 }", json);
        Assert.Contains("\"rawProp\": { \"_rawdata\": true, \"bytes\": 5 }", json);

        // Verify full binary round-trip including Sound_DX8 and RawData payloads
        byte[] reserialized = root.ToBytes();
        WzValue reparsed = WzValue.Parse(reserialized);
        Assert.Equal(root, reparsed);
        Assert.Equal([1, 2, 3, 4, 5], Assert.IsType<WzValue.RawData>(reparsed["rawProp"]).Payload.ToArray());
    }

    private static void WriteExtendedProp(WzWriter w, string name, Action<WzWriter> writeBody) {
        w.ImageString(name);
        var body = new WzWriter();
        writeBody(body);
        w.U8(0x09);
        w.U32((uint) body.Length);
        w.WriteBytes(body.AsSpan());
    }
}
