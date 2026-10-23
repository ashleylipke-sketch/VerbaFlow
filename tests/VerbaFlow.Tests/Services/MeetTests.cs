using System.Text.Json.Nodes;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Security;
using VerbaFlow.Infrastructure.MeetingBots;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

/// <summary>The stand-in note-taker, counting what VerbaFlow asks of it, and able to report a failure instead.</summary>
public sealed class CountingNoteTaker(TimeProvider clock) : INoteTakerService
{
    private readonly StandInNoteTaker inner = new(clock); // no ffmpeg: the sample is sound only
    public int Scheduled, Cancelled, Forgotten;
    public NoteTakerStatus? Instead;
    public List<NoteTakerRequest> Requests { get; } = [];
    public string Name => inner.Name;
    public Task<string> ScheduleAsync(NoteTakerRequest r, CancellationToken ct) { Scheduled++; Requests.Add(r); return inner.ScheduleAsync(r, ct); }
    public Task<NoteTakerStatus> GetAsync(string botId, CancellationToken ct) => Instead is { } s ? Task.FromResult(s) : inner.GetAsync(botId, ct);
    public Task CancelAsync(string botId, CancellationToken ct) { Cancelled++; return inner.CancelAsync(botId, ct); }
    public Task<Stream> DownloadVideoAsync(string url, CancellationToken ct) => inner.DownloadVideoAsync(url, ct);
    public Task<IReadOnlyList<PlatformSpeech>> DownloadSpeakersAsync(string url, CancellationToken ct) => inner.DownloadSpeakersAsync(url, ct);
    public Task ForgetAsync(string botId, CancellationToken ct) { Forgotten++; return inner.ForgetAsync(botId, ct); }
}

public class MeetTests
{
    private static (CountingNoteTaker Bot, MeetService Meet) Make(Env env)
    {
        var bot = new CountingNoteTaker(env.Clock);
        return (bot, new MeetService(env.Stores, bot, env.Meeting, env.Clock, env.Reporter));
    }

    private static NoteTakerBooking Zoom(string link = "https://us02web.zoom.us/j/81234567890?pwd=abc", DateTimeOffset? at = null, string? name = "Board") =>
        new(link, name, at, "en-GB");

    [Theory]
    [InlineData("https://us02web.zoom.us/j/81234567890?pwd=abc", "Zoom", "zoom:81234567890")]
    [InlineData("https://zoom.us/w/81234567890?tk=x&pwd=other", "Zoom", "zoom:81234567890")]
    [InlineData("https://meet.google.com/abc-defg-hij?authuser=1", "Google Meet", "meet:abc-defg-hij")]
    [InlineData("https://teams.microsoft.com/l/meetup-join/19%3ameeting_ABC%40thread.v2/0?context=%7b%7d", "Microsoft Teams", "teams:/l/meetup-join/19:meeting_abc@thread.v2/0")]
    [InlineData("https://acme.webex.com/acme/j.php?MTID=m123ABC", "Webex", "webex:acme.webex.com/acme/j.php?mtid=m123abc")]
    public void Meeting_links_are_recognised_and_the_same_meeting_matches(string link, string platform, string key)
    {
        Assert.Equal(platform, MeetingPlatforms.Of(link));
        Assert.Equal(key, MeetingPlatforms.Key(link));
    }

    [Theory]
    [InlineData("http://zoom.us/j/81234567890")]
    [InlineData("https://zoom.us.evil.example/j/81234567890")]
    [InlineData("https://example.com/meeting")]
    [InlineData("not a link")]
    public void Other_links_are_refused(string link) => Assert.Null(MeetingPlatforms.Of(link));

    [Fact]
    public async Task Only_one_note_taker_joins_however_many_colleagues_book_the_same_meeting()
    {
        using var env = new Env();
        var (bot, meet) = Make(env);
        var first = await meet.BookAsync(env.Alice, Zoom());
        Assert.False(first.JoinedExisting);
        // Bob's invitation has the same meeting with a different passcode part, and he books it for when it starts.
        var second = await meet.BookAsync(env.Bob, Zoom("https://zoom.us/j/81234567890?pwd=zzz", T.Now.AddMinutes(30)));
        Assert.True(second.JoinedExisting);
        Assert.Equal(first.NoteTaker.Id, second.NoteTaker.Id);
        Assert.Equal("Alice", second.NoteTaker.BookedBy);
        var again = await meet.BookAsync(env.Bob, Zoom());
        Assert.True(again.AlreadyBooked);
        Assert.Equal(1, bot.Scheduled);
        Assert.Contains("recording this meeting for Alice", bot.Requests[0].Announcement);
        // A weekly meeting can keep its link: next week's is another meeting and gets its own note-taker.
        var nextWeek = await meet.BookAsync(env.Xavier, Zoom(at: T.Now.AddDays(7)));
        Assert.False(nextWeek.JoinedExisting);
        Assert.Equal(2, bot.Scheduled);
        // Each person sees the bookings they are on; an administrator sees all.
        Assert.Single(await meet.ListAsync(env.Bob));
        Assert.Equal(2, (await meet.ListAsync(env.Carol)).Count);
        var audit = (await env.Stores.Audit.ListAsync()).Where(a => a.Product == "meet").Select(a => a.Type).ToList();
        Assert.Equal(["meet.booked", "meet.joined_booking", "meet.booked"], audit);
        Assert.DoesNotContain("pwd", string.Concat((await env.Stores.Audit.ListAsync()).Select(a => a.Details))); // passcodes stay out of History
    }

    [Fact]
    public async Task The_recording_comes_back_as_a_meeting_item_with_speakers_named_by_the_platform()
    {
        using var env = new Env();
        var (bot, meet) = Make(env);
        var booked = await meet.BookAsync(env.Alice, Zoom());
        await meet.BookAsync(env.Bob, Zoom());
        for (var i = 0; i < 3; i++) Assert.Equal(0, await meet.CheckAsync()); // joining, recording, meeting ended
        Assert.Equal("MeetingEnded", (await meet.ListAsync(env.Alice))[0].State);
        Assert.Equal(1, await meet.CheckAsync());
        Assert.Equal(0, await meet.CheckAsync()); // brought back once only
        var row = (await meet.ListAsync(env.Alice))[0];
        Assert.Equal(("Fetched", "Recording ready"), (row.State, row.StateLabel));
        Assert.Equal(1, bot.Forgotten); // the copy at the note-taker service is removed

        var id = row.ItemId!.Value;
        var item = await env.Item(id);
        Assert.Equal((SourceKind.OnlineMeeting, ItemStatus.WithAuthor, env.Alice.Id), (item.Source, item.Status, item.OwnerId));
        Assert.Equal([env.Bob.Id], item.Attendees);
        Assert.Contains("visible participant", Assert.Single(item.Markers).Note);
        await env.Processing.ProcessAsync(id);
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.Equal(["Lee Stand-in", "Pat Stand-in"], t.Speakers.Select(s => s.Name).Order());
        var done = (await env.Stores.Audit.ListAsync()).Last(a => a.Type == "processing.completed");
        Assert.Contains("\"speakerSeparation\":\"meeting platform\"", done.Details);

        // Bob was in the meeting too: he can read it, not change it, and export it once it is approved.
        var forBob = await env.Meeting.GetItemAsync(env.Bob, id);
        Assert.Equal(("Attendee", false, false), (forBob.Role, forBob.CanEditTranscript, forBob.CanExport));
        Assert.Equal(["Bob"], (await env.Meeting.GetItemAsync(env.Alice, id)).Attendees);
        Assert.Contains(await env.Meeting.ListAsync(env.Bob, new ListQuery(null, null, null)), r => r.Id == id);
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.GetItemAsync(env.Xavier, id));
        Assert.False(PermissionEvaluator.Evaluate(env.Bob, item, Capability.Approve).Allowed);
        await env.Meeting.ApproveAsync(env.Alice, id);
        Assert.True((await env.Meeting.GetItemAsync(env.Bob, id)).CanExport);
    }

    [Fact]
    public async Task Calling_off_takes_one_person_off_a_shared_booking_and_calls_the_note_taker_off_for_the_last()
    {
        using var env = new Env();
        var (bot, meet) = Make(env);
        var b = await meet.BookAsync(env.Alice, Zoom());
        await meet.BookAsync(env.Bob, Zoom());
        var left = await meet.CancelAsync(env.Alice, b.NoteTaker.Id);
        Assert.Equal(("Bob", "Booked", 0), (left.BookedBy, left.StateLabel, bot.Cancelled)); // Bob owns it now
        await Assert.ThrowsAsync<ForbiddenException>(() => meet.CancelAsync(env.Xavier, b.NoteTaker.Id));
        var off = await meet.CancelAsync(env.Bob, b.NoteTaker.Id);
        Assert.Equal(("Cancelled", 1), (off.State, bot.Cancelled));
        Assert.Empty(await env.Stores.Items.ListAsync());
        // Booking the same meeting again sends a new note-taker.
        Assert.False((await meet.BookAsync(env.Alice, Zoom())).JoinedExisting);
    }

    [Fact]
    public async Task A_note_taker_that_is_not_let_in_says_so_plainly()
    {
        using var env = new Env();
        var (bot, meet) = Make(env);
        var b = await meet.BookAsync(env.Alice, Zoom());
        bot.Instead = new NoteTakerStatus(NoteTakerPhase.Failed, "call_ended (timeout_exceeded_waiting_room): no recording was made");
        await meet.CheckAsync();
        var row = (await meet.ListAsync(env.Alice))[0];
        Assert.Equal("Not recorded", row.StateLabel);
        Assert.StartsWith("The host did not let the note-taker in from the lobby. Reference: VF-", row.FailureReason);
        Assert.DoesNotContain("timeout", row.FailureReason); // technical detail stays with support
        Assert.Single(await env.Stores.SupportErrors.ListAsync());
    }

    [Fact]
    public async Task A_meeting_booked_ahead_is_not_asked_about_until_it_starts()
    {
        using var env = new Env();
        var (bot, meet) = Make(env);
        await meet.BookAsync(env.Alice, Zoom(at: T.Now.AddHours(2)));
        bot.Instead = new NoteTakerStatus(NoteTakerPhase.Failed, "should not be asked");
        await meet.CheckAsync();
        Assert.Equal("Scheduled", (await meet.ListAsync(env.Alice))[0].State);
        await Assert.ThrowsAsync<DomainException>(() => meet.BookAsync(env.Alice, Zoom(at: T.Now.AddHours(-1))));
        var bad = await Assert.ThrowsAsync<DomainException>(() => meet.BookAsync(env.Alice, Zoom("https://example.com/x")));
        Assert.Contains("Zoom, Google Meet, Microsoft Teams and Webex", bad.Message);
    }

    [Fact]
    public async Task Deleting_the_meeting_removes_who_spoke_when()
    {
        using var env = new Env();
        var (_, meet) = Make(env);
        await meet.BookAsync(env.Alice, Zoom());
        for (var i = 0; i < 4; i++) await meet.CheckAsync();
        var id = (await meet.ListAsync(env.Alice))[0].ItemId!.Value;
        await env.Processing.ProcessAsync(id);
        Assert.NotNull(await env.Stores.MeetingSpeakers.GetAsync(id));
        await env.Meeting.DeleteAsync(env.Alice, id, "test");
        Assert.Null(await env.Stores.MeetingSpeakers.GetAsync(id));
    }

    [Fact]
    public void Recall_answers_are_read_into_plain_phases()
    {
        static JsonNode Bot(string code, string? sub = null, JsonNode? recordings = null) => new JsonObject
        {
            ["status_changes"] = new JsonArray(new JsonObject { ["code"] = "joining_call" }, new JsonObject { ["code"] = code, ["sub_code"] = sub }),
            ["recordings"] = recordings ?? new JsonArray(),
        };
        Assert.Equal(NoteTakerPhase.WaitingToBeLetIn, RecallNoteTaker.Read(Bot("in_waiting_room")).Phase);
        Assert.Equal(NoteTakerPhase.Recording, RecallNoteTaker.Read(Bot("in_call_recording")).Phase);
        Assert.Equal(NoteTakerPhase.Failed, RecallNoteTaker.Read(Bot("fatal", "bot_errored")).Phase);
        Assert.Equal(NoteTakerPhase.Failed, RecallNoteTaker.Read(Bot("done")).Phase); // done, but nothing was recorded
        var ready = RecallNoteTaker.Read(Bot("done", null, JsonNode.Parse("""
            [{"id":"r1","status":{"code":"done"},"media_shortcuts":{
              "video_mixed":{"status":{"code":"done"},"data":{"download_url":"https://s3.example/v.mp4"},"format":"mp4"},
              "participant_events":{"status":{"code":"done"},"data":{"speaker_timeline_download_url":"https://s3.example/t.json"}}}}]
            """)));
        Assert.Equal((NoteTakerPhase.Ready, "https://s3.example/v.mp4", "https://s3.example/t.json"), (ready.Phase, ready.VideoUrl, ready.SpeakersUrl));
        var processing = RecallNoteTaker.Read(Bot("done", null, JsonNode.Parse("""[{"media_shortcuts":{"video_mixed":{"status":{"code":"processing"}}}}]""")));
        Assert.Equal(NoteTakerPhase.MeetingEnded, processing.Phase);

        var speakers = RecallNoteTaker.ReadSpeakers("""
            [{"participant":{"id":100,"name":"Ann Smith","is_host":true},"start_timestamp":{"absolute":"x","relative":1.5},"end_timestamp":{"absolute":"y","relative":4.25}},
             {"participant":{"id":200,"name":null},"start_timestamp":{"relative":4.5},"end_timestamp":null}]
            """);
        Assert.Equal([new PlatformSpeech(1500, 4250, 100, "Ann Smith"), new PlatformSpeech(4500, 4500, 200, "Participant 200")], speakers);
    }
}
