using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class SpokenLanguageTests
{
    private static RecordingUpload Rec(Env env, string? spoken, string output = "en") =>
        new(Env.Audio(), "m.webm", "audio/webm", "Meeting", 60_000, output, true, [], spoken);

    [Theory]
    [InlineData(null, "en", new[] { "en" })]
    [InlineData("en", "en", new[] { "en" })]
    [InlineData("fr", "fr", new[] { "fr" })]
    [InlineData("en,fr", "en", new[] { "en", "fr" })]
    [InlineData("EN+FR", "en", new[] { "en", "fr" })]
    [InlineData("klingon", "en", new[] { "en" })]
    [InlineData("auto", "en", new string[0])]
    [InlineData("en,auto", "en", new string[0])]
    public async Task Only_the_chosen_languages_are_sent_to_the_speech_service(string? spoken, string output, string[] expected)
    {
        using var env = new Env();
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, Rec(env, spoken, output));
        await env.Processing.ProcessAsync(id);
        Assert.Equal(expected, env.Speech.LastOptions!.CandidateLanguages);
    }

    [Fact]
    public async Task Imports_choose_languages_the_same_way()
    {
        using var env = new Env();
        var id = await env.Meeting.ImportAsync(env.Alice, new ImportUpload(Env.Audio(), "c.mp4", "video/mp4", null, 0, "fr", true, "x", "fr"));
        await env.Processing.ProcessAsync(id);
        Assert.Equal(["fr"], env.Speech.LastOptions!.CandidateLanguages);
    }

    [Fact]
    public async Task A_reopened_copy_keeps_the_languages()
    {
        using var env = new Env();
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, Rec(env, "en,fr"));
        await env.Processing.ProcessAsync(id);
        await env.Meeting.ApproveAsync(env.Alice, id);
        var req = await env.Meeting.RequestReopenAsync(env.Alice, id, "fix");
        await env.Meeting.ApproveReopenAsync(env.Carol, req);
        var newId = (await env.Meeting.ApproveReopenAsync(env.Dave, req))!.Value;
        Assert.Equal("en,fr", (await env.Item(newId)).SpokenLanguages);
    }

    [Fact]
    public void Items_saved_before_this_existed_keep_both_languages()
    {
        var item = System.Text.Json.JsonSerializer.Deserialize<VerbaFlow.Core.Domain.Item>("{}", new System.Text.Json.JsonSerializerOptions { IncludeFields = false });
        Assert.Equal("en,fr", item!.SpokenLanguages);
    }

    [Theory]
    [InlineData("af-ZA", new[] { "af-ZA" })]
    [InlineData("EN-za,AF-za", new[] { "en-ZA", "af-ZA" })]
    [InlineData("de-CH", new[] { "de-CH" })]
    [InlineData("nl-BE,fr-BE,en-GB,de-DE", new[] { "nl-BE", "fr-BE", "en-GB" })] // no more than three
    public async Task Any_supported_locale_is_sent_to_the_speech_service_as_chosen(string spoken, string[] expected)
    {
        using var env = new Env();
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, Rec(env, spoken));
        await env.Processing.ProcessAsync(id);
        Assert.Equal(expected, env.Speech.LastOptions!.CandidateLanguages);
    }

    [Theory]
    [InlineData("xh-ZA")] [InlineData("st-ZA")] [InlineData("tn-ZA")] [InlineData("nso-ZA")]
    [InlineData("ts-ZA")] [InlineData("ss-ZA")] [InlineData("ve-ZA")] [InlineData("nr-ZA")]
    public async Task Languages_the_speech_service_cannot_do_yet_are_refused_with_a_plain_sentence(string spoken)
    {
        using var env = new Env();
        var ex = await Assert.ThrowsAsync<VerbaFlow.Core.Domain.DomainException>(() => env.Meeting.CreateRecordingAsync(env.Alice, Rec(env, spoken)));
        Assert.Contains("cannot be transcribed yet", ex.Message);
    }

    [Fact]
    public void Every_language_asked_for_is_in_the_list()
    {
        var wanted = new[] { "en-GB", "fr-FR", "es-ES", "de-DE", "it-IT", "nl-NL", "nl-BE", "mt-MT", "de-CH", "it-CH", "fr-CH", "pl-PL", "uk-UA", "ru-RU",
            "af-ZA", "zu-ZA", "xh-ZA", "st-ZA", "tn-ZA", "nso-ZA", "ts-ZA", "ss-ZA", "ve-ZA", "nr-ZA", "zh-CN", "ja-JP", "ar-SA" };
        var codes = VerbaFlow.Core.Domain.SpokenLanguageCatalogue.All.Select(l => l.Code).ToHashSet();
        Assert.All(wanted, w => Assert.Contains(w, codes));
        Assert.Equal(8, VerbaFlow.Core.Domain.SpokenLanguageCatalogue.All.Count(l => !l.Supported));
    }
}

public class RetryLanguageTests
{
    [Fact]
    public async Task Retrying_a_failed_conversion_can_change_the_languages()
    {
        using var env = new Env();
        env.Speech.Fail = true;
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording() with { SpokenLanguages = "en,fr" });
        await env.Processing.ProcessAsync(id);
        env.Speech.Fail = false;
        await env.Meeting.RetryAsync(env.Alice, id, "en");
        await env.Processing.ProcessAsync(id);
        Assert.Equal(["en"], env.Speech.LastOptions!.CandidateLanguages);
        Assert.Equal("en", (await env.Item(id)).SpokenLanguages);
    }
}
