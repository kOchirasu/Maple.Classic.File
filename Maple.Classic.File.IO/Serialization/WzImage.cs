using System.Globalization;
using System.Text;
using Maple.Classic.File.IO.Values;

namespace Maple.Classic.File.IO.Serialization;

/// <summary>
/// Internal image (<c>.img</c>) property deserialization, single-pass binary serialization, and single-pass JSON rendering.
/// </summary>
internal static class WzImage {
    public static ReadOnlySpan<byte> NameInline => [0x00, 0x04, 0x73];
    public static ReadOnlySpan<byte> NameDeref => [0x01, 0x1B];

    public static WzValue Parse(ReadOnlyMemory<byte> data) {
        var r = new WzReader(data.Span, 0, 0);

        byte tag = r.ReadByte();
        if (tag is 0x73 or 0x1B or 0x01 or 0x00) {
            r.Seek(0);
            string ty = r.ReadStringAt(0, NameInline, NameDeref);
            if (ty != "Property") {
                r.Seek(0);
                return ReadExtended(ref r, data, 0, data.Length);
            }
        } else {
            r.Seek(0);
            return ReadExtended(ref r, data, 0, data.Length);
        }

        r.ReadUInt16();
        return new WzValue.Object(ReadPropertyList(ref r, data, 0));
    }

    private static List<WzProperty> ReadPropertyList(ref WzReader r, ReadOnlyMemory<byte> data, int baseOffset) {
        int count = r.ReadCompressedInt32();
        if (count is < 0 or > 500_000) {
            throw WzException.BadPropertyType(0xFF, r.Position);
        }

        var output = new List<WzProperty>(Math.Min(count, 1024));
        for (int i = 0; i < count; i++) {
            string name = r.ReadStringAt(baseOffset, NameInline, NameDeref);
            WzValue value = ReadValue(ref r, data, baseOffset);
            output.Add(new WzProperty(name, value));
        }
        return output;
    }

    private static WzValue ReadValue(ref WzReader r, ReadOnlyMemory<byte> data, int baseOffset) {
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
            0x09 => ReadLengthDelimitedExtended(ref r, data, baseOffset),
            _ => throw WzException.BadPropertyType(kind, start),
        };
    }

    private static WzValue ReadLengthDelimitedExtended(ref WzReader r, ReadOnlyMemory<byte> data, int baseOffset) {
        int len = (int) r.ReadUInt32();
        int end = r.Position + len;
        WzValue v = ReadExtended(ref r, data, baseOffset, end);
        r.Seek(end);
        return v;
    }

    private static WzValue ReadExtended(ref WzReader r, ReadOnlyMemory<byte> data, int baseOffset, int end) {
        int start = r.Position;
        string ty = r.ReadStringAt(baseOffset, NameInline, NameDeref);
        switch (ty) {
            case "Property": {
                r.ReadUInt16();
                return new WzValue.Object(ReadPropertyList(ref r, data, baseOffset));
            }

            case "Canvas": {
                r.ReadByte();
                bool hasChildren = r.ReadByte() == 1;
                List<WzProperty> children;
                if (hasChildren) {
                    r.ReadUInt16();
                    children = ReadPropertyList(ref r, data, baseOffset);
                } else {
                    children = [];
                }
                int width = r.ReadCompressedInt32();
                int height = r.ReadCompressedInt32();
                var format = (WzCanvasFormat) (r.ReadCompressedInt32() + r.ReadByte());
                r.ReadUInt32();
                int rawLen = r.ReadInt32();
                int dataLen = rawLen > 0 ? rawLen - 1 : 0;
                int dataOff = r.Position + 1;

                // Zero-copy slice directly into the underlying image memory
                ReadOnlyMemory<byte> payload = ReadOnlyMemory<byte>.Empty;
                if (dataLen > 0 && (long) dataOff + dataLen <= data.Length) {
                    payload = data.Slice(dataOff, dataLen);
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
                    items.Add(ReadExtended(ref r, data, baseOffset, end));
                }
                return new WzValue.Convex(items);
            }

            case "Sound_DX8": {
                r.ReadByte();
                int dataLen = Math.Max(0, r.ReadCompressedInt32());
                int duration = r.ReadCompressedInt32();
                int safeEnd = Math.Min(end, data.Length);
                int rem = Math.Max(0, safeEnd - r.Position);
                ReadOnlyMemory<byte> header = ReadOnlyMemory<byte>.Empty;
                ReadOnlyMemory<byte> payload = ReadOnlyMemory<byte>.Empty;
                if (rem >= dataLen && dataLen > 0) {
                    int headerLen = rem - dataLen;
                    if (headerLen > 0) {
                        header = data.Slice(r.Position, headerLen);
                    }
                    payload = data.Slice(r.Position + headerLen, dataLen);
                } else if (rem > 0) {
                    payload = data.Slice(r.Position, rem);
                }
                r.Seek(Math.Min(end, r.Data.Length));
                return new WzValue.Sound(dataLen, duration, payload, header);
            }

            case "UOL": {
                r.ReadByte();
                return new WzValue.Uol(r.ReadStringAt(baseOffset, NameInline, NameDeref));
            }

            case "RawData": {
                int safeEnd = Math.Min(end, data.Length);
                int dataLen = Math.Max(0, safeEnd - r.Position);
                ReadOnlyMemory<byte> payload = dataLen > 0 ? data.Slice(r.Position, dataLen) : ReadOnlyMemory<byte>.Empty;
                r.Seek(Math.Min(end, r.Data.Length));
                return new WzValue.RawData(dataLen, payload);
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
                // Single-pass in-place length backpatching — no intermediate WzWriter or buffer copies
                w.U8(0x09);
                int lenFieldPos = w.ReserveU32();
                int bodyStart = w.Length;
                WriteExtendedBody(w, v);
                w.PatchU32(lenFieldPos, (uint) (w.Length - bodyStart));
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

            case WzValue.Canvas c: {
                ReadOnlySpan<byte> payloadSpan = c.Payload.Span;
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
                w.CompressedI32((int) c.Format);
                w.U8(0);
                w.U32(0);
                w.I32(payloadSpan.Length + 1);
                w.U8(0);
                w.WriteBytes(payloadSpan);
                break;
            }

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

            case WzValue.Sound s: {
                ReadOnlySpan<byte> headerSpan = s.Header.Span;
                ReadOnlySpan<byte> payloadSpan = s.Payload.Span;
                int actualDataLen = !payloadSpan.IsEmpty ? payloadSpan.Length : Math.Max(0, s.DataLength);
                w.TypeString("Sound_DX8");
                w.U8(0);
                w.CompressedI32(actualDataLen);
                w.CompressedI32(s.Duration);
                if (!headerSpan.IsEmpty) {
                    w.WriteBytes(headerSpan);
                }
                if (!payloadSpan.IsEmpty) {
                    w.WriteBytes(payloadSpan);
                } else if (actualDataLen > 0) {
                    w.WriteZeroBytes(actualDataLen);
                }
                break;
            }

            case WzValue.RawData raw: {
                ReadOnlySpan<byte> payloadSpan = raw.Payload.Span;
                w.TypeString("RawData");
                if (!payloadSpan.IsEmpty) {
                    w.WriteBytes(payloadSpan);
                } else if (raw.DataLength > 0) {
                    w.WriteZeroBytes(Math.Max(0, raw.DataLength));
                }
                break;
            }

            default:
                WriteValue(w, v);
                break;
        }
    }

    public static string ToJson(WzValue v, int indent = 0) {
        ArgumentNullException.ThrowIfNull(v);
        var sb = new StringBuilder(256);
        WriteJson(sb, v, indent);
        return sb.ToString();
    }

    private static void WriteJson(StringBuilder sb, WzValue v, int indent) {
        switch (v) {
            case WzValue.Null:
                sb.Append("null");
                break;
            case WzValue.Short(var x):
                sb.Append(x.ToString(CultureInfo.InvariantCulture));
                break;
            case WzValue.Int(var x):
                sb.Append(x.ToString(CultureInfo.InvariantCulture));
                break;
            case WzValue.Long(var x):
                sb.Append(x.ToString(CultureInfo.InvariantCulture));
                break;
            case WzValue.Float(var x):
                AppendNumber(sb, x);
                break;
            case WzValue.Double(var x):
                AppendNumber(sb, x);
                break;
            case WzValue.String(var s):
                AppendJsonString(sb, s);
                break;
            case WzValue.Uol(var s):
                AppendJsonString(sb, s);
                break;
            case WzValue.Vector(var x, var y):
                sb.Append("{ \"x\": ").Append(x).Append(", \"y\": ").Append(y).Append(" }");
                break;
            case WzValue.Sound(var dataLen, var duration, _, _):
                sb.Append("{ \"_sound\": true, \"bytes\": ").Append(dataLen)
                  .Append(", \"duration\": ").Append(duration).Append(" }");
                break;
            case WzValue.RawData(var dataLen, _):
                sb.Append("{ \"_rawdata\": true, \"bytes\": ").Append(dataLen).Append(" }");
                break;
            case WzValue.Convex(var items): {
                sb.Append('[');
                for (int i = 0; i < items.Count; i++) {
                    if (i > 0) {
                        sb.Append(", ");
                    }
                    WriteJson(sb, items[i], indent + 1);
                }
                sb.Append(']');
                break;
            }
            case WzValue.Canvas c: {
                int pad = indent * 2;
                int pad2 = (indent + 1) * 2;
                int byteLen = c.Payload.Length > 0 ? c.Payload.Length : c.DataLength;

                sb.Append("{\n");
                sb.Append(' ', pad2).Append("\"_canvas\": true,\n");
                sb.Append(' ', pad2).Append("\"width\": ").Append(c.Width).Append(",\n");
                sb.Append(' ', pad2).Append("\"height\": ").Append(c.Height).Append(",\n");
                sb.Append(' ', pad2).Append("\"format\": ").Append((int) c.Format).Append(",\n");
                sb.Append(' ', pad2).Append("\"bytes\": ").Append(byteLen);

                foreach (WzProperty child in c.Properties) {
                    sb.Append(",\n").Append(' ', pad2);
                    AppendJsonString(sb, child.Name);
                    sb.Append(": ");
                    WriteJson(sb, child.Value, indent + 1);
                }

                sb.Append('\n').Append(' ', pad).Append('}');
                break;
            }
            case WzValue.Object(var props): {
                if (props.Count == 0) {
                    sb.Append("{}");
                    break;
                }
                int pad = indent * 2;
                int pad2 = (indent + 1) * 2;
                sb.Append("{\n");
                for (int i = 0; i < props.Count; i++) {
                    if (i > 0) {
                        sb.Append(",\n");
                    }
                    WzProperty p = props[i];
                    sb.Append(' ', pad2);
                    AppendJsonString(sb, p.Name);
                    sb.Append(": ");
                    WriteJson(sb, p.Value, indent + 1);
                }
                sb.Append('\n').Append(' ', pad).Append('}');
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(v));
        }
    }

    private static void AppendNumber(StringBuilder sb, double x) {
        if (double.IsFinite(x)) {
            string s = x.ToString("G", CultureInfo.InvariantCulture);
            sb.Append(s);
            if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E')) {
                sb.Append(".0");
            }
        } else {
            sb.Append("null");
        }
    }

    private static void AppendJsonString(StringBuilder sb, ReadOnlySpan<char> s) {
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
    }
}
