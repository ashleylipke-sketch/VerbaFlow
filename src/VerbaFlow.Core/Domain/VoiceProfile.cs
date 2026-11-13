namespace VerbaFlow.Core.Domain;

/// <summary>
/// A person's saved voice, so in-room meetings can name them without introductions. Only the person themselves can save
/// or remove it, after agreeing to it (a voice fingerprint is personal data). Kept by user id: one per person.
/// </summary>
/// <param name="Space">Whose recordings it is compared with: the person's organisation (stored as for items) or, for a
/// personal account, the person alone (<see cref="User.Space"/>, matched with <see cref="Item.Space"/>).</param>
/// <param name="AudioSha256">The sample the person recorded (a WAV kept in the voices folder), so the fingerprint can be made
/// again when the voice model changes, and the person can listen to it.</param>
/// <param name="Model">The voice model that made <paramref name="Print"/>: fingerprints of different models cannot be compared.</param>
/// <param name="Print">The voice fingerprint: the average of the sample's pieces, length 1.</param>
public sealed record VoiceProfile(Guid Id, Guid? Space, string AudioSha256, int LengthMs, string Model, float[] Print,
    DateTimeOffset AgreedAt, DateTimeOffset SavedAt)
{
    /// <summary>A shorter sample gives an unreliable fingerprint.</summary>
    public const int MinLengthMs = 20_000;
    public const int MaxLengthMs = 120_000;
    public const long MaxBytes = 10L * 1024 * 1024;
    /// <summary>The sample is fingerprinted in pieces of this length and the pieces are averaged.</summary>
    public const int PieceMs = 3000;
}
