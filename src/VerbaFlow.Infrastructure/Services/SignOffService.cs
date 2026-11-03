using System.Security.Cryptography;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Each person's letter sign-off. Only the person themselves sets it. Their dictated letters end with it when exported:
/// the closing, a picture of their signature (only once the letter is approved), their name and job title.
/// </summary>
public sealed class SignOffService(Stores stores, TimeProvider clock)
{
    public const long MaxPictureBytes = 1024 * 1024;
    public const int MaxText = 100;

    /// <summary>The person's sign-off, or a suggested one (switched off) when they have not set one yet.</summary>
    public async Task<SignOff> GetAsync(User user) =>
        await stores.SignOffs.GetAsync(user.Id) ?? new SignOff(user.Id, false, SignOff.DefaultClosing, user.Name, null, null, null, default);

    /// <summary>The sign-off a letter by this person should carry, or null when they have none switched on.</summary>
    public async Task<SignOff?> ForAsync(Guid userId) => await stores.SignOffs.GetAsync(userId) is { On: true } s ? s : null;

    public async Task<SignOff> SaveAsync(User actor, bool on, string? closing, string? name, string? title)
    {
        closing = Tidy(closing, "closing");
        name = Tidy(name, "name");
        title = Tidy(title, "job title");
        if (on && name.Length == 0) throw new DomainException("Type the name to print under your signature.");
        var current = await GetAsync(actor);
        var next = current with { On = on, Closing = closing, Name = name, Title = title.Length == 0 ? null : title, ChangedAt = clock.GetUtcNow() };
        await stores.SignOffs.UpsertAsync(actor.Id, next);
        await Audit(actor, "signoff.changed", ("on", on), ("hasTitle", next.Title is not null), ("hasClosing", closing.Length > 0));
        return next;
    }

    public async Task<SignOff> SetPictureAsync(User actor, Stream content)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await content.ReadAsync(buffer)) > 0)
        {
            if (ms.Length + n > MaxPictureBytes) throw new DomainException("The picture is too big. Use one of at most 1 MB.");
            ms.Write(buffer, 0, n);
        }
        var bytes = ms.ToArray();
        var type = PictureType(bytes) ?? throw new DomainException("Use a PNG or JPEG picture of your signature.");
        try { using var check = PdfSharp.Drawing.XImage.FromStream(new MemoryStream(bytes)); if (check.PixelWidth < 1) throw new FormatException(); }
        catch { throw new DomainException("This picture cannot be read. Save it again as PNG or JPEG and try again."); }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Directory.CreateDirectory(stores.SignatureRoot);
        var path = PathFor(sha, type);
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
        var current = await GetAsync(actor);
        var next = current with { PictureSha256 = sha, PictureType = type, ChangedAt = clock.GetUtcNow() };
        await stores.SignOffs.UpsertAsync(actor.Id, next);
        await Audit(actor, "signoff.picture_added", ("sha256", sha), ("previousSha256", current.PictureSha256), ("bytes", bytes.LongLength));
        return next;
    }

    public async Task<SignOff> RemovePictureAsync(User actor)
    {
        var current = await GetAsync(actor);
        if (current.PictureSha256 is null) return current;
        var next = current with { PictureSha256 = null, PictureType = null, ChangedAt = clock.GetUtcNow() };
        await stores.SignOffs.UpsertAsync(actor.Id, next);
        await Audit(actor, "signoff.picture_removed", ("sha256", current.PictureSha256));
        return next;
    }

    /// <summary>The stored signature picture, or null.</summary>
    public async Task<byte[]?> PictureAsync(SignOff s)
    {
        if (s.PictureSha256 is null || s.PictureType is null) return null;
        var path = PathFor(s.PictureSha256, s.PictureType);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }

    private string PathFor(string sha, string type) => Path.Combine(stores.SignatureRoot, sha + (type == "png" ? ".png" : ".jpg"));

    public static string? PictureType(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G' ? "png"
        : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "jpeg" : null;

    private static string Tidy(string? text, string what)
    {
        var t = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (t.Length > MaxText) throw new DomainException($"The {what} is too long. Keep it under {MaxText} characters.");
        return t;
    }

    private Task Audit(User actor, string action, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, "user", "platform", null, action, AuditChain.Details(details));
}
