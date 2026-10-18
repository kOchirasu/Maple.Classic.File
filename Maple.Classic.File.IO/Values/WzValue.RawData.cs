namespace Maple.Classic.File.IO.Values;

public abstract partial record WzValue {
    /// <summary>
    /// <c>RawData</c> — opaque length-delimited blob (embedded fonts, colour LUTs).
    /// </summary>
    public sealed record RawData(int DataLength, ReadOnlyMemory<byte> Payload = default) : WzValue {
        public RawData(ReadOnlyMemory<byte> payload) : this(payload.Length, payload) { }

        public bool Equals(RawData? other) {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            if (DataLength != other.DataLength) return false;
            if (!Payload.IsEmpty && !other.Payload.IsEmpty && !Payload.Span.SequenceEqual(other.Payload.Span)) return false;
            return true;
        }

        public override int GetHashCode() => DataLength.GetHashCode();
    }
}
