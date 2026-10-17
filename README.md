# Maple.Classic.File

Strongly typed MapleStory Classic World (`mscw`) WZ archive and `.img` property parser, reader, and writer library in C#.

## Overview

The `mscw` client ships split `PKG1` WZ archives (`<Tree>_000.wz`) with:
- **Game version**: `779`
- **Version hash**: `0x0000E73A` (`encVer = 34` / `0x22`)
- **String key**: Zero key (positional XOR mask `0xAA + k` for ASCII, `0xAAAA + k` for UTF-16LE, no AES keystream)
- **Offset magic**: `0x581C3F6D`

## Projects

- **`Maple.Classic.File.IO`**: WZ archive reader (`WzArchive`, `WzReader`), `.img` property deserializer (`WzImage`, `WzProperty`, `WzValue`), editable property tree (`WzOwnedValue`), and archive/image writer (`WzWriter`, `WzCrypto`).
- **`Maple.Classic.File.Tests`**: Unit tests for string decoding, version hashing, offset encryption/decryption, directory traversal, property deserialization, and round-trip archive/image serialization.

## Usage Examples

### Opening a WZ Archive & Listing Nodes
```csharp
using Maple.Classic.File.IO;

using var archive = WzArchive.Open("Data/String/String_000.wz");
foreach (WzNode node in archive.Root.Children) {
    Console.WriteLine($"{node.Name} ({node.Size} bytes)");
}
```

### Parsing an `.img` Property Tree
```csharp
using Maple.Classic.File.IO;

using var archive = WzArchive.Open("Data/String/String_000.wz");
WzValue eqp = archive.ParseImage("Eqp.img");

string? coatName = eqp["Eqp/Coat/1040000/name"]?.AsString();
Console.WriteLine(coatName);

// Render property tree to JSON
Console.WriteLine(eqp.ToJson());
```

### Editing Images & Rebuilding an Archive
```csharp
using Maple.Classic.File.IO;
using Maple.Classic.File.IO.Crypto;

using var archive = WzArchive.Open("Data/String/String_000.wz");
WzNode eqpNode = archive.Root["Eqp.img"]!;

WzOwnedValue editable = archive.ParseOwnedImage(eqpNode);
editable.SetPath("Eqp/Longcoat/1054555/name", new WzOwnedValue.String("Frieren's Clothes"));

List<WzImageEntry> baseImages = WzWriter.ArchiveImages(archive);
List<WzImageEntry> merged = WzWriter.MergeImages(baseImages, [
    new WzImageEntry("Eqp.img", editable.SerializeImage()),
]);

byte[] outputBytes = WzWriter.WriteArchive(WzCrypto.ClientVersion, merged);
WzWriter.VerifyArchive(outputBytes, WzCrypto.ClientVersion, merged);
File.WriteAllBytes("String_000.wz", outputBytes);
```
