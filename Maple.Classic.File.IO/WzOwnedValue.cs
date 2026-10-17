namespace Maple.Classic.File.IO;

/// <summary>
/// An editable WZ image property tree that owns its canvas pixel payloads, allowing images to be built, mutated, merged, and serialized.
/// </summary>
public abstract record WzOwnedValue {
    private WzOwnedValue() { }

    public sealed record Null() : WzOwnedValue {
        public static readonly Null Instance = new();
    }

    public sealed record Short(short Value) : WzOwnedValue;

    public sealed record Int(int Value) : WzOwnedValue;

    public sealed record Long(long Value) : WzOwnedValue;

    public sealed record Float(float Value) : WzOwnedValue;

    public sealed record Double(double Value) : WzOwnedValue;

    public sealed record String(string Value) : WzOwnedValue;

    public sealed record Vector(int X, int Y) : WzOwnedValue;

    public sealed record Uol(string Value) : WzOwnedValue;

    /// <summary>
    /// Children in on-disk order.
    /// </summary>
    public sealed record Object(List<(string Name, WzOwnedValue Value)> Entries) : WzOwnedValue {
        public Object() : this([]) { }

        public bool Equals(Object? other) {
            if (ReferenceEquals(this, other)) return true;
            if (other is null || Entries.Count != other.Entries.Count) return false;
            for (int i = 0; i < Entries.Count; i++) {
                if (Entries[i].Name != other.Entries[i].Name || !Equals(Entries[i].Value, other.Entries[i].Value)) {
                    return false;
                }
            }
            return true;
        }

        public override int GetHashCode() {
            var hc = new HashCode();
            foreach ((string name, WzOwnedValue val) in Entries) {
                hc.Add(name);
                hc.Add(val);
            }
            return hc.ToHashCode();
        }
    }

    public sealed record Canvas : WzOwnedValue {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Format { get; set; }
        public byte[] Payload { get; set; }
        public List<(string Name, WzOwnedValue Value)> Entries { get; set; }

        public Canvas(
            int width,
            int height,
            int format,
            byte[] payload,
            List<(string Name, WzOwnedValue Value)>? entries = null) {
            Width = width;
            Height = height;
            Format = format;
            Payload = payload ?? [];
            Entries = entries ?? [];
        }

        public bool Equals(Canvas? other) {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            if (Width != other.Width
                || Height != other.Height
                || Format != other.Format
                || !Payload.AsSpan().SequenceEqual(other.Payload)
                || Entries.Count != other.Entries.Count) {
                return false;
            }
            for (int i = 0; i < Entries.Count; i++) {
                if (Entries[i].Name != other.Entries[i].Name || !Equals(Entries[i].Value, other.Entries[i].Value)) {
                    return false;
                }
            }
            return true;
        }

        public override int GetHashCode() {
            var hc = new HashCode();
            hc.Add(Width);
            hc.Add(Height);
            hc.Add(Format);
            hc.AddBytes(Payload);
            foreach ((string name, WzOwnedValue val) in Entries) {
                hc.Add(name);
                hc.Add(val);
            }
            return hc.ToHashCode();
        }
    }

    public sealed record Convex(List<WzOwnedValue> Items) : WzOwnedValue {
        public bool Equals(Convex? other) {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            return Items.SequenceEqual(other.Items);
        }

        public override int GetHashCode() {
            var hc = new HashCode();
            foreach (WzOwnedValue item in Items) {
                hc.Add(item);
            }
            return hc.ToHashCode();
        }
    }

    /// <summary>
    /// Converts a parsed <see cref="WzValue"/> into an owned tree, pulling canvas payloads out of <paramref name="imageBytes"/>.
    /// <see cref="WzValue.Sound"/> and <see cref="WzValue.RawData"/> are refused because <see cref="WzValue"/> does not record their payload offsets.
    /// </summary>
    public static WzOwnedValue FromValue(WzValue v, ReadOnlySpan<byte> imageBytes) {
        ArgumentNullException.ThrowIfNull(v);
        switch (v) {
            case WzValue.Null:
                return Null.Instance;
            case WzValue.Short(var x):
                return new Short(x);
            case WzValue.Int(var x):
                return new Int(x);
            case WzValue.Long(var x):
                return new Long(x);
            case WzValue.Float(var x):
                return new Float(x);
            case WzValue.Double(var x):
                return new Double(x);
            case WzValue.String(var s):
                return new String(s);
            case WzValue.Vector(var x, var y):
                return new Vector(x, y);
            case WzValue.Uol(var s):
                return new Uol(s);
            case WzValue.Object(var props):
                return new Object(PropsFrom(props, imageBytes));
            case WzValue.Canvas(var width, var height, var format, var dataOff, var dataLen, var children): {
                long end = (long) dataOff + dataLen;
                if (dataOff < 0 || dataLen < 0 || end > imageBytes.Length) {
                    int have = Math.Max(0, imageBytes.Length - Math.Max(0, dataOff));
                    throw WzException.UnexpectedEof(dataOff, dataLen, have);
                }
                return new Canvas(
                    width,
                    height,
                    format,
                    imageBytes.Slice(dataOff, dataLen).ToArray(),
                    PropsFrom(children, imageBytes));
            }
            case WzValue.Convex(var items): {
                var ownedItems = new List<WzOwnedValue>(items.Count);
                foreach (WzValue item in items) {
                    ownedItems.Add(FromValue(item, imageBytes));
                }
                return new Convex(ownedItems);
            }
            case WzValue.Sound:
                throw WzException.BadExtendedType("Sound_DX8", 0);
            case WzValue.RawData:
                throw WzException.BadExtendedType("RawData", 0);
            default:
                throw new ArgumentOutOfRangeException(nameof(v));
        }
    }

    /// <summary>
    /// Parses and owns an image in one step.
    /// </summary>
    public static WzOwnedValue Parse(byte[] imageBytes) {
        WzValue parsed = WzImage.Parse(imageBytes);
        return FromValue(parsed, imageBytes);
    }

    private static List<(string Name, WzOwnedValue Value)> PropsFrom(
        IReadOnlyList<WzProperty> props,
        ReadOnlySpan<byte> imageBytes) {
        var result = new List<(string Name, WzOwnedValue Value)>(props.Count);
        foreach (WzProperty p in props) {
            result.Add((p.Name, FromValue(p.Value, imageBytes)));
        }
        return result;
    }

    /// <summary>
    /// Returns the mutable child entry list if this node is an <see cref="Object"/> or <see cref="Canvas"/>, otherwise <c>null</c>.
    /// </summary>
    public List<(string Name, WzOwnedValue Value)>? Children => this switch {
        Object(var c) => c,
        Canvas { Entries: var c } => c,
        _ => null,
    };

    /// <summary>
    /// Gets a direct child by name, or <c>null</c> if not found.
    /// </summary>
    public WzOwnedValue? Get(string name) {
        List<(string Name, WzOwnedValue Value)>? kids = Children;
        if (kids is null) {
            return null;
        }
        foreach ((string n, WzOwnedValue v) in kids) {
            if (n == name) {
                return v;
            }
        }
        return null;
    }

    /// <summary>
    /// Sets (replaces or appends) a direct child.
    /// </summary>
    public void Set(string name, WzOwnedValue value) {
        List<(string Name, WzOwnedValue Value)>? kids = Children;
        if (kids is null) {
            return;
        }
        for (int i = 0; i < kids.Count; i++) {
            if (kids[i].Name == name) {
                kids[i] = (name, value);
                return;
            }
        }
        kids.Add((name, value));
    }

    /// <summary>
    /// Sets a leaf by <c>/</c>-separated path, creating intermediate <see cref="Object"/> nodes as needed.
    /// </summary>
    public void SetPath(string path, WzOwnedValue value) {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) {
            return;
        }

        string leaf = parts[^1];
        WzOwnedValue cur = this;
        for (int i = 0; i < parts.Length - 1; i++) {
            string part = parts[i];
            if (cur.Get(part) is null) {
                cur.Set(part, new Object());
            }
            cur = cur.Get(part)!;
        }
        cur.Set(leaf, value);
    }

    /// <summary>
    /// Gets the node at a <c>/</c>-separated path, or <c>null</c>.
    /// </summary>
    public WzOwnedValue? GetPath(string path) {
        WzOwnedValue cur = this;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            WzOwnedValue? next = cur.Get(part);
            if (next is null) {
                return null;
            }
            cur = next;
        }
        return cur;
    }

    /// <summary>
    /// Removes the node at a <c>/</c>-separated path. Returns <c>true</c> when a node was removed.
    /// </summary>
    public bool RemovePath(string path) {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) {
            return false;
        }

        string leaf = parts[^1];
        WzOwnedValue cur = this;
        for (int i = 0; i < parts.Length - 1; i++) {
            WzOwnedValue? next = cur.Get(parts[i]);
            if (next is null) {
                return false;
            }
            cur = next;
        }

        List<(string Name, WzOwnedValue Value)>? kids = cur.Children;
        if (kids is null) {
            return false;
        }

        int before = kids.Count;
        kids.RemoveAll(entry => entry.Name == leaf);
        return kids.Count != before;
    }

    /// <summary>
    /// Replaces the pixels of the <see cref="Canvas"/> at <paramref name="path"/>, keeping its children (<c>origin</c>, <c>z</c>, <c>_outlink</c>, etc.).
    /// Returns <c>false</c> when there is no canvas at <paramref name="path"/>.
    /// </summary>
    public bool ReplaceCanvasPixels(string path, int width, int height, int format, byte[] payload) {
        WzOwnedValue? node = GetPath(path);
        if (node is Canvas canvas) {
            canvas.Width = width;
            canvas.Height = height;
            canvas.Format = format;
            canvas.Payload = payload;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Pulls every <c>_outlink</c>ed canvas into this tree, replacing each stub with the pixels the link names and dropping the <c>_outlink</c> leaf.
    /// Returns the number of stubs filled.
    /// </summary>
    public int InlineOutlinks(Func<string, WzOwnedValue?> resolve) {
        ArgumentNullException.ThrowIfNull(resolve);
        int filled = 0;

        string? link = null;
        if (this is Canvas canvasNode) {
            foreach ((string n, WzOwnedValue v) in canvasNode.Entries) {
                if (n == "_outlink" && v is String(var s)) {
                    link = s;
                    break;
                }
            }

            if (link is not null) {
                WzOwnedValue? target = resolve(link);
                if (target is null) {
                    throw WzException.BadEntryType(0xEC, 0);
                }
                if (target is not Canvas targetCanvas) {
                    throw WzException.BadEntryType(0xEC, 1);
                }

                canvasNode.Width = targetCanvas.Width;
                canvasNode.Height = targetCanvas.Height;
                canvasNode.Format = targetCanvas.Format;
                canvasNode.Payload = targetCanvas.Payload.ToArray();
                canvasNode.Entries.RemoveAll(e => e.Name == "_outlink");
                filled++;
            }
        }

        List<(string Name, WzOwnedValue Value)>? kids = Children;
        if (kids is not null) {
            for (int i = 0; i < kids.Count; i++) {
                filled += kids[i].Value.InlineOutlinks(resolve);
            }
        }

        return filled;
    }

    /// <summary>
    /// Serializes this tree as a complete WZ image (<c>Property</c> root for <see cref="Object"/>).
    /// </summary>
    public byte[] SerializeImage() {
        var w = new WzWriter();
        if (this is Object(var kids)) {
            w.TypeString("Property");
            w.U16(0);
            WriteList(w, kids);
        } else {
            WriteExtendedBody(w, this);
        }
        return w.ToArray();
    }

    /// <summary>
    /// Enumerates every canvas in the tree as <c>(Path, Width, Height, Format, PayloadLength)</c>.
    /// </summary>
    public List<(string Path, int Width, int Height, int Format, int PayloadLength)> Canvases() {
        var output = new List<(string Path, int Width, int Height, int Format, int PayloadLength)>();
        Walk(this, "", output);
        return output;

        static void Walk(
            WzOwnedValue v,
            string path,
            List<(string Path, int Width, int Height, int Format, int PayloadLength)> acc) {
            if (v is Canvas c) {
                acc.Add((path, c.Width, c.Height, c.Format, c.Payload.Length));
            }
            List<(string Name, WzOwnedValue Value)>? kids = v.Children;
            if (kids is not null) {
                foreach ((string n, WzOwnedValue child) in kids) {
                    Walk(child, $"{path}/{n}", acc);
                }
            }
        }
    }

    /// <summary>
    /// Creates a deep copy of this <see cref="WzOwnedValue"/> tree.
    /// </summary>
    public WzOwnedValue DeepClone() => this switch {
        Null => Null.Instance,
        Short(var x) => new Short(x),
        Int(var x) => new Int(x),
        Long(var x) => new Long(x),
        Float(var x) => new Float(x),
        Double(var x) => new Double(x),
        String(var s) => new String(s),
        Vector(var x, var y) => new Vector(x, y),
        Uol(var s) => new Uol(s),
        Object(var entries) => new Object(entries.Select(e => (e.Name, e.Value.DeepClone())).ToList()),
        Canvas c => new Canvas(
            c.Width,
            c.Height,
            c.Format,
            c.Payload.ToArray(),
            c.Entries.Select(e => (e.Name, e.Value.DeepClone())).ToList()),
        Convex(var items) => new Convex(items.Select(i => i.DeepClone()).ToList()),
        _ => throw new InvalidOperationException(),
    };

    internal static void WriteList(WzWriter w, IReadOnlyList<(string Name, WzOwnedValue Value)> kids) {
        w.CompressedI32(kids.Count);
        foreach ((string name, WzOwnedValue v) in kids) {
            w.ImageString(name);
            WriteValue(w, v);
        }
    }

    internal static void WriteValue(WzWriter w, WzOwnedValue v) {
        switch (v) {
            case Null:
                w.U8(0x00);
                break;
            case Short(var x):
                w.U8(0x02);
                w.I16(x);
                break;
            case Int(var x):
                w.U8(0x03);
                w.CompressedI32(x);
                break;
            case Long(var x):
                w.U8(0x14);
                w.CompressedI64(x);
                break;
            case Float(var x):
                w.U8(0x04);
                w.CompressedF32(x);
                break;
            case Double(var x):
                w.U8(0x05);
                w.F64(x);
                break;
            case String(var s):
                w.U8(0x08);
                w.ImageString(s);
                break;
            default: {
                // 0x09, u32 length of what follows, then the length-delimited body.
                var body = new WzWriter();
                WriteExtendedBody(body, v);
                w.U8(0x09);
                w.U32((uint) body.Length);
                w.WriteBytes(body.AsSpan());
                break;
            }
        }
    }

    internal static void WriteExtendedBody(WzWriter w, WzOwnedValue v) {
        switch (v) {
            case Object(var kids):
                w.TypeString("Property");
                w.U16(0);
                WriteList(w, kids);
                break;

            case Canvas c:
                w.TypeString("Canvas");
                w.U8(0);
                if (c.Entries.Count == 0) {
                    w.U8(0);
                } else {
                    w.U8(1);
                    w.U16(0);
                    WriteList(w, c.Entries);
                }
                w.CompressedI32(c.Width);
                w.CompressedI32(c.Height);
                // The reader computes `format = cint + u8`; the second byte is 0 for every canvas.
                w.CompressedI32(c.Format);
                w.U8(0);
                w.U32(0);
                // Declared length counts one leading byte that is not pixel data.
                w.I32(c.Payload.Length + 1);
                w.U8(0);
                w.WriteBytes(c.Payload);
                break;

            case Vector(var x, var y):
                w.TypeString("Shape2D#Vector2D");
                w.CompressedI32(x);
                w.CompressedI32(y);
                break;

            case Convex(var items):
                w.TypeString("Shape2D#Convex2D");
                w.CompressedI32(items.Count);
                foreach (WzOwnedValue item in items) {
                    WriteExtendedBody(w, item);
                }
                break;

            case Uol(var s):
                w.TypeString("UOL");
                w.U8(0);
                w.ImageString(s);
                break;

            default:
                WriteValue(w, v);
                break;
        }
    }
}
