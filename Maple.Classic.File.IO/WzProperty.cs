namespace Maple.Classic.File.IO;

/// <summary>
/// A named property inside a WZ image or extended property list.
/// </summary>
public sealed record WzProperty(string Name, WzValue Value);

/// <summary>
/// Represents a deserialized WZ image property value.
/// </summary>
public abstract record WzValue {
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
    public sealed record Object(IReadOnlyList<WzProperty> Properties) : WzValue {
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
    /// <c>Canvas</c> — image metadata and payload slice location (<see cref="DataOffset"/>, <see cref="DataLength"/>) within the image bytes.
    /// </summary>
    public sealed record Canvas(
        int Width,
        int Height,
        int Format,
        int DataOffset,
        int DataLength,
        IReadOnlyList<WzProperty> ChildrenProperties) : WzValue {
        public bool Equals(Canvas? other) {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            return Width == other.Width
                && Height == other.Height
                && Format == other.Format
                && DataOffset == other.DataOffset
                && DataLength == other.DataLength
                && ChildrenProperties.SequenceEqual(other.ChildrenProperties);
        }

        public override int GetHashCode() {
            var hc = new HashCode();
            hc.Add(Width);
            hc.Add(Height);
            hc.Add(Format);
            hc.Add(DataOffset);
            hc.Add(DataLength);
            foreach (WzProperty p in ChildrenProperties) {
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
    public sealed record Convex(IReadOnlyList<WzValue> Items) : WzValue {
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

    /// <summary>
    /// Returns the string content if this value is <see cref="String"/> or <see cref="Uol"/>, otherwise <c>null</c>.
    /// </summary>
    public string? AsString() => this switch {
        String(var s) => s,
        Uol(var s) => s,
        _ => null,
    };

    /// <summary>
    /// Alias for <see cref="AsString"/> matching Rust's <c>as_str</c>.
    /// </summary>
    public string? AsStr() => AsString();

    /// <summary>
    /// Converts numeric values (<see cref="Short"/>, <see cref="Int"/>, <see cref="Long"/>, <see cref="Float"/>, <see cref="Double"/>) to <see cref="long"/>.
    /// </summary>
    public long? AsInt64() => this switch {
        Short(var v) => v,
        Int(var v) => v,
        Long(var v) => v,
        Float(var v) => (long) v,
        Double(var v) => (long) v,
        _ => null,
    };

    /// <summary>
    /// Alias for <see cref="AsInt64"/> matching Rust's <c>as_i64</c>.
    /// </summary>
    public long? AsI64() => AsInt64();

    public int? AsInt32() => AsInt64() is { } v ? (int) v : null;

    public double? AsDouble() => this switch {
        Short(var v) => v,
        Int(var v) => v,
        Long(var v) => v,
        Float(var v) => v,
        Double(var v) => v,
        _ => null,
    };

    /// <summary>
    /// Child properties if this node is an <see cref="Object"/> or <see cref="Canvas"/>, otherwise <c>null</c>.
    /// </summary>
    public IReadOnlyList<WzProperty>? Children => this switch {
        Object(var c) => c,
        Canvas { ChildrenProperties: var c } => c,
        _ => null,
    };

    /// <summary>
    /// Walks to a descendant value by <c>/</c>-separated path.
    /// </summary>
    public WzValue? Get(string path) {
        WzValue cur = this;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            IReadOnlyList<WzProperty>? kids = cur.Children;
            if (kids is null) {
                return null;
            }
            WzValue? next = null;
            foreach (WzProperty prop in kids) {
                if (prop.Name == part) {
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

    public WzValue? this[string path] => Get(path);

    /// <summary>
    /// Renders this value as JSON. Canvas pixel data and sound payloads are summarized.
    /// </summary>
    public string ToJson(int indent = 0) => WzImage.ToJson(this, indent);
}
