using System.Buffers.Binary;
using System.Text;
using Maple.Classic.File.IO.Crypto;

namespace Maple.Classic.File.IO;

public record WzHeader(ulong FSize, uint FStart, string Copyright, ushort EncVer);

public enum WzNodeKind {
    Directory,
    Image,
}

public class WzNode {
    public string Name { get; set; }
    public WzNodeKind Kind { get; set; }
    public uint Offset { get; set; }
    public int Size { get; set; }
    public int Checksum { get; set; }
    public List<WzNode> Children { get; set; }

    public bool IsDir => Kind == WzNodeKind.Directory;
    public bool IsImage => Kind == WzNodeKind.Image;

    public WzNode(
        string name,
        WzNodeKind kind,
        uint offset = 0,
        int size = 0,
        int checksum = 0,
        List<WzNode>? children = null) {
        Name = name;
        Kind = kind;
        Offset = offset;
        Size = size;
        Checksum = checksum;
        Children = children ?? [];
    }

    /// <summary>
    /// Walks to a descendant node by <c>/</c>-separated path.
    /// </summary>
    public WzNode? Get(string path) {
        WzNode cur = this;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            WzNode? next = null;
            foreach (WzNode child in cur.Children) {
                if (child.Name == part) {
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

    public WzNode? this[string path] => Get(path);

    /// <summary>
    /// Recursively counts all directories and images beneath this node.
    /// </summary>
    public (int Directories, int Images) CountRecursive() {
        int dirs = 0;
        int imgs = 0;
        foreach (WzNode c in Children) {
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
}

/// <summary>
/// Represents a parsed WZ archive: header, detected version, directory tree, and underlying data buffer.
/// </summary>
public class WzArchive : IDisposable {
    public string Path { get; }
    public byte[] Data { get; }
    public WzHeader Header { get; }
    public ushort Version { get; }
    public uint VersionHash { get; }
    public WzNode Root { get; }

    private WzArchive(
        string path,
        byte[] data,
        WzHeader header,
        ushort version,
        uint versionHash,
        WzNode root) {
        Path = path;
        Data = data;
        Header = header;
        Version = version;
        VersionHash = versionHash;
        Root = root;
    }

    /// <summary>
    /// Opens a WZ archive from disk, detecting the version automatically.
    /// </summary>
    public static WzArchive Open(string path) {
        return OpenInner(path, null);
    }

    /// <summary>
    /// Opens a WZ archive from disk with a known version (skips version detection).
    /// </summary>
    public static WzArchive OpenWithVersion(string path, ushort version) {
        return OpenInner(path, version);
    }

    /// <summary>
    /// Parses a WZ archive directly from an in-memory byte array.
    /// </summary>
    public static WzArchive FromBytes(byte[] data, string name = "", ushort? version = null, string path = "") {
        ArgumentNullException.ThrowIfNull(data);
        WzHeader header = ParseHeader(data);
        ushort resolvedVersion = version ?? DetectVersion(data, header);
        uint vhash = WzCrypto.VersionHash(resolvedVersion);

        var r = new WzReader(data, header.FStart, vhash);
        r.Seek(checked((int) header.FStart + 2));
        List<WzNode> children = ReadDirectory(r, data);

        return new WzArchive(
            path,
            data,
            header,
            resolvedVersion,
            vhash,
            new WzNode(name, WzNodeKind.Directory, 0, 0, 0, children));
    }

    /// <summary>
    /// Convenience: returns the <c>&lt;Tree&gt;_000.wz</c> archive path for a data tree directory.
    /// </summary>
    public static string TreeArchive(string dataDir, string tree) {
        return System.IO.Path.Combine(dataDir, tree, $"{tree}_000.wz");
    }

    private static WzArchive OpenInner(string path, ushort? version) {
        byte[] data;
        try {
            data = System.IO.File.ReadAllBytes(path);
        } catch (Exception ex) {
            throw WzException.Io(path, ex);
        }

        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
        return FromBytes(data, stem, version, path);
    }

    /// <summary>
    /// Returns a <see cref="ReadOnlySpan{T}"/> over the raw bytes of an image node.
    /// </summary>
    public ReadOnlySpan<byte> GetImageSpan(WzNode node) {
        ArgumentNullException.ThrowIfNull(node);
        long start = node.Offset;
        long size = Math.Max(0, node.Size);
        long end = start + size;
        if (node.Kind != WzNodeKind.Image || start < 0 || end > Data.Length) {
            throw WzException.OutOfBounds(node.Name, node.Offset, node.Size, Data.Length);
        }
        return Data.AsSpan((int) start, (int) size);
    }

    /// <summary>
    /// Returns a copy of the raw bytes of an image node.
    /// </summary>
    public byte[] ImageBytes(WzNode node) {
        return GetImageSpan(node).ToArray();
    }

    /// <summary>
    /// Deserializes the property tree of an image node.
    /// </summary>
    public WzValue ParseImage(WzNode node) {
        return WzImage.Parse(ImageBytes(node));
    }

    /// <summary>
    /// Resolves a node by <c>/</c>-separated path and deserializes its image property tree.
    /// </summary>
    public WzValue ParseImage(string path) {
        WzNode node = Root.Get(path)
            ?? throw new KeyNotFoundException($"No node found at path \"{path}\".");
        return ParseImage(node);
    }

    /// <summary>
    /// Deserializes an image node into an editable <see cref="WzOwnedValue"/> tree.
    /// </summary>
    public WzOwnedValue ParseOwnedImage(WzNode node) {
        return WzOwnedValue.Parse(ImageBytes(node));
    }

    /// <summary>
    /// Parses the PKG1 header and <c>encVer</c> word at <c>fstart</c>.
    /// </summary>
    public static WzHeader ParseHeader(ReadOnlySpan<byte> data) {
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
        return new WzHeader(fsize, fstart, copyright, encVer);
    }

    /// <summary>
    /// Finds the version by matching <c>encVer</c>, then confirming that every root entry decodes to an in-bounds offset.
    /// </summary>
    public static ushort DetectVersion(byte[] data, WzHeader header) {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(header);

        ushort? firstMatch = null;

        for (int v = 0; v <= ushort.MaxValue; v++) {
            ushort candidate = (ushort) v;
            if (WzCrypto.EncVersion(candidate) != header.EncVer) {
                continue;
            }
            firstMatch ??= candidate;

            var r = new WzReader(data, header.FStart, WzCrypto.VersionHash(candidate));
            r.Seek(checked((int) header.FStart + 2));
            try {
                List<WzNode> children = ReadDirectoryShallow(r, data);
                if (children.Count == 0) {
                    // Empty archive stub: offsets carry no signal, so return client version.
                    return WzCrypto.ClientVersion;
                }
                bool allInBounds = true;
                foreach (WzNode n in children) {
                    if (n.Offset >= (uint) data.Length) {
                        allInBounds = false;
                        break;
                    }
                }
                if (allInBounds) {
                    return candidate;
                }
            } catch (WzException) {
                // Wrong version guess failed directory decode; keep probing.
            }
        }

        if (firstMatch.HasValue) {
            return firstMatch.Value;
        }

        throw WzException.VersionNotFound(header.EncVer);
    }

    private static (string Name, WzNodeKind Kind)? ReadEntryHeader(WzReader r, byte[] data) {
        int start = r.Position;
        byte tag = r.ReadByte();
        switch (tag) {
            case 1:
                // Padding / unused slot.
                r.Take(10);
                return null;

            case 2: {
                // Name and real type live elsewhere in the file.
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

            case 3:
            case 4: {
                string name = r.ReadString();
                WzNodeKind kind = tag == 3 ? WzNodeKind.Directory : WzNodeKind.Image;
                return (name, kind);
            }

            default:
                throw WzException.BadEntryType(tag, start);
        }
    }

    private static List<WzNode> ReadEntries(WzReader r, byte[] data) {
        int count = r.ReadCompressedInt32();
        if (count is < 0 or > 100_000) {
            throw WzException.BadEntryType(0, r.Position);
        }

        var output = new List<WzNode>(count);
        for (int i = 0; i < count; i++) {
            (string Name, WzNodeKind Kind)? header = ReadEntryHeader(r, data);
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

    private static List<WzNode> ReadDirectoryShallow(WzReader r, byte[] data) {
        return ReadEntries(r, data);
    }

    private static List<WzNode> ReadDirectory(WzReader r, byte[] data) {
        List<WzNode> nodes = ReadEntries(r, data);
        foreach (WzNode n in nodes) {
            if (n.Kind == WzNodeKind.Directory && n.Offset < (uint) data.Length) {
                var sub = new WzReader(data, r.FStart, r.VersionHash);
                sub.Seek((int) n.Offset);
                try {
                    n.Children = ReadDirectory(sub, data);
                } catch (WzException) {
                    // A malformed subtree should not sink the whole archive.
                    n.Children = [];
                }
            }
        }
        return nodes;
    }

    public void Dispose() {
        GC.SuppressFinalize(this);
    }
}
