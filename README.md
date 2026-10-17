# Maple.Classic.File

MapleStory Classic World (`mscw`) WZ archive and `.img` property parser, reader, and writer library in C#.

## Projects

- **`Maple.Classic.File.IO`**: Public API (`WzArchive`, `WzNode`, `WzValue`, `WzProperty`, `WzHeader`, `WzException`) with internal binary/crypto implementation details (`WzCrypto`, `WzReader`, `WzWriter`, `WzImage`).
- **`Maple.Classic.File.Tests`**: Unit tests for string decoding, version hashing, offset encryption/decryption, directory traversal, property deserialization, and round-trip archive/image serialization.

## Usage Examples

### Opening a WZ Archive & Listing Nodes
```csharp
using Maple.Classic.File.IO;

using var archive = new WzArchive("Data/String/String_000.wz");
foreach (WzNode node in archive.Nodes) {
    Console.WriteLine($"{node.Name} ({node.Size} bytes)");
}
```

### Parsing an `.img` Property Tree
```csharp
using Maple.Classic.File.IO;

using var archive = new WzArchive("Data/String/String_000.wz");
WzValue eqp = archive.ParseImage("Eqp.img");

string? coatName = eqp["Eqp/Coat/1040000/name"]?.AsString();
Console.WriteLine(coatName);

// Render property tree to JSON
Console.WriteLine(eqp.ToJson());
```

### Editing Images & Saving an Archive
```csharp
using Maple.Classic.File.IO;

using var archive = new WzArchive("Data/String/String_000.wz");
WzValue eqp = archive.ParseImage("Eqp.img");

// Implicit conversions and path-creating indexer
eqp["Eqp/Longcoat/1054555/name"] = "Frieren's Clothes";
eqp["Eqp/Longcoat/1054555/reqLevel"] = 10;

archive.SetImage("Eqp.img", eqp);
archive.Save("String_000.wz");
```
