using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class SearchTests
{
    private static async Task<Guid> Heard(Env env, string text, User? by = null)
    {
        env.Speech.Heard = text;
        return await env.ProcessedRecordingAsync(by ?? env.Alice);
    }

    [Fact]
    public async Task Finds_recordings_by_what_was_said_and_points_at_the_passage()
    {
        using var env = new Env();
        var id = await Heard(env, "We agreed the Café contracts must be signed before Friday");
        await Heard(env, "Nothing about that here at all");
        var search = new SearchService(env.Stores);

        var answer = await search.SearchAsync(env.Alice, "cafe CONTRACT");
        var r = Assert.Single(answer.Results);
        Assert.Equal(id, r.Id);
        var hit = Assert.Single(r.Hits, h => h.Where == "Transcript");
        Assert.Equal(0, hit.StartMs);
        Assert.NotNull(hit.SegmentId);
        Assert.Equal("Speaker 1", hit.Speaker);
        Assert.Equal(["Café", "contracts"], hit.Marks.Select(m => hit.Text.Substring(m[0], m[1])));

        Assert.Empty((await search.SearchAsync(env.Alice, "contract tuesday")).Results); // every word must be there
        Assert.Empty((await search.SearchAsync(env.Alice, "tract")).Results); // only at the start of a word
        Assert.Single((await search.SearchAsync(env.Alice, "\"signed before friday\"")).Results);
        Assert.Empty((await search.SearchAsync(env.Alice, "\"before signed\"")).Results);
        Assert.Empty((await search.SearchAsync(env.Alice, "")).Results);
        Assert.NotNull((await search.SearchAsync(env.Alice, "a")).Problem);
    }

    [Fact]
    public async Task Searches_the_edited_text_only_what_the_person_may_open_and_not_deleted_ones()
    {
        using var env = new Env();
        var id = await Heard(env, "The budget for next year");
        var search = new SearchService(env.Stores);
        Assert.Empty((await search.SearchAsync(env.Bob, "budget")).Results);
        Assert.Single((await search.SearchAsync(env.Carol, "budget")).Results); // administrators see everything

        var seg = (await env.Stores.Transcripts.GetAsync(id))!.Render()[0].Segment.Id;
        await env.Meeting.EditSegmentAsync(env.Alice, id, seg, "The forecast for next year");
        Assert.Empty((await search.SearchAsync(env.Alice, "budget")).Results);
        Assert.Single((await search.SearchAsync(env.Alice, "forecast")).Results);
        Assert.Single((await search.SearchAsync(env.Alice, "forecast", ItemMode.Meeting)).Results);
        Assert.Empty((await search.SearchAsync(env.Alice, "forecast", ItemMode.Dictate)).Results);

        await env.Meeting.DeleteAsync(env.Alice, id, "test");
        Assert.Empty((await search.SearchAsync(env.Alice, "forecast")).Results);
    }

    [Fact]
    public async Task Finds_words_in_the_summary_and_the_name()
    {
        using var env = new Env();
        await Heard(env, "Hello there");
        var search = new SearchService(env.Stores);
        var r = Assert.Single((await search.SearchAsync(env.Alice, "board")).Results); // the recording is called "Board meeting"
        Assert.Contains(r.Hits, h => h.Where == "Details");
        Assert.Contains(Assert.Single((await search.SearchAsync(env.Alice, "summary")).Results).Hits, h => h.Where == "Summary");
    }

    [Fact]
    public void Long_passages_are_cut_around_the_match()
    {
        var text = string.Join(' ', Enumerable.Repeat("filler", 60)) + " the Rhodes contract " + string.Join(' ', Enumerable.Repeat("words", 60));
        var marks = SearchService.Find(text, SearchService.Terms("rhodes"), out _);
        var (snippet, moved) = SearchService.Snippet(text, marks);
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.Equal("Rhodes", snippet.Substring(moved[0][0], moved[0][1]));
        Assert.True(snippet.Length < 260);
    }
}
