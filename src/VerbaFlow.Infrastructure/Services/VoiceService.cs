using System.Buffers.Binary;
using System.Security.Cryptography;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Saved voices ("Your voice"): a person records a short sample, VerbaFlow keeps it with its voice fingerprint, and
/// in-room meetings of their organisation are then named from it (<see cref="VoiceMatcher"/>). Each person saves and
/// removes only their own voice, after agreeing; nobody else can see, hear or change it. The History records that a voice
/// was saved or removed, never the fingerprint.
/// </summary>
public sealed class VoiceService(Stores stores, TimeProvider clock, ISpeakerDiarizer? diarizer = null)
{
    /// <summary>The voice model is needed to make fingerprints; without it voices cannot be saved or compared.</summary>
    public bool Available => diarizer is IVoicePrints;

    public Task<VoiceProfile?> GetAsync(User user) => stores.VoiceProfiles.GetAsync(user.Id);

    /// <summary>How many people of this person's organisation (or just them, for a personal account) have saved a voice.</summary>
    public async Task<int> CountInSpaceAsync(User user) =>
        (await stores.VoiceProfiles.ListAsync()).Count(p => p.Space == user.Space);

    public async Task<VoiceProfile> SaveAsync(User actor, bool agreed, Stream wav, CancellationToken ct = default)
    {
        if (actor.IsSupport) throw new ForbiddenException("VerbaFlow support accounts do not take part in meetings.");
        if (!agreed) throw new DomainException("Tick the box to agree that VerbaFlow keeps your voice before saving it.");
        if (diarizer is not IVoicePrints prints) throw new DomainException("Voices cannot be saved here because VerbaFlow's voice model is switched off.");
        var bytes = await ReadAllAsync(wav, ct);
        var lengthMs = WavLengthMs(bytes) ?? throw new DomainException("This recording cannot be read. Record your voice again on this page.");
        if (lengthMs < VoiceProfile.MinLengthMs) throw new DomainException($"The recording is too short. Speak for at least {VoiceProfile.MinLengthMs / 1000} seconds.");
        if (lengthMs > VoiceProfile.MaxLengthMs) throw new DomainException($"The recording is too long. Keep it under {VoiceProfile.MaxLengthMs / 60000} minutes.");
        var print = await PrintAsync(prints, bytes, lengthMs, ct)
            ?? throw new DomainException("No voice could be heard in the recording. Check the microphone and record again.");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Directory.CreateDirectory(stores.VoiceRoot);
        var path = PathFor(sha);
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes, ct);
        var previous = await stores.VoiceProfiles.GetAsync(actor.Id);
        var now = clock.GetUtcNow();
        var profile = new VoiceProfile(actor.Id, actor.Space, sha, lengthMs, diarizer.Name, print, now, now);
        await stores.VoiceProfiles.UpsertAsync(actor.Id, profile);
        if (previous is not null && previous.AudioSha256 != sha) TryDelete(PathFor(previous.AudioSha256));
        await Audit(actor, "voice.saved", ("lengthMs", lengthMs), ("model", diarizer.Name), ("replaced", previous is not null));
        return profile;
    }

    /// <summary>Removes the person's voice: the sample and the fingerprint. Meetings already named keep their names.</summary>
    public async Task RemoveAsync(User actor)
    {
        if (await stores.VoiceProfiles.GetAsync(actor.Id) is not { } profile) return;
        await RemoveProfileAsync(profile);
        await Audit(actor, "voice.removed");
    }

    /// <summary>Removes a person's voice when their account goes (no History entry of its own).</summary>
    public async Task<bool> RemoveForAsync(Guid userId)
    {
        if (await stores.VoiceProfiles.GetAsync(userId) is not { } profile) return false;
        await RemoveProfileAsync(profile);
        return true;
    }

    private async Task RemoveProfileAsync(VoiceProfile profile)
    {
        await stores.VoiceProfiles.DeleteAsync(profile.Id);
        TryDelete(PathFor(profile.AudioSha256));
    }

    /// <summary>The person's own sample, to listen to.</summary>
    public async Task<byte[]> AudioAsync(User actor)
    {
        var profile = await stores.VoiceProfiles.GetAsync(actor.Id) ?? throw new NotFoundException("You have not saved your voice.");
        var path = PathFor(profile.AudioSha256);
        if (!File.Exists(path)) throw new NotFoundException("The recording of your voice is missing. Record it again.");
        return await File.ReadAllBytesAsync(path);
    }

    /// <summary>
    /// The saved voices a recording in this space is compared with, with the person's current name. A fingerprint made by
    /// another voice model is made again from the sample (and stored); one that cannot be is left out.
    /// </summary>
    public async Task<List<VoiceMatcher.Known>> KnownInAsync(Guid? space, CancellationToken ct)
    {
        var known = new List<VoiceMatcher.Known>();
        if (diarizer is not IVoicePrints prints) return known;
        foreach (var profile in await stores.VoiceProfiles.ListAsync())
        {
            // The person's organisation now, in case they moved since saving.
            var user = await stores.Users.GetAsync(profile.Id);
            if (user is null || user.Space != space) continue;
            var print = profile.Print;
            if (profile.Model != diarizer.Name)
            {
                var path = PathFor(profile.AudioSha256);
                if (!File.Exists(path)) continue;
                var fresh = await PrintAsync(prints, await File.ReadAllBytesAsync(path, ct), profile.LengthMs, ct);
                if (fresh is null) continue;
                print = fresh;
                await stores.VoiceProfiles.UpsertAsync(profile.Id, profile with { Model = diarizer.Name, Print = fresh });
            }
            known.Add(new VoiceMatcher.Known(user.Id, user.Name, print));
        }
        return known;
    }

    private static async Task<float[]?> PrintAsync(IVoicePrints prints, byte[] wav, int lengthMs, CancellationToken ct)
    {
        var stretches = new List<(int, int)>();
        for (var at = 0; at + VoiceProfile.PieceMs / 2 <= lengthMs; at += VoiceProfile.PieceMs)
            stretches.Add((at, Math.Min(lengthMs, at + VoiceProfile.PieceMs)));
        using var audio = new MemoryStream(wav);
        return VoiceMatcher.Average(await prints.PrintsAsync(audio, stretches, ct));
    }

    /// <summary>The length of a PCM WAV file, or null when it is not one.</summary>
    public static int? WavLengthMs(byte[] b)
    {
        if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E') return null;
        int? bytesPerSecond = null;
        var at = 12;
        while (at + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, at, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 4)), int.MaxValue);
            if (id == "fmt " && at + 20 <= b.Length)
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at + 8)) != 1) return null; // PCM only
                bytesPerSecond = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 16));
            }
            else if (id == "data")
            {
                if (bytesPerSecond is not > 0) return null;
                var data = Math.Min(size, b.Length - at - 8);
                return (int)(data * 1000L / bytesPerSecond.Value);
            }
            at += 8 + size + (size & 1);
        }
        return null;
    }

    private static async Task<byte[]> ReadAllAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > VoiceProfile.MaxBytes) throw new DomainException("The recording is too big. Keep it under 2 minutes.");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private string PathFor(string sha) => Path.Combine(stores.VoiceRoot, sha + ".wav");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Task Audit(User actor, string action, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, "user", "platform", null, action, AuditChain.Details(details));
}
