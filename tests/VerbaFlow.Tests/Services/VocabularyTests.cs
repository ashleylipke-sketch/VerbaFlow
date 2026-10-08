using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class VocabularyTests
{
    [Fact]
    public async Task Administrators_add_and_everyone_can_read_in_alphabetical_order()
    {
        using var env = new Env();
        await env.Vocabulary.AddAsync(env.Carol, "Bruno Fernandes", "Player");
        await env.Vocabulary.AddAsync(env.Carol, "Amarin", null);
        var seen = await env.Vocabulary.ListAsync();
        Assert.Equal(["Amarin", "Bruno Fernandes"], seen.Select(t => t.Text));
        Assert.Equal("Player", seen[1].Note);
        Assert.Equal(env.Carol.Id, seen[0].AddedBy);
    }

    [Fact]
    public async Task Ordinary_users_cannot_change_the_vocabulary()
    {
        using var env = new Env();
        var term = await env.Vocabulary.AddAsync(env.Carol, "Amarin", null);
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Vocabulary.AddAsync(env.Alice, "Nope", null));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Vocabulary.ImportAsync(env.Alice, "Nope"));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Vocabulary.RemoveAsync(env.Alice, term.Id));
        Assert.Single(await env.Vocabulary.ListAsync());
    }

    [Fact]
    public async Task Terms_are_tidied_and_duplicates_ignoring_case_are_refused()
    {
        using var env = new Env();
        var t = await env.Vocabulary.AddAsync(env.Carol, "  Manchester    United  ", "  ");
        Assert.Equal("Manchester United", t.Text);
        Assert.Null(t.Note);
        await Assert.ThrowsAsync<DomainException>(() => env.Vocabulary.AddAsync(env.Carol, "manchester united", null));
        await Assert.ThrowsAsync<DomainException>(() => env.Vocabulary.AddAsync(env.Carol, "   ", null));
        await Assert.ThrowsAsync<DomainException>(() => env.Vocabulary.AddAsync(env.Carol, new string('x', VocabularyService.MaxLength + 1), null));
        await Assert.ThrowsAsync<DomainException>(() => env.Vocabulary.AddAsync(env.Carol, "bad\u0007bell", null));
        Assert.Single(await env.Vocabulary.ListAsync());
    }

    [Fact]
    public async Task Import_adds_good_lines_and_reports_the_ones_it_skipped()
    {
        using var env = new Env();
        await env.Vocabulary.AddAsync(env.Carol, "Amarin", null);
        var result = await env.Vocabulary.ImportAsync(env.Carol, "Bruno Fernandes\r\n\r\namarin\nOld Trafford\nOld  Trafford\n" + new string('y', 150));
        Assert.Equal(2, result.Added);
        Assert.Equal(3, result.Skipped.Count);
        Assert.Contains(result.Skipped, s => s.Contains("already there"));
        Assert.Contains(result.Skipped, s => s.Contains("not a valid term"));
        Assert.Equal(["Amarin", "Bruno Fernandes", "Old Trafford"], (await env.Vocabulary.ListAsync()).Select(t => t.Text));
    }

    [Fact]
    public async Task The_library_has_a_size_limit()
    {
        using var env = new Env();
        var lines = string.Join("\n", Enumerable.Range(0, VocabularyService.MaxTerms + 5).Select(i => $"term {i}"));
        var r = await env.Vocabulary.ImportAsync(env.Carol, lines);
        Assert.Equal(VocabularyService.MaxTerms, r.Added);
        Assert.Equal(5, r.Skipped.Count);
        await Assert.ThrowsAsync<DomainException>(() => env.Vocabulary.AddAsync(env.Carol, "one too many", null));
    }

    [Fact]
    public async Task Removing_a_term_works_and_every_change_is_in_the_audit_trail()
    {
        using var env = new Env();
        var term = await env.Vocabulary.AddAsync(env.Carol, "Amarin", null);
        await env.Vocabulary.RemoveAsync(env.Carol, term.Id);
        Assert.Empty(await env.Vocabulary.ListAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => env.Vocabulary.RemoveAsync(env.Carol, term.Id));
        var actions = (await env.Stores.Audit.ListAsync(null)).Select(a => a.Type).ToList();
        Assert.Contains("vocabulary.added", actions);
        Assert.Contains("vocabulary.removed", actions);
        Assert.True((await env.Meeting.VerifyAuditAsync(env.Carol)).Ok);
    }

    [Fact]
    public async Task Processing_sends_the_vocabulary_to_the_speech_service_and_records_how_many_terms_were_used()
    {
        using var env = new Env();
        await env.Vocabulary.AddAsync(env.Carol, "Amarin", null);
        await env.Vocabulary.AddAsync(env.Carol, "Bruno Fernandes", null);
        var id = await env.ProcessedRecordingAsync();
        Assert.Equal(["Amarin", "Bruno Fernandes"], env.Speech.LastOptions!.Phrases!.OrderBy(x => x));
        var done = (await env.Stores.Audit.ListAsync(id)).Single(a => a.Type == "processing.completed");
        Assert.Contains("\"vocabularyTerms\":2", done.Details);
    }
}
