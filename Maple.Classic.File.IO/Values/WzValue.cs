using System.Collections;
using Maple.Classic.File.IO.Serialization;

namespace Maple.Classic.File.IO.Values;

/// <summary>
/// Represents a WZ image property value. Supports direct path navigation, implicit primitive conversions, in-place mutation, and binary/JSON serialization.
/// </summary>
public abstract partial record WzValue : IEnumerable<WzProperty> {
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

    public WzValue? this[ReadOnlySpan<char> path] {
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
    /// Sets a property at a <c>/</c>-separated path, creating intermediate <see cref="Object"/> nodes as needed without array allocations.
    /// </summary>
    public void Set(ReadOnlySpan<char> path, WzValue value) {
        ArgumentNullException.ThrowIfNull(value);

        path = path.Trim('/');
        if (path.IsEmpty) {
            return;
        }

        WzValue cur = this;
        while (true) {
            int slash = path.IndexOf('/');
            if (slash < 0) {
                cur.SetDirect(path, value);
                return;
            }

            ReadOnlySpan<char> part = path[..slash];
            path = path[(slash + 1)..].TrimStart('/');
            if (part.IsEmpty) {
                continue;
            }

            WzValue? next = cur.GetDirect(part);
            if (next is null) {
                next = new Object();
                cur.SetDirect(part, next);
            }
            cur = next;
        }
    }

    /// <summary>
    /// Removes the property at a <c>/</c>-separated path without intermediate array allocations. Returns <c>true</c> if a property was removed.
    /// </summary>
    public bool Remove(ReadOnlySpan<char> path) {
        path = path.Trim('/');
        if (path.IsEmpty) {
            return false;
        }

        WzValue cur = this;
        while (true) {
            int slash = path.IndexOf('/');
            if (slash < 0) {
                break;
            }

            ReadOnlySpan<char> part = path[..slash];
            path = path[(slash + 1)..].TrimStart('/');
            if (part.IsEmpty) {
                continue;
            }

            WzValue? next = cur.GetDirect(part);
            if (next is null) {
                return false;
            }
            cur = next;
        }

        List<WzProperty>? kids = cur.MutableChildren;
        if (kids is null) {
            return false;
        }

        for (int i = kids.Count - 1; i >= 0; i--) {
            if (kids[i].Name.AsSpan().SequenceEqual(path)) {
                kids.RemoveAt(i);
                return true;
            }
        }
        return false;
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

    private void SetDirect(ReadOnlySpan<char> name, WzValue value) {
        List<WzProperty>? kids = MutableChildren;
        if (kids is null) {
            return;
        }
        for (int i = 0; i < kids.Count; i++) {
            if (kids[i].Name.AsSpan().SequenceEqual(name)) {
                kids[i] = kids[i] with { Value = value };
                return;
            }
        }
        kids.Add(new WzProperty(name.ToString(), value));
    }

    // -------------------------------------------------------------------------
    // Parsing & Serialization
    // -------------------------------------------------------------------------

    public static WzValue Parse(ReadOnlyMemory<byte> imageBytes) => WzImage.Parse(imageBytes);

    public static WzValue Parse(byte[] imageBytes) => WzImage.Parse(imageBytes);

    public byte[] ToBytes() => WzImage.Serialize(this);

    public string ToJson(int indent = 0) => WzImage.ToJson(this, indent);

    public IEnumerator<WzProperty> GetEnumerator() =>
        (MutableChildren ?? Enumerable.Empty<WzProperty>()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
