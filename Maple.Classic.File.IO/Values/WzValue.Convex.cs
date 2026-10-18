namespace Maple.Classic.File.IO.Values;

public abstract partial record WzValue {
    /// <summary>
    /// <c>Shape2D#Convex2D</c>
    /// </summary>
    public sealed record Convex(List<WzValue> Items) : WzValue {
        public Convex() : this(new List<WzValue>()) { }

        public bool Equals(Convex? other) {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            return Items.SequenceEqual(other.Items);
        }

        public override int GetHashCode() {
            var hc = new HashCode();
            foreach (WzValue item in Items) {
                hc.Add(item);
            }
            return hc.ToHashCode();
        }
    }
}
