using System.ComponentModel.DataAnnotations;
using Maple.Classic.File.IO.Archive;

namespace Maple.Classic.File.IO.Values;

/// <summary>
/// WZ Canvas pixel formats: the format code stored with each <see cref="WzValue.Canvas"/> (format + format2 as one
/// number). The <see cref="DisplayAttribute"/> names are human-readable labels for tools.
/// </summary>
public enum WzCanvasFormat {
    [Display(Name = "BGRA4444 (16-bit)")]
    Bgra4444 = 1,
    [Display(Name = "BGRA8888 (32-bit)")]
    Bgra8888 = 2,
    [Display(Name = "BGR565 (16-bit)")]
    Bgr565 = 513,
    [Display(Name = "BGR565 Block16 (4-bit scale)")]
    Bgr565Block16 = 517,
    [Display(Name = "DXT3 / BC2")]
    Dxt3 = 1026,
    [Display(Name = "DXT5 / BC3")]
    Dxt5 = 2050,
}

public abstract partial record WzValue {
    /// <summary>
    /// <c>Canvas</c> — image dimensions, format, zero-copy pixel payload slice, and child properties (<c>origin</c>, <c>z</c>, <c>_outlink</c>, etc.).
    /// </summary>
    public sealed record Canvas(
        int Width,
        int Height,
        WzCanvasFormat Format,
        ReadOnlyMemory<byte> Payload,
        List<WzProperty> Properties) : WzValue {
        public Canvas(int width, int height, WzCanvasFormat format, ReadOnlyMemory<byte> payload)
            : this(width, height, format, payload, []) { }

        internal int DataOffset { get; init; }
        internal int DataLength { get; init; } = Payload.Length;

        public bool Equals(Canvas? other) {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            return Width == other.Width
                && Height == other.Height
                && Format == other.Format
                && Payload.Span.SequenceEqual(other.Payload.Span)
                && Properties.SequenceEqual(other.Properties);
        }

        public override int GetHashCode() {
            var hc = new HashCode();
            hc.Add(Width);
            hc.Add(Height);
            hc.Add(Format);
            hc.AddBytes(Payload.Span);
            foreach (WzProperty p in Properties) {
                hc.Add(p);
            }
            return hc.ToHashCode();
        }
    }

    public bool ReplaceCanvasPixels(ReadOnlySpan<char> path, int width, int height, WzCanvasFormat format, ReadOnlyMemory<byte> payload) {
        if (Get(path) is Canvas canvas) {
            Set(path, canvas with {
                Width = width,
                Height = height,
                Format = format,
                Payload = payload,
            });
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
            ReadOnlySpan<char> imgPart = link.AsSpan(0, idx + 4);
            ReadOnlySpan<char> innerPath = link.AsSpan(idx + 5);
            int lastSlash = imgPart.LastIndexOf('/');
            string imgName = (lastSlash >= 0 ? imgPart[(lastSlash + 1)..] : imgPart).ToString();

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

        List<WzProperty>? kids = MutableChildren;
        if (kids is not null) {
            for (int i = 0; i < kids.Count; i++) {
                WzValue child = kids[i].Value;
                if (child is Canvas canvasNode) {
                    string? link = canvasNode.GetDirect("_outlink")?.AsString();
                    if (link is not null) {
                        WzValue? target = resolve(link);
                        if (target is null) {
                            throw WzException.BadEntryType(0xEC, 0);
                        }
                        if (target is not Canvas targetCanvas) {
                            throw WzException.BadEntryType(0xEC, 1);
                        }

                        canvasNode.Properties.RemoveAll(static p => p.Name == "_outlink");
                        child = canvasNode with {
                            Width = targetCanvas.Width,
                            Height = targetCanvas.Height,
                            Format = targetCanvas.Format,
                            Payload = targetCanvas.Payload,
                        };
                        kids[i] = kids[i] with { Value = child };
                        filled++;
                    }
                }

                filled += child.InlineOutlinks(resolve);
            }
        }

        return filled;
    }

    public List<(string Path, int Width, int Height, WzCanvasFormat Format, int PayloadLength)> Canvases() {
        var output = new List<(string Path, int Width, int Height, WzCanvasFormat Format, int PayloadLength)>();
        Walk(this, "", output);
        return output;

        static void Walk(
            WzValue v,
            string path,
            List<(string Path, int Width, int Height, WzCanvasFormat Format, int PayloadLength)> acc) {
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
}
