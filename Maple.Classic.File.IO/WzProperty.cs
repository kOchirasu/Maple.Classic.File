using System.Collections;

namespace Maple.Classic.File.IO;

/// <summary>
/// A named property inside a WZ image or extended property list.
/// </summary>
public sealed record WzProperty(string Name, WzValue Value);

/// <summary>
/// Represents a WZ image property value. Supports direct path navigation, implicit primitive conversions, in-place mutation, and binary/JSON serialization.
/// </summary>
public abstract record WzValue : IEnumerable<WzProperty> {
    private WzValue() { }

    public sealed record Null() : WzValue {
        public static readonly Null Instance = new();
    }

    public sealed record Short(short Value) : WzValue;

    public sealed record Int(int Value) : WzValue;

    public sealed record Long(long Value) : WzValue;

    public sealed record Float(float Value) : WzValue;

    public sealed record Double(double Value) : WzValue;

    public sealed record String(string Value) : WzValue;

    /// <summary>
    /// <c>Shape2D#Vector2D</c>
    /// </summary>
    public sealed record Vector(int X, int Y) : WzValue;

    /// <summary>
    /// <c>UOL</c> — a symbolic link to another node.
    /// </summary>
    public sealed record Uol(string Value) : WzValue;

    /// <summary>
    /// A nested property list (<c>Property</c>).
    /// </summary>
    public sealed record Object(List<WzProperty> Properties) : WzValue {
        public Object() : this(new List<WzProperty>()) { }

        public void Add(string name, WzValue value) => Set(name, value);

        public bool Equals(Object? other) {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            return Properties.SequenceEqual(other.Properties);
        }

        public override int GetHashCode() {
            var hc = new HashCode();
            foreach (WzProperty p in Properties) {
                hc.Add(p);
            }
            return hc.ToHashCode();
        }
    }

    /// <summary>
    /// <c>Canvas</c> — image dimensions, format, compressed pixel payload, and child properties (<c>origin</c>, <c>z</c>, <c>_outlink</c>, etc.).
    /// </summary>
    public sealed record Canvas(
        int width,
        int height,
        int format,
        byte[] payload,
        List<WzProperty>? properties = null) : WzValue {
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public int Format { get; set; } = format;
        public byte[] Payload { get; set => field = value ?? []; } = payload ?? [];
        public List<WzProperty> Properties { get; set => field = value ?? []; } = properties ?? [];

        internal int DataOffset { get; init; }
        internal int DataLength { get; init; } = payload?.Length ?? 0;

        public bool Equals(Canvas? other) {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            return Width == other.Width
                && Height == other.Height
                && Format == other.Format
                && Payload.AsSpan().SequenceEqual(other.Payload)
                && Properties.SequenceEqual(other.Properties);
        }

        public override int GetHashCode() {
            var hc = new HashCode();
            hc.Add(Width);
            hc.Add(Height);
            hc.Add(Format);
            hc.AddBytes(Payload);
            foreach (WzProperty p in Properties) {
                hc.Add(p);
            }
            return hc.ToHashCode();
        }
    }

    /// <summary>
    /// <c>Sound_DX8</c>
    /// </summary>
    public sealed record Sound(int DataLength, int Duration) : WzValue;

    /// <summary>
    /// <c>Shape2D#Convex2D</c>
    /// </summary>
    public sealed record Convex(List<WzValue> Items) : WzValue {
        public Convex() : this(new List<WzValue>()) { }

        public bool Equals(Convex? other) {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            return Items.SequenceEqual(other.Items);
        }

        public override int GetHashCode() {
            var hc = new HashCode();
            foreach (WzValue item in Items) {
                hc.Add(item);
            }
            return hc.ToHashCode();
        }
    }

    /// <summary>
    /// <c>RawData</c> — opaque length-delimited blob (embedded fonts, colour LUTs).
    /// </summary>
    public sealed record RawData(int DataLength) : WzValue;

    // -------------------------------------------------------------------------
    // Implicit & Explicit Conversions (Minimal Boilerplate)
    // -------------------------------------------------------------------------

    public static implicit operator WzValue(short value) => new Short(value);
    public static implicit operator WzValue(int value) => new Int(value);
    public static implicit operator WzValue(long value) => new Long(value);
    public static implicit operator WzValue(float value) => new Float(value);
    public static implicit operator WzValue(double value) => new Double(value);
    public static implicit operator WzValue(string value) => new String(value);
    public static implicit operator WzValue((int X, int Y) value) => new Vector(value.X, value.Y);

    public static explicit operator short(WzValue value) =>
        value.AsShort() ?? throw new InvalidCastException($"Cannot convert {value.GetType().Name} to Int16.");

    public static explicit operator int(WzValue value) =>
        value.AsInt() ?? throw new InvalidCastException($"Cannot convert {value.GetType().Name} to Int32.");

    public static explicit operator long(WzValue value) =>
        value.AsLong() ?? throw new InvalidCastException($"Cannot convert {value.GetType().Name} to Int64.");

    public static explicit operator float(WzValue value) =>
        value.AsFloat() ?? throw new InvalidCastException($"Cannot convert {value.GetType().Name} to Single.");

    public static explicit operator double(WzValue value) =>
        value.AsDouble() ?? throw new InvalidCastException($"Cannot convert {value.GetType().Name} to Double.");

    public static explicit operator string?(WzValue? value) => value?.AsString();

    public static explicit operator (int X, int Y)(WzValue value) =>
        value.AsVector() ?? throw new InvalidCastException($"Cannot convert {value.GetType().Name} to Vector.");

    // -------------------------------------------------------------------------
    // Typed Value Accessors
    // -------------------------------------------------------------------------

    public string? AsString() => this switch {
        String(var s) => s,
        Uol(var s) => s,
        _ => null,
    };

    public string AsString(string defaultValue) => AsString() ?? defaultValue;

    public long? AsLong() => this switch {
        Short(var v) => v,
        Int(var v) => v,
        Long(var v) => v,
        Float(var v) => (long) v,
        Double(var v) => (long) v,
        _ => null,
    };

    public long AsLong(long defaultValue) => AsLong() ?? defaultValue;

    public long? AsInt64() => AsLong();

    public int? AsInt() => AsLong() is { } v ? (int) v : null;

    public int AsInt(int defaultValue) => AsInt() ?? defaultValue;

    public int? AsInt32() => AsInt();

    public short? AsShort() => AsLong() is { } v ? (short) v : null;

    public short AsShort(short defaultValue) => AsShort() ?? defaultValue;

    public float? AsFloat() => AsDouble() is { } v ? (float) v : null;

    public float AsFloat(float defaultValue) => AsFloat() ?? defaultValue;

    public double? AsDouble() => this switch {
        Short(var v) => v,
        Int(var v) => v,
        Long(var v) => v,
        Float(var v) => v,
        Double(var v) => v,
        _ => null,
    };

    public double AsDouble(double defaultValue) => AsDouble() ?? defaultValue;

    public (int X, int Y)? AsVector() => this is Vector(var x, var y) ? (x, y) : null;

    // -------------------------------------------------------------------------
    // Hierarchy Navigation & In-Place Mutation
    // -------------------------------------------------------------------------

    public IReadOnlyList<WzProperty>? Children => MutableChildren;

    internal List<WzProperty>? MutableChildren => this switch {
        Object(var c) => c,
        Canvas { Properties: var c } => c,
        _ => null,
    };

    public WzValue? this[string path] {
        get => Get(path);
        set {
            if (value is null) {
                Remove(path);
            } else {
                Set(path, value);
            }
        }
    }

    /// <summary>
    /// Walks to a descendant value by <c>/</c>-separated path with zero intermediate string allocations, or returns <c>null</c>.
    /// </summary>
    public WzValue? Get(ReadOnlySpan<char> path) {
        WzValue cur = this;
        foreach (Range range in path.Split('/')) {
            ReadOnlySpan<char> part = path[range];
            if (part.IsEmpty) {
                continue;
            }

            List<WzProperty>? kids = cur.MutableChildren;
            if (kids is null) {
                return null;
            }

            WzValue? next = null;
            foreach (WzProperty prop in kids) {
                if (prop.Name.AsSpan().SequenceEqual(part)) {
                    next = prop.Value;
                    break;
                }
            }
            if (next is null) {
                return null;
            }
            cur = next;
        }
        return cur;
    }

    /// <summary>
    /// Sets a property at a <c>/</c>-separated path, creating intermediate <see cref="Object"/> nodes as needed.
    /// </summary>
    public void Set(string path, WzValue value) {
        ArgumentNullException.ThrowIfNull(value);
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) {
            return;
        }

        string leaf = parts[^1];
        WzValue cur = this;
        for (int i = 0; i < parts.Length - 1; i++) {
            string part = parts[i];
            if (cur.GetDirect(part) is null) {
                cur.SetDirect(part, new Object());
            }
            cur = cur.GetDirect(part)!;
        }
        cur.SetDirect(leaf, value);
    }

    /// <summary>
    /// Removes the property at a <c>/</c>-separated path. Returns <c>true</c> if a property was removed.
    /// </summary>
    public bool Remove(string path) {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) {
            return false;
        }

        string leaf = parts[^1];
        WzValue cur = this;
        for (int i = 0; i < parts.Length - 1; i++) {
            WzValue? next = cur.GetDirect(parts[i]);
            if (next is null) {
                return false;
            }
            cur = next;
        }

        List<WzProperty>? kids = cur.MutableChildren;
        if (kids is null) {
            return false;
        }

        int before = kids.Count;
        kids.RemoveAll(p => p.Name == leaf);
        return kids.Count != before;
    }

    private WzValue? GetDirect(ReadOnlySpan<char> name) {
        List<WzProperty>? kids = MutableChildren;
        if (kids is null) {
            return null;
        }
        foreach (WzProperty prop in kids) {
            if (prop.Name.AsSpan().SequenceEqual(name)) {
                return prop.Value;
            }
        }
        return null;
    }

    private void SetDirect(string name, WzValue value) {
        List<WzProperty>? kids = MutableChildren;
        if (kids is null) {
            return;
        }
        for (int i = 0; i < kids.Count; i++) {
            if (kids[i].Name == name) {
                kids[i] = new WzProperty(name, value);
                return;
            }
        }
        kids.Add(new WzProperty(name, value));
    }

    public bool ReplaceCanvasPixels(string path, int width, int height, int format, byte[] payload) {
        if (Get(path) is Canvas canvas) {
            canvas.Width = width;
            canvas.Height = height;
            canvas.Format = format;
            canvas.Payload = payload;
            return true;
        }
        return false;
    }

    public int InlineOutlinks(WzArchive canvasArchive) {
        ArgumentNullException.ThrowIfNull(canvasArchive);
        var cache = new Dictionary<string, WzValue>(StringComparer.Ordinal);

        return InlineOutlinks(link => {
            int idx = link.IndexOf(".img/", StringComparison.Ordinal);
            if (idx < 0) {
                return null;
            }
            string imgPart = link[..(idx + 4)];
            string innerPath = link[(idx + 5)..];
            int lastSlash = imgPart.LastIndexOf('/');
            string imgName = lastSlash >= 0 ? imgPart[(lastSlash + 1)..] : imgPart;

            if (!cache.TryGetValue(imgName, out WzValue? parsedCanvasImg)) {
                WzNode? node = canvasArchive[imgName];
                if (node is null) {
                    return null;
                }
                parsedCanvasImg = canvasArchive.ParseImage(node);
                cache[imgName] = parsedCanvasImg;
            }

            return parsedCanvasImg[innerPath];
        });
    }

    public int InlineOutlinks(Func<string, WzValue?> resolve) {
        ArgumentNullException.ThrowIfNull(resolve);
        int filled = 0;

        if (this is Canvas canvasNode) {
            string? link = canvasNode.GetDirect("_outlink")?.AsString();
            if (link is not null) {
                WzValue? target = resolve(link);
                if (target is null) {
                    throw WzException.BadEntryType(0xEC, 0);
                }
                if (target is not Canvas targetCanvas) {
                    throw WzException.BadEntryType(0xEC, 1);
                }

                canvasNode.Width = targetCanvas.Width;
                canvasNode.Height = targetCanvas.Height;
                canvasNode.Format = targetCanvas.Format;
                canvasNode.Payload = [.. targetCanvas.Payload];
                canvasNode.Properties.RemoveAll(p => p.Name == "_outlink");
                filled++;
            }
        }

        List<WzProperty>? kids = MutableChildren;
        if (kids is not null) {
            for (int i = 0; i < kids.Count; i++) {
                filled += kids[i].Value.InlineOutlinks(resolve);
            }
        }

        return filled;
    }

    public List<(string Path, int Width, int Height, int Format, int PayloadLength)> Canvases() {
        var output = new List<(string Path, int Width, int Height, int Format, int PayloadLength)>();
        Walk(this, "", output);
        return output;

        static void Walk(
            WzValue v,
            string path,
            List<(string Path, int Width, int Height, int Format, int PayloadLength)> acc) {
            if (v is Canvas c) {
                acc.Add((path, c.Width, c.Height, c.Format, c.Payload.Length));
            }
            List<WzProperty>? kids = v.MutableChildren;
            if (kids is not null) {
                foreach (WzProperty prop in kids) {
                    Walk(prop.Value, $"{path}/{prop.Name}", acc);
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Parsing & Serialization
    // -------------------------------------------------------------------------

    public static WzValue Parse(byte[] imageBytes) => WzImage.Parse(imageBytes);

    public byte[] ToBytes() => WzImage.Serialize(this);

    public string ToJson(int indent = 0) => WzImage.ToJson(this, indent);

    public IEnumerator<WzProperty> GetEnumerator() =>
        (MutableChildren ?? Enumerable.Empty<WzProperty>()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
