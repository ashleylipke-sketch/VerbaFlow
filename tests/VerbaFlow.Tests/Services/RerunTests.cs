using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

public class RerunTests
{
    /// <summary>Remembers what it was asked, and says everything is one voice.</summary>
    private sealed class SpyDiarizer : ISpeakerDiarizer
    {
        public List<int?> Asked { get; } = [];
        public List<string?> Methods { get; } = [];
        public string Name => "spy";
        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(Stream audio, int? numSpeakers, string? method, CancellationToken ct)
        {
            Asked.Add(numSpeakers); Methods.Add(method);
            return Task.FromResult<IReadOnlyList<SpeakerTurn>>([new SpeakerTurn(0, 1_000_000, 0), new SpeakerTurn(1_000_000, 2_000_000, 1)]);
        }
    }

    private static async Task<(Env env, SpyDiarizer spy, ProcessingService processing, Guid id)> Start()
    {
        var env = new Env();
        var spy = new SpyDiarizer();
        var processing = new ProcessingService(env.Stores, env.Speech, env.Ai, new StandInAudioEnhancer(), env.Clock, env.Reporter, spy);
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording());
        await processing.ProcessAsync(id);
        return (env, spy, processing, id);
    }

    [Fact]
    public async Task The_window_method_is_passed_on_and_recorded_and_unknown_names_mean_the_normal_way()
    {
        var (env, spy, processing, id) = await Start();
        using var _ = env;
        await env.Meeting.RerunAsync(env.Alice, id, null, method: "windowed");
        await processing.ProcessAsync(id);
        await env.Meeting.RerunAsync(env.Alice, id, null, method: "something-else");
        await processing.ProcessAsync(id);
        Assert.Equal<string?>([null, "windowed", null], spy.Methods);
        var events = await env.Stores.Audit.ListAsync(id);
        Assert.Contains(events, a => a.Type == "processing.completed" && a.Details.Contains("\"groupingMethod\":\"windowed\""));
    }

    [Fact]
    public async Task The_speech_service_only_method_skips_the_local_model_and_gives_azure_the_count()
    {
        var (env, spy, processing, id) = await Start();
        using var _ = env;
        await env.Meeting.RerunAsync(env.Alice, id, 4, method: "azure");
        await processing.ProcessAsync(id);
        Assert.Equal(4, env.Speech.LastOptions!.MaxSpeakers);
        Assert.Single(spy.Asked); // only the first run used the local model
        var done = (await env.Stores.Audit.ListAsync(id)).Last(a => a.Type == "processing.completed");
        Assert.Contains("\"groupingMethod\":\"azure\"", done.Details);
        Assert.Contains("\"speakerSeparation\":\"azure\"", done.Details);
    }

    [Fact]
    public async Task Running_again_passes_the_speaker_count_and_records_it_in_the_history()
    {
        var (env, spy, processing, id) = await Start();
        using var _ = env;
        await env.Meeting.RerunAsync(env.Alice, id, 5);
        await processing.ProcessAsync(id);
        Assert.Equal<int?>([null, 5], spy.Asked);
        Assert.Equal(5, (await env.Item(id)).NumSpeakers);
        var events = await env.Stores.Audit.ListAsync(id);
        Assert.Contains(events, a => a.Type == "processing.rerun_requested" && a.Details.Contains("5"));
        var last = events.Last(a => a.Type == "processing.completed");
        Assert.Contains("localSpeakersFound", last.Details);
        Assert.Contains("speakersToldTo", last.Details);
        Assert.Contains("localVoiceSeconds", last.Details);
        Assert.Contains("prepared", last.Details);
    }

    [Fact]
    public async Task Running_again_with_no_count_goes_back_to_automatic()
    {
        var (env, spy, processing, id) = await Start();
        using var _ = env;
        await env.Meeting.RerunAsync(env.Alice, id, 4);
        await processing.ProcessAsync(id);
        await env.Meeting.RerunAsync(env.Alice, id, null);
        await processing.ProcessAsync(id);
        Assert.Equal<int?>([null, 4, null], spy.Asked);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(21)]
    public async Task A_silly_speaker_count_is_refused(int count)
    {
        var (env, _, _, id) = await Start();
        using var __ = env;
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.RerunAsync(env.Alice, id, count));
    }

    [Fact]
    public async Task An_approved_item_cannot_be_run_again()
    {
        var (env, _, _, id) = await Start();
        using var _ = env;
        await env.Meeting.ApproveAsync(env.Alice, id);
        await Assert.ThrowsAnyAsync<Exception>(() => env.Meeting.RerunAsync(env.Alice, id, 3));
    }

    [Fact]
    public async Task Only_the_owner_or_an_admin_can_run_it_again()
    {
        var (env, _, _, id) = await Start();
        using var _ = env;
        await Assert.ThrowsAnyAsync<Exception>(() => env.Meeting.RerunAsync(env.Bob, id, 3));
        await env.Meeting.RerunAsync(env.Carol, id, 3); // an admin may
    }

    [Fact]
    public async Task The_action_is_offered_to_the_owner_until_the_item_is_approved()
    {
        var (env, _, _, id) = await Start();
        using var _ = env;
        var row = (await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null))).Single(r => r.Id == id);
        Assert.Contains("rerun", row.Actions);
        await env.Meeting.ApproveAsync(env.Alice, id);
        row = (await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null))).Single(r => r.Id == id);
        Assert.DoesNotContain("rerun", row.Actions);
    }

    [Fact]
    public async Task An_edited_transcript_is_only_replaced_when_the_owner_says_so()
    {
        var (env, _, processing, id) = await Start();
        using var _ = env;
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        await env.Meeting.EditSegmentAsync(env.Alice, id, t.Segments[0].Id, "something corrected");
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.RerunAsync(env.Alice, id, 3));
        await env.Meeting.RerunAsync(env.Alice, id, 3, null, discardEdits: true);
        await processing.ProcessAsync(id);
        var fresh = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.Equal(1, fresh.VersionNo);
        var ev = (await env.Stores.Audit.ListAsync(id)).Last(a => a.Type == "processing.rerun_requested");
        Assert.Contains("replacedEdits", ev.Details);
    }

    [Fact]
    public async Task A_count_given_when_recording_is_sent_to_the_speech_service_without_the_local_model()
    {
        var env = new Env();
        using var _ = env;
        var spy = new SpyDiarizer();
        var processing = new ProcessingService(env.Stores, env.Speech, env.Ai, new StandInAudioEnhancer(), env.Clock, env.Reporter, spy);
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording() with { NumSpeakers = 4 });
        await processing.ProcessAsync(id);
        var item = await env.Item(id);
        Assert.Equal(4, item.NumSpeakers); Assert.Equal("azure", item.SpeakerMethod);
        Assert.Equal(4, env.Speech.LastOptions!.MaxSpeakers);
        Assert.Empty(spy.Asked);
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.CreateRecordingAsync(env.Alice, env.Recording() with { NumSpeakers = 1 }));
    }
}
