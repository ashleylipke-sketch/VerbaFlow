using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;
using Xunit;

namespace VerbaFlow.Tests.Services;

public class SavedVoiceTests
{
    /// <summary>
    /// A pretend voice model. A saved sample of 20 s sounds like voice A, one of 25 s like voice B; in a meeting the
    /// passages starting in an even 4-second slot sound like A and the others like B.
    /// </summary>
    private sealed class FakeVoices : ISpeakerDiarizer, IVoicePrints
    {
        public string Name { get; set; } = "fake-voices";
        public int Calls { get; private set; }
        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(Stream audio, int? n, string? m, CancellationToken ct) => Task.FromResult<IReadOnlyList<SpeakerTurn>>([]);
        public Task<IReadOnlyList<float[]?>> PrintsAsync(Stream audio, IReadOnlyList<(int StartMs, int EndMs)> stretches, CancellationToken ct)
        {
            Calls++;
            float[] a = [1f, 0.1f], b = [0.1f, 1f];
            var sample = audio.Length == Wav(20_000).Length ? a : audio.Length == Wav(25_000).Length ? b : null;
            return Task.FromResult<IReadOnlyList<float[]?>>(stretches.Select(s => (float[]?)(sample ?? (s.StartMs / 4000 % 2 == 0 ? a : b))).ToList());
        }
    }

    /// <summary>A silent 16 kHz mono PCM WAV of this length.</summary>
    private static byte[] Wav(int ms)
    {
        var data = ms * 32;
        var b = new byte[44 + data];
        void Text(int o, string s) { for (var i = 0; i < s.Length; i++) b[o + i] = (byte)s[i]; }
        Text(0, "RIFF"); BitConverter.GetBytes(36 + data).CopyTo(b, 4); Text(8, "WAVE");
        Text(12, "fmt "); BitConverter.GetBytes(16).CopyTo(b, 16); BitConverter.GetBytes((short)1).CopyTo(b, 20); BitConverter.GetBytes((short)1).CopyTo(b, 22);
        BitConverter.GetBytes(16000).CopyTo(b, 24); BitConverter.GetBytes(32000).CopyTo(b, 28); BitConverter.GetBytes((short)2).CopyTo(b, 32); BitConverter.GetBytes((short)16).CopyTo(b, 34);
        Text(36, "data"); BitConverter.GetBytes(data).CopyTo(b, 40);
        return b;
    }

    private static async Task<Guid> MeetingAsync(Env env, FakeVoices voices, VoiceService saved, User owner)
    {
        var processing = new ProcessingService(env.Stores, env.Speech, env.Ai, new StandInAudioEnhancer(), env.Clock, env.Reporter, voices, savedVoices: saved);
        var id = await env.Meeting.CreateRecordingAsync(owner, env.Recording() with { NumSpeakers = 2 });
        await processing.ProcessAsync(id);
        return id;
    }

    [Fact]
    public void The_length_of_a_wav_file_is_read_from_its_header()
    {
        Assert.Equal(20_000, VoiceService.WavLengthMs(Wav(20_000)));
        Assert.Null(VoiceService.WavLengthMs("not a sound file at all, just some words"u8.ToArray()));
    }

    [Fact]
    public async Task A_voice_is_saved_only_with_agreement_and_long_enough()
    {
        using var env = new Env();
        var voices = new VoiceService(env.Stores, env.Clock, new FakeVoices());
        var ex = await Assert.ThrowsAsync<DomainException>(() => voices.SaveAsync(env.Alice, false, new MemoryStream(Wav(20_000))));
        Assert.Contains("agree", ex.Message);
        ex = await Assert.ThrowsAsync<DomainException>(() => voices.SaveAsync(env.Alice, true, new MemoryStream(Wav(5_000))));
        Assert.Contains("too short", ex.Message);

        var saved = await voices.SaveAsync(env.Alice, true, new MemoryStream(Wav(20_000)));
        Assert.Equal(20_000, saved.LengthMs);
        Assert.Equal(Wav(20_000), await voices.AudioAsync(env.Alice));
        await Assert.ThrowsAsync<NotFoundException>(() => voices.AudioAsync(env.Bob)); // nobody else's
        var history = await env.Stores.Audit.ListAsync(null);
        Assert.Contains(history, a => a.Type == "voice.saved" && a.ActorId == env.Alice.Id);

        await voices.RemoveAsync(env.Alice);
        Assert.Null(await voices.GetAsync(env.Alice));
        Assert.Empty(Directory.GetFiles(env.Stores.VoiceRoot));
        Assert.Contains(await env.Stores.Audit.ListAsync(null), a => a.Type == "voice.removed");
    }

    [Fact]
    public async Task Without_the_voice_model_voices_cannot_be_saved()
    {
        using var env = new Env();
        var voices = new VoiceService(env.Stores, env.Clock);
        Assert.False(voices.Available);
        await Assert.ThrowsAsync<DomainException>(() => voices.SaveAsync(env.Alice, true, new MemoryStream(Wav(20_000))));
    }

    [Fact]
    public async Task Meeting_speakers_are_named_from_saved_voices()
    {
        using var env = new Env();
        var model = new FakeVoices();
        var voices = new VoiceService(env.Stores, env.Clock, model);
        await voices.SaveAsync(env.Bob, true, new MemoryStream(Wav(20_000)));   // sounds like A
        await voices.SaveAsync(env.Carol, true, new MemoryStream(Wav(25_000))); // sounds like B
        var id = await MeetingAsync(env, model, voices, env.Alice);

        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        var first = t.Segments.Single(s => s.Text.Contains("passage 1."));
        var second = t.Segments.Single(s => s.Text.Contains("passage 2."));
        Assert.Equal("Bob", t.Speakers.Single(s => s.Id == first.SpeakerId).Label);
        Assert.Equal("Carol", t.Speakers.Single(s => s.Id == second.SpeakerId).Label);
        var done = (await env.Stores.Audit.ListAsync(id)).Single(a => a.Type == "processing.completed");
        Assert.Contains("2 saved voices compared with 2 speakers; 2 named from them", done.Details);
        Assert.Contains("saved-voices", done.Details);
        Assert.DoesNotContain("Carol", done.Details); // counts only, never names
    }

    [Fact]
    public async Task A_speaker_who_sounds_like_nobody_saved_keeps_their_number()
    {
        using var env = new Env();
        var model = new FakeVoices();
        var voices = new VoiceService(env.Stores, env.Clock, model);
        await voices.SaveAsync(env.Bob, true, new MemoryStream(Wav(20_000)));
        var id = await MeetingAsync(env, model, voices, env.Alice);
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.Equal(["Bob", "Speaker 2"], t.Speakers.Select(s => s.Label).Order());
    }

    [Fact]
    public async Task A_personal_accounts_voice_is_never_compared_with_the_organisations_meetings()
    {
        using var env = new Env();
        var model = new FakeVoices();
        var voices = new VoiceService(env.Stores, env.Clock, model);
        var solo = T.U("Pat") with { Individual = true, SignInId = "google:pat" };
        await env.Stores.Users.UpsertAsync(solo.Id, solo);
        await voices.SaveAsync(solo, true, new MemoryStream(Wav(20_000)));
        var id = await MeetingAsync(env, model, voices, env.Alice);
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.DoesNotContain(t.Speakers, s => s.Label == "Pat");
        var done = (await env.Stores.Audit.ListAsync(id)).Single(a => a.Type == "processing.completed");
        Assert.DoesNotContain("saved voice", done.Details);
    }

    [Fact]
    public async Task A_fingerprint_from_another_voice_model_is_made_again()
    {
        using var env = new Env();
        var model = new FakeVoices { Name = "old-model" };
        var voices = new VoiceService(env.Stores, env.Clock, model);
        await voices.SaveAsync(env.Bob, true, new MemoryStream(Wav(20_000)));
        model.Name = "new-model";
        var known = await voices.KnownInAsync(env.Bob.Space, default);
        Assert.Equal("Bob", Assert.Single(known).Name);
        Assert.Equal("new-model", (await voices.GetAsync(env.Bob))!.Model);
    }

    [Fact]
    public void Two_voices_that_sound_alike_name_nobody()
    {
        var speakers = new Dictionary<Guid, float[]> { [Guid.NewGuid()] = [1f, 0f] };
        var match = VoiceMatcher.Match(speakers, [new(Guid.NewGuid(), "Ann", [1f, 0.02f]), new(Guid.NewGuid(), "Ben", [1f, 0.03f])]);
        Assert.Empty(match.Named);
    }
}
