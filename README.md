# Maple.Classic.File

MapleStory Classic World (`mscw`) WZ archive and `.img` property parser, reader, and writer library in C#.

## Projects

- **`Maple.Classic.File.IO`**:
  - **`Archive/`** (`Maple.Classic.File.IO.Archive`): `WzArchive`, `WzNode`, `WzHeader`
  - **`Values/`** (`Maple.Classic.File.IO.Values`): `WzProperty`, `WzCanvasFormat`, and partial `WzValue` hierarchy (`WzValue.cs`, `WzValue.Object.cs`, `WzValue.Canvas.cs`, `WzValue.Sound.cs`, `WzValue.Convex.cs`, `WzValue.RawData.cs`)
  - **`Serialization/`** (`Maple.Classic.File.IO.Serialization`): Internal binary/JSON serialization (`WzReader`, `WzWriter`, `WzImage`)
  - **`Crypto/`** (`Maple.Classic.File.IO.Crypto`): `WzCrypto` version hashing, offset encryption/decryption, and checksums
  - **`WzException.cs`** (`Maple.Classic.File.IO`): `WzException` and `WzErrorKind`
- **`Maple.Classic.File.Tests`**: Unit tests for string decoding, version hashing, offset encryption/decryption, directory traversal, property deserialization, and round-trip archive/image serialization.

## Usage Examples

### Opening a WZ Archive & Listing Nodes
```csharp
using Maple.Classic.File.IO.Archive;

using var archive = new WzArchive("Data/String/String_000.wz");
foreach (WzNode node in archive.Nodes) {
    Console.WriteLine($"{node.Name} ({node.Size} bytes)");
}
```

### Parsing an `.img` Property Tree
```csharp
using Maple.Classic.File.IO.Archive;
using Maple.Classic.File.IO.Values;

using var archive = new WzArchive("Data/String/String_000.wz");
WzValue eqp = archive.ParseImage("Eqp.img");

string? coatName = eqp["Eqp/Coat/1040000/name"]?.AsString();
Console.WriteLine(coatName);

// Render property tree to JSON
Console.WriteLine(eqp.ToJson());
```

### Editing Images & Saving an Archive
```csharp
using Maple.Classic.File.IO.Archive;
using Maple.Classic.File.IO.Values;

using var archive = new WzArchive("Data/String/String_000.wz");
WzValue eqp = archive.ParseImage("Eqp.img");

// Implicit conversions and path-creating indexer
eqp["Eqp/Longcoat/1054555/name"] = "Frieren's Clothes";
eqp["Eqp/Longcoat/1054555/reqLevel"] = 10;

archive.SetImage("Eqp.img", eqp);
archive.Save("String_000.wz");
```
