namespace Maple.Classic.File.IO.Values;

public abstract partial record WzValue {
    /// <summary>
    /// <c>Sound_DX8</c> — audio duration, payload slice, and optional DirectSound wave header.
    /// </summary>
    public sealed record Sound(
        int DataLength,
        int Duration,
        ReadOnlyMemory<byte> Payload = default,
        ReadOnlyMemory<byte> Header = default) : WzValue {
        public bool Equals(Sound? other) {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            if (DataLength != other.DataLength || Duration != other.Duration) return false;
            if (!Payload.IsEmpty && !other.Payload.IsEmpty && !Payload.Span.SequenceEqual(other.Payload.Span)) return false;
            if (!Header.IsEmpty && !other.Header.IsEmpty && !Header.Span.SequenceEqual(other.Header.Span)) return false;
            return true;
        }

        public override int GetHashCode() => HashCode.Combine(DataLength, Duration);
    }
}
