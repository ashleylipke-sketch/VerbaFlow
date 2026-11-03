namespace VerbaFlow.Core.Domain;

/// <summary>
/// How one person ends their letters: the closing ("Yours sincerely,"), their name and job title, and optionally a picture
/// of their handwritten signature. The id is the person's user id. The picture is kept under its fingerprint and never changed.
/// </summary>
public sealed record SignOff(Guid Id, bool On, string Closing, string Name, string? Title, string? PictureSha256, string? PictureType,
    DateTimeOffset ChangedAt)
{
    public const string DefaultClosing = "Yours sincerely,";
}
