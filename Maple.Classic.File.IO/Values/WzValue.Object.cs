namespace Maple.Classic.File.IO.Values;

public abstract partial record WzValue {
    /// <summary>
    /// A nested property list (<c>Property</c>).
    /// </summary>
    public sealed record Object(List<WzProperty> Properties) : WzValue {
        public Object() : this(new List<WzProperty>()) { }

        public void Add(string name, WzValue value) => Set(name, value);

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
}
