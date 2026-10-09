using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

public class SmallVoiceTests
{
    private static SpeakerTurn T(int speaker, int startS, int endS) => new(startS * 1000, endS * 1000, speaker);

    [Fact]
    public void A_voice_that_spoke_for_a_second_is_folded_into_the_nearest_real_speaker()
    {
        var turns = new[] { T(0, 0, 40), T(7, 40, 41), T(1, 42, 80), T(0, 80, 120), T(9, 118, 119) };
        var (cleaned, merged) = SpeakerTurnCleaner.AbsorbSmallVoices(turns, 8_000);
        Assert.Equal(2, merged);
        Assert.Equal([0, 0, 1, 0, 0], cleaned.Select(t => t.Speaker));
        Assert.Equal(turns.Select(t => t.StartMs), cleaned.Select(t => t.StartMs)); // timings untouched
    }

    [Fact]
    public void Nothing_changes_when_every_voice_spoke_enough_or_the_rule_is_off()
    {
        var turns = new[] { T(0, 0, 40), T(1, 40, 80) };
        Assert.Same(turns, SpeakerTurnCleaner.AbsorbSmallVoices(turns, 8_000).Turns);
        var tiny = new[] { T(0, 0, 40), T(1, 40, 41) };
        Assert.Equal(0, SpeakerTurnCleaner.AbsorbSmallVoices(tiny, 0).Merged);
    }

    [Fact]
    public void If_everyone_is_small_the_one_who_spoke_most_is_kept()
    {
        var (cleaned, merged) = SpeakerTurnCleaner.AbsorbSmallVoices([T(0, 0, 3), T(1, 3, 4), T(2, 4, 5)], 8_000);
        Assert.Equal(2, merged);
        Assert.All(cleaned, t => Assert.Equal(0, t.Speaker));
    }

    private sealed class ManyVoices : ISpeakerDiarizer
    {
        public string Name => "many";
        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(Stream audio, int? numSpeakers, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SpeakerTurn>>([T(0, 0, 100), T(1, 100, 200), T(5, 50, 51), T(6, 150, 151)]);
    }

    private static async Task<int> SpeakersFor(int? told)
    {
        using var env = new Env();
        var opts = new DiarizationOptions("x", null, MinSpeakerSeconds: 8f);
        var processing = new ProcessingService(env.Stores, env.Speech, env.Ai, new StandInAudioEnhancer(), env.Clock, env.Reporter, new ManyVoices(), opts);
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording());
        if (told is not null) { await processing.ProcessAsync(id); await env.Meeting.RerunAsync(env.Alice, id, told, null, true); }
        await processing.ProcessAsync(id);
        var done = (await env.Stores.Audit.ListAsync(id)).Last(a => a.Type == "processing.completed");
        var folded = System.Text.Json.JsonDocument.Parse(done.Details).RootElement.GetProperty("smallVoicesFolded");
        return folded.ValueKind == System.Text.Json.JsonValueKind.Number ? folded.GetInt32() : 0;
    }

    [Fact]
    public async Task Small_voices_are_folded_in_automatic_mode_and_the_history_says_so() => Assert.Equal(2, await SpeakersFor(null));

    [Fact]
    public async Task A_count_given_by_the_owner_is_trusted_and_nothing_is_folded() => Assert.Equal(0, await SpeakersFor(4));
}
