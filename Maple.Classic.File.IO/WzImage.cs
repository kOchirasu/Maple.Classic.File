using System.Globalization;
using System.Text;

namespace Maple.Classic.File.IO;

/// <summary>
/// Image (<c>.img</c>) property deserialization and JSON rendering.
/// </summary>
public static class WzImage {
    /// <summary>
    /// Name tags representing inline strings inside an image.
    /// </summary>
    public static ReadOnlySpan<byte> NameInline => [0x00, 0x04, 0x73];

    /// <summary>
    /// Name tags representing offset-referenced strings inside an image.
    /// </summary>
    public static ReadOnlySpan<byte> NameDeref => [0x01, 0x1B];

    /// <summary>
    /// Parses a whole image. <paramref name="data"/> must be the image's bytes; offsets inside are relative to its start (0).
    /// </summary>
    public static WzValue Parse(byte[] data) {
        ArgumentNullException.ThrowIfNull(data);
        var r = new WzReader(data, 0, 0);

        // Images begin with a name-encoded type string, normally "Property".
        byte tag = r.ReadByte();
        if (tag is 0x73 or 0x1B or 0x01 or 0x00) {
            r.Seek(0);
            string ty = r.ReadStringAt(0, NameInline, NameDeref);
            if (ty != "Property") {
                // Some images are a bare extended object; re-read as one.
                r.Seek(0);
                return ReadExtended(r, 0, data.Length);
            }
        } else {
            r.Seek(0);
            return ReadExtended(r, 0, data.Length);
        }

        // u16 reserved, then the property list.
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
        r.Seek(end); // Extended blocks are length-delimited; trust the length.
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
                r.ReadByte(); // unused
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
                r.ReadUInt32(); // unused
                // The declared length counts a leading byte that is not part of the payload,
                // so the pixels start one byte after this field and run `dataLen` bytes.
                int rawLen = r.ReadInt32();
                int dataLen = rawLen > 0 ? rawLen - 1 : 0;
                int dataOff = r.Position + 1;
                return new WzValue.Canvas(width, height, format, dataOff, dataLen, children);
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

    /// <summary>
    /// Renders a <see cref="WzValue"/> as JSON. Canvas pixel data and sound payloads are summarized, not embedded.
    /// </summary>
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
            case WzValue.Canvas(var width, var height, var format, _, var dataLen, var children): {
                var parts = new List<string>(5 + children.Count) {
                    $"{pad2}\"_canvas\": true",
                    $"{pad2}\"width\": {width}",
                    $"{pad2}\"height\": {height}",
                    $"{pad2}\"format\": {format}",
                    $"{pad2}\"bytes\": {dataLen}",
                };
                foreach (WzProperty c in children) {
                    parts.Add($"{pad2}{JsonString(c.Name)}: {ToJson(c.Value, indent + 1)}");
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
