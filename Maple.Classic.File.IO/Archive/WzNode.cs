using System.Collections;
using Maple.Classic.File.IO.Values;

namespace Maple.Classic.File.IO.Archive;

public enum WzNodeKind {
    Directory,
    Image,
}

public class WzNode : IEnumerable<WzNode> {
    public string Name { get; }
    public WzNodeKind Kind { get; }
    public int Size { get; }
    public IReadOnlyList<WzNode> Children => MutableChildren;

    public bool IsDir => Kind == WzNodeKind.Directory;
    public bool IsImage => Kind == WzNodeKind.Image;

    internal uint Offset { get; }
    internal int Checksum { get; }
    internal WzArchive? Archive { get; set; }
    internal List<WzNode> MutableChildren { get; set => field = value ?? []; }

    internal WzNode(
        string name,
        WzNodeKind kind,
        uint offset = 0,
        int size = 0,
        int checksum = 0,
        List<WzNode>? children = null,
        WzArchive? archive = null) {
        Name = name;
        Kind = kind;
        Offset = offset;
        Size = size;
        Checksum = checksum;
        MutableChildren = children ?? [];
        Archive = archive;
    }

    public WzNode? this[ReadOnlySpan<char> path] => Get(path);

    /// <summary>
    /// Walks to a descendant node by <c>/</c>-separated path with zero intermediate string allocations.
    /// </summary>
    public WzNode? Get(ReadOnlySpan<char> path) {
        WzNode cur = this;
        foreach (Range range in path.Split('/')) {
            ReadOnlySpan<char> part = path[range];
            if (part.IsEmpty) {
                continue;
            }

            WzNode? next = null;
            foreach (WzNode child in cur.MutableChildren) {
                if (child.Name.AsSpan().SequenceEqual(part)) {
                    next = child;
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
    /// Returns a zero-copy <see cref="ReadOnlyMemory{T}"/> slice over the raw bytes of this image node.
    /// </summary>
    public ReadOnlyMemory<byte> GetMemory() {
        if (Archive is null) {
            throw new InvalidOperationException($"Node \"{Name}\" is not attached to an archive.");
        }
        return Archive.GetImageMemory(this);
    }

    /// <summary>
    /// Returns a copy of the raw bytes of this image node from its owning <see cref="WzArchive"/>.
    /// </summary>
    public byte[] GetBytes() {
        if (Archive is null) {
            throw new InvalidOperationException($"Node \"{Name}\" is not attached to an archive.");
        }
        return Archive.ImageBytes(this);
    }

    /// <summary>
    /// Parses the property tree of this image node directly from its owning <see cref="WzArchive"/> without copying the image buffer.
    /// </summary>
    public WzValue ParseImage() {
        if (Archive is null) {
            throw new InvalidOperationException($"Node \"{Name}\" is not attached to an archive.");
        }
        return Archive.ParseImage(this);
    }

    /// <summary>
    /// Recursively counts all directories and images beneath this node.
    /// </summary>
    public (int Directories, int Images) CountRecursive() {
        int dirs = 0;
        int imgs = 0;
        foreach (WzNode c in MutableChildren) {
            if (c.Kind == WzNodeKind.Directory) {
                dirs++;
            } else {
                imgs++;
            }
            (int d, int i) = c.CountRecursive();
            dirs += d;
            imgs += i;
        }
        return (dirs, imgs);
    }

    public IEnumerator<WzNode> GetEnumerator() => MutableChildren.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
