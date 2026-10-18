namespace Maple.Classic.File.IO.Values;

/// <summary>
/// A named property inside a WZ image or extended property list.
/// </summary>
public sealed record WzProperty(string Name, WzValue Value);
