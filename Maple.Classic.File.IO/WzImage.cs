using System.Globalization;
using System.Text;

namespace Maple.Classic.File.IO;

/// <summary>
/// Internal image (<c>.img</c>) property deserialization, serialization, and JSON rendering logic.
/// </summary>
internal static class WzImage {
    public static ReadOnlySpan<byte> NameInline => [0x00, 0x04, 0x73];
    public static ReadOnlySpan<byte> NameDeref => [0x01, 0x1B];

    public static WzValue Parse(byte[] data) {
        ArgumentNullException.ThrowIfNull(data);
        var r = new WzReader(data, 0, 0);

        byte tag = r.ReadByte();
        if (tag is 0x73 or 0x1B or 0x01 or 0x00) {
            r.Seek(0);
            string ty = r.ReadStringAt(0, NameInline, NameDeref);
            if (ty != "Property") {
                r.Seek(0);
                return ReadExtended(r, 0, data.Length);
            }
        } else {
            r.Seek(0);
            return ReadExtended(r, 0, data.Length);
        }

        r.ReadUInt16();
        return new WzValue.Object(ReadPropertyList(r, 0));
    }

    private static List<WzProperty> ReadPropertyList(WzReader r, int baseOffset) {
        int count = r.ReadCompressedInt32();
        if (count is < 0 or > 500_000) {
            throw WzException.BadPropertyType(0xFF, r.Position);
        }

        var output = new List<WzProperty>(Math.Min(count, 1024));
        for (int i = 0; i < count; i++) {
            string name = r.ReadStringAt(baseOffset, NameInline, NameDeref);
            WzValue value = ReadValue(r, baseOffset);
            output.Add(new WzProperty(name, value));
        }
        return output;
    }

    private static WzValue ReadValue(WzReader r, int baseOffset) {
        int start = r.Position;
        byte kind = r.ReadByte();
        return kind switch {
            0x00 => WzValue.Null.Instance,
            0x02 or 0x0B => new WzValue.Short(r.ReadInt16()),
            0x03 or 0x13 => new WzValue.Int(r.ReadCompressedInt32()),
            0x14 => new WzValue.Long(r.ReadCompressedInt64()),
            0x04 => new WzValue.Float(r.ReadCompressedSingle()),
            0x05 => new WzValue.Double(r.ReadDouble()),
            0x08 => new WzValue.String(r.ReadStringAt(baseOffset, NameInline, NameDeref)),
            0x09 => ReadLengthDelimitedExtended(r, baseOffset),
            _ => throw WzException.BadPropertyType(kind, start),
        };
    }

    private static WzValue ReadLengthDelimitedExtended(WzReader r, int baseOffset) {
        int len = (int) r.ReadUInt32();
        int end = r.Position + len;
        WzValue v = ReadExtended(r, baseOffset, end);
        r.Seek(end);
        return v;
    }

    private static WzValue ReadExtended(WzReader r, int baseOffset, int end) {
        int start = r.Position;
        string ty = r.ReadStringAt(baseOffset, NameInline, NameDeref);
        switch (ty) {
            case "Property": {
                r.ReadUInt16();
                return new WzValue.Object(ReadPropertyList(r, baseOffset));
            }

            case "Canvas": {
                r.ReadByte();
                bool hasChildren = r.ReadByte() == 1;
                List<WzProperty> children;
                if (hasChildren) {
                    r.ReadUInt16();
                    children = ReadPropertyList(r, baseOffset);
                } else {
                    children = [];
                }
                int width = r.ReadCompressedInt32();
                int height = r.ReadCompressedInt32();
                int format = r.ReadCompressedInt32() + r.ReadByte();
                r.ReadUInt32();
                int rawLen = r.ReadInt32();
                int dataLen = rawLen > 0 ? rawLen - 1 : 0;
                int dataOff = r.Position + 1;

                byte[] payload = [];
                if (dataLen > 0 && (long) dataOff + dataLen <= r.Data.Length) {
                    payload = r.Data.AsSpan(dataOff, dataLen).ToArray();
                }

                return new WzValue.Canvas(width, height, format, payload, children) {
                    DataOffset = dataOff,
                    DataLength = dataLen,
                };
            }

            case "Shape2D#Vector2D": {
                int x = r.ReadCompressedInt32();
                int y = r.ReadCompressedInt32();
                return new WzValue.Vector(x, y);
            }

            case "Shape2D#Convex2D": {
                int n = Math.Max(0, r.ReadCompressedInt32());
                var items = new List<WzValue>(Math.Min(n, 1024));
                for (int i = 0; i < n; i++) {
                    items.Add(ReadExtended(r, baseOffset, end));
                }
                return new WzValue.Convex(items);
            }

            case "Sound_DX8": {
                r.ReadByte();
                int dataLen = Math.Max(0, r.ReadCompressedInt32());
                int duration = r.ReadCompressedInt32();
                return new WzValue.Sound(dataLen, duration);
            }

            case "UOL": {
                r.ReadByte();
                return new WzValue.Uol(r.ReadStringAt(baseOffset, NameInline, NameDeref));
            }

            case "RawData": {
                int dataLen = Math.Max(0, end - r.Position);
                r.Seek(Math.Min(end, r.Data.Length));
                return new WzValue.RawData(dataLen);
            }

            default:
                throw WzException.BadExtendedType(ty, start);
        }
    }

    public static byte[] Serialize(WzValue root) {
        ArgumentNullException.ThrowIfNull(root);
        var w = new WzWriter();
        if (root is WzValue.Object(var props)) {
            w.TypeString("Property");
            w.U16(0);
            WriteList(w, props);
        } else {
            WriteExtendedBody(w, root);
        }
        return w.ToArray();
    }

    private static void WriteList(WzWriter w, IReadOnlyList<WzProperty> props) {
        w.CompressedI32(props.Count);
        foreach (WzProperty prop in props) {
            w.ImageString(prop.Name);
            WriteValue(w, prop.Value);
        }
    }

    private static void WriteValue(WzWriter w, WzValue v) {
        switch (v) {
            case WzValue.Null:
                w.U8(0x00);
                break;
            case WzValue.Short(var x):
                w.U8(0x02);
                w.I16(x);
                break;
            case WzValue.Int(var x):
                w.U8(0x03);
                w.CompressedI32(x);
                break;
            case WzValue.Long(var x):
                w.U8(0x14);
                w.CompressedI64(x);
                break;
            case WzValue.Float(var x):
                w.U8(0x04);
                w.CompressedF32(x);
                break;
            case WzValue.Double(var x):
                w.U8(0x05);
                w.F64(x);
                break;
            case WzValue.String(var s):
                w.U8(0x08);
                w.ImageString(s);
                break;
            default: {
                var body = new WzWriter();
                WriteExtendedBody(body, v);
                w.U8(0x09);
                w.U32((uint) body.Length);
                w.WriteBytes(body.AsSpan());
                break;
            }
        }
    }

    private static void WriteExtendedBody(WzWriter w, WzValue v) {
        switch (v) {
            case WzValue.Object(var props):
                w.TypeString("Property");
                w.U16(0);
                WriteList(w, props);
                break;

            case WzValue.Canvas c:
                w.TypeString("Canvas");
                w.U8(0);
                if (c.Properties.Count == 0) {
                    w.U8(0);
                } else {
                    w.U8(1);
                    w.U16(0);
                    WriteList(w, c.Properties);
                }
                w.CompressedI32(c.Width);
                w.CompressedI32(c.Height);
                w.CompressedI32(c.Format);
                w.U8(0);
                w.U32(0);
                w.I32(c.Payload.Length + 1);
                w.U8(0);
                w.WriteBytes(c.Payload);
                break;

            case WzValue.Vector(var x, var y):
                w.TypeString("Shape2D#Vector2D");
                w.CompressedI32(x);
                w.CompressedI32(y);
                break;

            case WzValue.Convex(var items):
                w.TypeString("Shape2D#Convex2D");
                w.CompressedI32(items.Count);
                foreach (WzValue item in items) {
                    WriteExtendedBody(w, item);
                }
                break;

            case WzValue.Uol(var s):
                w.TypeString("UOL");
                w.U8(0);
                w.ImageString(s);
                break;

            case WzValue.Sound:
                throw WzException.BadExtendedType("Sound_DX8", 0);

            case WzValue.RawData:
                throw WzException.BadExtendedType("RawData", 0);

            default:
                WriteValue(w, v);
                break;
        }
    }

    public static string ToJson(WzValue v, int indent = 0) {
        ArgumentNullException.ThrowIfNull(v);
        string pad = new(' ', indent * 2);
        string pad2 = new(' ', (indent + 1) * 2);

        switch (v) {
            case WzValue.Null:
                return "null";
            case WzValue.Short(var x):
                return x.ToString(CultureInfo.InvariantCulture);
            case WzValue.Int(var x):
                return x.ToString(CultureInfo.InvariantCulture);
            case WzValue.Long(var x):
                return x.ToString(CultureInfo.InvariantCulture);
            case WzValue.Float(var x):
                return FormatNumber(x);
            case WzValue.Double(var x):
                return FormatNumber(x);
            case WzValue.String(var s):
                return JsonString(s);
            case WzValue.Uol(var s):
                return JsonString(s);
            case WzValue.Vector(var x, var y):
                return $"{{ \"x\": {x}, \"y\": {y} }}";
            case WzValue.Sound(var dataLen, var duration):
                return $"{{ \"_sound\": true, \"bytes\": {dataLen}, \"duration\": {duration} }}";
            case WzValue.RawData(var dataLen):
                return $"{{ \"_rawdata\": true, \"bytes\": {dataLen} }}";
            case WzValue.Convex(var items): {
                IEnumerable<string> inner = items.Select(i => ToJson(i, indent + 1));
                return $"[{string.Join(", ", inner)}]";
            }
            case WzValue.Canvas c: {
                int byteLen = c.Payload.Length > 0 ? c.Payload.Length : c.DataLength;
                var parts = new List<string>(5 + c.Properties.Count) {
                    $"{pad2}\"_canvas\": true",
                    $"{pad2}\"width\": {c.Width}",
                    $"{pad2}\"height\": {c.Height}",
                    $"{pad2}\"format\": {c.Format}",
                    $"{pad2}\"bytes\": {byteLen}",
                };
                foreach (WzProperty child in c.Properties) {
                    parts.Add($"{pad2}{JsonString(child.Name)}: {ToJson(child.Value, indent + 1)}");
                }
                return $"{{\n{string.Join(",\n", parts)}\n{pad}}}";
            }
            case WzValue.Object(var props): {
                if (props.Count == 0) {
                    return "{}";
                }
                var inner = new List<string>(props.Count);
                foreach (WzProperty p in props) {
                    inner.Add($"{pad2}{JsonString(p.Name)}: {ToJson(p.Value, indent + 1)}");
                }
                return $"{{\n{string.Join(",\n", inner)}\n{pad}}}";
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(v));
        }
    }

    private static string FormatNumber(double x) {
        if (double.IsFinite(x)) {
            string s = x.ToString("G", CultureInfo.InvariantCulture);
            if (s.Contains('.') || s.Contains('e') || s.Contains('E')) {
                return s;
            }
            return $"{s}.0";
        }
        return "null";
    }

    private static string JsonString(string s) {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s) {
            switch (c) {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20) {
                        sb.Append($"\\u{(uint) c:x4}");
                    } else {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
