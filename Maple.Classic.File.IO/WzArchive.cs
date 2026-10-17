using System.Buffers.Binary;
using System.Collections;
using System.Text;
using Maple.Classic.File.IO.Crypto;

namespace Maple.Classic.File.IO;

public record WzHeader(ulong FSize, uint FStart, string Copyright) {
    internal ushort EncVer { get; init; }
}

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

/// <summary>
/// Represents a WZ archive for reading, navigating, editing, and saving <c>.wz</c> files.
/// </summary>
public class WzArchive : IDisposable {
    public const ushort DefaultVersion = WzCrypto.ClientVersion;

    private readonly List<WzImageEntry> stagedAdditions = [];
    private readonly HashSet<string> stagedRemovals = new(StringComparer.Ordinal);

    public string Path { get; }
    public WzHeader Header { get; }
    public ushort Version { get; }
    public WzNode Root { get; }
    public IReadOnlyList<WzNode> Nodes => Root.Children;

    internal byte[] Data { get; }
    internal uint VersionHash { get; }

    /// <summary>
    /// Opens a WZ archive from disk, detecting the version automatically unless <paramref name="version"/> is specified.
    /// </summary>
    public WzArchive(string path, ushort? version = null) {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] data;
        try {
            data = System.IO.File.ReadAllBytes(path);
        } catch (Exception ex) {
            throw WzException.Io(path, ex);
        }

        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
        (Header, Version, VersionHash, Root) = Initialize(data, stem, version);
        Path = path;
        Data = data;
        AttachArchive(Root);
    }

    /// <summary>
    /// Creates an empty in-memory WZ archive ready for adding images via <see cref="SetImage(string, WzValue)"/> and saving.
    /// </summary>
    public WzArchive(ushort version = DefaultVersion)
        : this(WzWriter.WriteArchive(version, []), "Archive", version, "") {
    }

    private WzArchive(byte[] data, string name, ushort? version, string path) {
        (Header, Version, VersionHash, Root) = Initialize(data, name, version);
        Path = path;
        Data = data;
        AttachArchive(Root);
    }

    public static WzArchive Open(string path, ushort? version = null) => new(path, version);

    public static WzArchive OpenTree(string dataDir, string tree, ushort? version = null) =>
        new(TreeArchivePath(dataDir, tree), version);

    public static WzArchive FromBytes(byte[] data, string name = "", ushort? version = null) {
        ArgumentNullException.ThrowIfNull(data);
        return new WzArchive(data, name, version, "");
    }

    public static string TreeArchivePath(string dataDir, string tree) =>
        System.IO.Path.Combine(dataDir, tree, $"{tree}_000.wz");

    public WzNode? this[ReadOnlySpan<char> path] => Root.Get(path);

    /// <summary>
    /// Resolves an image node by <c>/</c>-separated path and parses its property tree without copying the image slice.
    /// If <paramref name="path"/> points inside an image (e.g. <c>"Eqp.img/Eqp/Coat"</c>), returns the property at that subpath.
    /// </summary>
    public WzValue ParseImage(string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        WzNode? directNode = Root.Get(path);
        if (directNode is not null) {
            return ParseImage(directNode);
        }

        int imgIdx = path.IndexOf(".img/", StringComparison.Ordinal);
        if (imgIdx >= 0) {
            ReadOnlySpan<char> imgPath = path.AsSpan(0, imgIdx + 4);
            ReadOnlySpan<char> subPath = path.AsSpan(imgIdx + 5);
            WzNode node = Root.Get(imgPath)
                ?? throw new KeyNotFoundException($"No image node found at path \"{imgPath}\".");
            WzValue rootVal = ParseImage(node);
            return rootVal.Get(subPath)
                ?? throw new KeyNotFoundException($"No property found at path \"{subPath}\" inside \"{imgPath}\".");
        }

        throw new KeyNotFoundException($"No node found at path \"{path}\".");
    }

    /// <summary>
    /// Parses the property tree of an image node directly over the archive's memory slice with zero buffer copies.
    /// </summary>
    public WzValue ParseImage(WzNode node) => WzImage.Parse(GetImageMemory(node));

    /// <summary>
    /// Returns a zero-copy <see cref="ReadOnlyMemory{T}"/> slice over the raw bytes of an image node.
    /// </summary>
    public ReadOnlyMemory<byte> GetImageMemory(WzNode node) {
        ArgumentNullException.ThrowIfNull(node);
        long start = node.Offset;
        long size = Math.Max(0, node.Size);
        long end = start + size;
        if (node.Kind != WzNodeKind.Image || start < 0 || end > Data.Length) {
            throw WzException.OutOfBounds(node.Name, node.Offset, node.Size, Data.Length);
        }
        return Data.AsMemory((int) start, (int) size);
    }

    /// <summary>
    /// Returns a zero-copy <see cref="ReadOnlySpan{T}"/> over the raw bytes of an image node.
    /// </summary>
    public ReadOnlySpan<byte> GetImageSpan(WzNode node) => GetImageMemory(node).Span;

    /// <summary>
    /// Returns a copy of the raw bytes of an image node.
    /// </summary>
    public byte[] ImageBytes(WzNode node) => [.. GetImageSpan(node)];

    public void SetImage(string name, WzValue image) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(image);
        SetImage(name, (ReadOnlyMemory<byte>) image.ToBytes());
    }

    public void SetImage(string name, ReadOnlyMemory<byte> imageBytes) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        stagedRemovals.Remove(name);
        stagedAdditions.Add(new WzImageEntry(name, imageBytes));
    }

    public void SetImage(string name, byte[] imageBytes) {
        ArgumentNullException.ThrowIfNull(imageBytes);
        SetImage(name, (ReadOnlyMemory<byte>) imageBytes);
    }

    public bool RemoveImage(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        int removedStaged = stagedAdditions.RemoveAll(e => e.Name == name);
        bool existsInBase = Root.Get(name) is { IsImage: true };
        if (existsInBase) {
            stagedRemovals.Add(name);
        }
        return removedStaged > 0 || existsInBase;
    }

    public byte[] ToBytes(ushort? version = null) {
        ushort targetVersion = version ?? Version;
        List<WzImageEntry> baseImages = WzWriter.ArchiveImages(this);
        List<WzImageEntry> merged = WzWriter.MergeImages(baseImages, stagedAdditions, stagedRemovals);
        byte[] bytes = WzWriter.WriteArchive(targetVersion, merged);
        WzWriter.VerifyArchive(bytes, targetVersion, merged);
        return bytes;
    }

    public void Save(string path, ushort? version = null) {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes = ToBytes(version);
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }
        try {
            System.IO.File.WriteAllBytes(path, bytes);
        } catch (Exception ex) {
            throw WzException.Io(path, ex);
        }
    }

    private void AttachArchive(WzNode node) {
        node.Archive = this;
        foreach (WzNode child in node.MutableChildren) {
            AttachArchive(child);
        }
    }

    private static (WzHeader Header, ushort Version, uint VersionHash, WzNode Root) Initialize(
        byte[] data,
        string name,
        ushort? version) {
        WzHeader header = ParseHeader(data);
        ushort resolvedVersion = version ?? DetectVersion(data, header);
        uint vhash = WzCrypto.VersionHash(resolvedVersion);

        var r = new WzReader(data, header.FStart, vhash);
        r.Seek(checked((int) header.FStart + 2));
        List<WzNode> children = ReadDirectory(ref r, data);

        var root = new WzNode(name, WzNodeKind.Directory, 0, 0, 0, children);
        return (header, resolvedVersion, vhash, root);
    }

    internal static WzHeader ParseHeader(ReadOnlySpan<byte> data) {
        if (data.Length < 16) {
            throw WzException.UnexpectedEof(0, 16, data.Length);
        }

        ReadOnlySpan<byte> magic = data[..4];
        if (!magic.SequenceEqual(WzCrypto.Magic)) {
            throw WzException.BadMagic(magic);
        }

        ulong fsize = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(4, 8));
        uint fstart = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4));

        ReadOnlySpan<byte> afterHeader = data[16..];
        int nullIdx = afterHeader.IndexOf((byte) 0);
        int end = nullIdx >= 0 ? 16 + nullIdx : data.Length;
        string copyright = Encoding.UTF8.GetString(data[16..end]);

        if (fstart > (uint) data.Length || (long) fstart + 2 > data.Length) {
            int fs = (int) Math.Min(fstart, (uint) int.MaxValue);
            int have = Math.Max(0, data.Length - fs);
            throw WzException.UnexpectedEof(fs, 2, have);
        }

        ushort encVer = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice((int) fstart, 2));
        return new WzHeader(fsize, fstart, copyright) { EncVer = encVer };
    }

    internal static ushort DetectVersion(ReadOnlySpan<byte> data, WzHeader header) {
        ArgumentNullException.ThrowIfNull(header);

        // Fast-path: check the known client version (779) first before scanning all u16 candidates.
        if (WzCrypto.EncVersion(WzCrypto.ClientVersion) == header.EncVer
            && ProbeDirectoryOffsetsInBounds(data, header.FStart, WzCrypto.ClientVersionHash, out bool isEmpty)) {
            return WzCrypto.ClientVersion;
        }

        ushort? firstMatch = null;
        for (int v = 0; v <= ushort.MaxValue; v++) {
            ushort candidate = (ushort) v;
            if (WzCrypto.EncVersion(candidate) != header.EncVer) {
                continue;
            }
            firstMatch ??= candidate;

            if (ProbeDirectoryOffsetsInBounds(data, header.FStart, WzCrypto.VersionHash(candidate), out isEmpty)) {
                return isEmpty ? WzCrypto.ClientVersion : candidate;
            }
        }

        if (firstMatch.HasValue) {
            return firstMatch.Value;
        }

        throw WzException.VersionNotFound(header.EncVer);
    }

    /// <summary>
    /// Zero-allocation shallow directory probe that verifies all root entry offsets land in-bounds without allocating nodes or strings.
    /// </summary>
    private static bool ProbeDirectoryOffsetsInBounds(
        ReadOnlySpan<byte> data,
        uint fstart,
        uint versionHash,
        out bool isEmpty) {
        isEmpty = false;
        try {
            var r = new WzReader(data, fstart, versionHash);
            r.Seek(checked((int) fstart + 2));

            int count = r.ReadCompressedInt32();
            if (count is < 0 or > 100_000) {
                return false;
            }
            if (count == 0) {
                isEmpty = true;
                return true;
            }

            for (int i = 0; i < count; i++) {
                byte tag = r.ReadByte();
                switch (tag) {
                    case 1:
                        r.Take(10);
                        continue;
                    case 2: {
                        int rel = r.ReadInt32();
                        long atLong = (long) fstart + 1L + rel;
                        if (atLong < 1 || atLong >= data.Length) {
                            return false;
                        }
                        var nr = new WzReader(data, fstart, versionHash);
                        nr.Seek((int) atLong);
                        nr.SkipString();
                        break;
                    }
                    case 3 or 4:
                        r.SkipString();
                        break;
                    default:
                        return false;
                }

                r.ReadCompressedInt32(); // size
                r.ReadCompressedInt32(); // checksum
                uint offset = r.DecryptOffset();
                if (offset >= (uint) data.Length) {
                    return false;
                }
            }

            return true;
        } catch (WzException) {
            return false;
        }
    }

    private static (string Name, WzNodeKind Kind)? ReadEntryHeader(ref WzReader r, ReadOnlySpan<byte> data) {
        int start = r.Position;
        byte tag = r.ReadByte();
        switch (tag) {
            case 1:
                r.Take(10);
                return null;

            case 2: {
                int rel = r.ReadInt32();
                long atLong = (long) r.FStart + 1L + rel;
                if (atLong < 1 || atLong >= data.Length) {
                    throw WzException.UnexpectedEof((int) Math.Clamp(atLong, 0, int.MaxValue), 1, 0);
                }
                int at = (int) atLong;
                byte real = data[at - 1];
                var nr = new WzReader(data, r.FStart, r.VersionHash);
                nr.Seek(at);
                string name = nr.ReadString();
                WzNodeKind kind = real == 3 ? WzNodeKind.Directory : WzNodeKind.Image;
                return (name, kind);
            }

            case 3 or 4: {
                string name = r.ReadString();
                WzNodeKind kind = tag == 3 ? WzNodeKind.Directory : WzNodeKind.Image;
                return (name, kind);
            }

            default:
                throw WzException.BadEntryType(tag, start);
        }
    }

    private static List<WzNode> ReadEntries(ref WzReader r, ReadOnlySpan<byte> data) {
        int count = r.ReadCompressedInt32();
        if (count is < 0 or > 100_000) {
            throw WzException.BadEntryType(0, r.Position);
        }

        var output = new List<WzNode>(count);
        for (int i = 0; i < count; i++) {
            (string Name, WzNodeKind Kind)? header = ReadEntryHeader(ref r, data);
            if (header is null) {
                continue;
            }
            int size = r.ReadCompressedInt32();
            int checksum = r.ReadCompressedInt32();
            uint offset = r.DecryptOffset();
            output.Add(new WzNode(header.Value.Name, header.Value.Kind, offset, size, checksum));
        }
        return output;
    }

    private static List<WzNode> ReadDirectory(ref WzReader r, ReadOnlySpan<byte> data) {
        List<WzNode> nodes = ReadEntries(ref r, data);
        foreach (WzNode n in nodes) {
            if (n.Kind == WzNodeKind.Directory && n.Offset < (uint) data.Length) {
                var sub = new WzReader(data, r.FStart, r.VersionHash);
                sub.Seek((int) n.Offset);
                try {
                    n.MutableChildren.AddRange(ReadDirectory(ref sub, data));
                } catch (WzException) {
                    n.MutableChildren.Clear();
                }
            }
        }
        return nodes;
    }

    public void Dispose() {
        GC.SuppressFinalize(this);
    }
}
