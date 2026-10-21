using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

public class WidgetTests
{
    private static (LiveSessions Live, WidgetService W) Make(Env env)
    {
        var live = new LiveSessions(env.Stores, new StandInLiveSpeech(), env.Clock);
        return (live, new WidgetService(env.Stores, live, env.Clock));
    }

    [Fact]
    public async Task A_use_keeps_only_facts_and_the_live_text_is_thrown_away_when_it_ends()
    {
        using var env = new Env();
        var (live, w) = Make(env);
        var s = await w.StartAsync(env.Alice, "https://www.Mail.Example.com/compose?to=x", "Edge on Windows", "en-GB");
        Assert.NotNull(live.Redeem(s.LiveId, s.Ticket)); // the extension connects with the ticket, as Speech mode does
        var again = await w.ListenAgainAsync(env.Alice, s.Id);
        Assert.Null(live.Take(env.Alice, s.LiveId, Guid.Empty)); // the first live session is gone
        await w.EndAsync(env.Alice, s.Id, "accepted", 4200, 9, 0);
        Assert.Null(live.Take(env.Alice, again.LiveId, Guid.Empty));

        var row = Assert.Single(await w.ListAsync(env.Alice));
        Assert.Equal(("mail.example.com", "accepted", 2, 9), (row.Site, row.Outcome, row.Listens, row.Words));
        Assert.Empty(await env.Stores.Items.ListAsync()); // no item, no text, no audio
        Assert.Empty(await env.Stores.LiveText.ListAsync());
        var audit = (await env.Stores.Audit.ListAsync()).Where(a => a.Product == "widget").ToList();
        Assert.Equal(["widget.started", "widget.accepted"], audit.Select(a => a.Type));
        Assert.All(audit, a => Assert.Null(a.ItemId));
        Assert.DoesNotContain("compose", string.Concat(audit.Select(a => a.Details))); // only the site, never the page address
        await Assert.ThrowsAsync<DomainException>(() => w.EndAsync(env.Alice, s.Id, "cancelled", null, null, null));
        await Assert.ThrowsAsync<NotFoundException>(() => w.EndAsync(env.Bob, Guid.NewGuid(), "cancelled", null, null, null));
    }

    [Fact]
    public async Task Administrators_switch_the_widget_off_on_websites_and_their_subdomains()
    {
        using var env = new Env();
        var (_, w) = Make(env);
        await Assert.ThrowsAsync<ForbiddenException>(() => w.SetBlockedSitesAsync(env.Alice, ["bank.example"]));
        await Assert.ThrowsAsync<DomainException>(() => w.SetBlockedSitesAsync(env.Carol, ["not a site!"]));
        var p = await w.SetBlockedSitesAsync(env.Carol, ["https://www.Bank.example/login", "bank.example", "pay.test"]);
        Assert.Equal(["bank.example", "pay.test"], p.BlockedSites);
        await Assert.ThrowsAsync<DomainException>(() => w.StartAsync(env.Alice, "online.bank.example", "Edge", "en-GB"));
        await w.StartAsync(env.Alice, "notbank.example", "Edge", "en-GB");
        Assert.Contains("\"added\":\"bank.example,pay.test\"", (await env.Stores.Audit.ListAsync()).Single(a => a.Type == "widget.policy_changed").Details);
        Assert.False((await w.GetPolicyAsync(env.Alice)).CanChange);
    }

    [Fact]
    public async Task People_see_their_own_uses_and_administrators_see_everyones()
    {
        using var env = new Env();
        var (_, w) = Make(env);
        await w.StartAsync(env.Alice, "a.example", "Edge", "en-GB");
        var b = await w.StartAsync(env.Bob, "b.example", "Chrome", "af-ZA");
        await Assert.ThrowsAsync<ForbiddenException>(() => w.EndAsync(env.Alice, b.Id, "accepted", 1, 1, 0));
        Assert.Equal(["a.example"], (await w.ListAsync(env.Alice)).Select(r => r.Site));
        var all = await w.ListAsync(env.Carol);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.User == env.Bob.Name && r.Outcome == "open");
        await Assert.ThrowsAsync<DomainException>(() => w.StartAsync(env.Alice, "a.example", "Edge", "auto"));
    }

    private sealed class FakeGrammar(Func<string, string?> fix, bool fail = false) : IGrammarChecker
    {
        public List<string> Sent { get; } = [];
        public string Name => "fake";
        public Task<IReadOnlyList<GrammarFix>> CheckAsync(IReadOnlyList<GrammarPassage> passages, string language, CancellationToken ct)
        {
            Sent.AddRange(passages.Select(p => p.Text));
            if (fail) throw new InvalidOperationException("down");
            return Task.FromResult<IReadOnlyList<GrammarFix>>(passages.Select(p => (p, t: fix(p.Text))).Where(x => x.t is not null)
                .Select(x => new GrammarFix(x.p.Id, x.t!, "fixed")).ToList());
        }
    }

    [Fact]
    public async Task Fix_grammar_corrects_each_line_and_leaves_saved_text_list_markers_and_rewrites_alone()
    {
        using var env = new Env();
        await env.Stores.SavedText.UpsertAsync(Guid.NewGuid(), new SavedText(Guid.NewGuid(), "disclaimer 1.1", "The information provided is general guidance", env.Alice.Id, env.Clock.GetUtcNow()));
        var g = new FakeGrammar(t => t switch
        {
            "i hope this finds you well i refer to our letter" => "I hope this finds you well. I refer to our letter.",
            "first point" => "First point.",
            "all my love" => "Our gathering will take place next week",
            _ => t + "!",
        });
        var live = new LiveSessions(env.Stores, new StandInLiveSpeech(), env.Clock);
        var w = new WidgetService(env.Stores, live, env.Clock, g, env.Reporter);
        var s = await w.StartAsync(env.Alice, "outlook.office.com", "Chrome on Windows", "en-GB");
        var text = "i hope this finds you well i refer to our letter\n\nThe information provided is general guidance\n• first point\nall my love";
        var r = await w.FixGrammarAsync(env.Alice, s.Id, text);
        Assert.Equal("I hope this finds you well. I refer to our letter.\n\nThe information provided is general guidance\n• First point.\nall my love", r.Text);
        Assert.Equal((2, 1), (r.LinesChanged, r.LeftOutAsRewrites));
        Assert.DoesNotContain("The information provided is general guidance", g.Sent);
        var audit = (await env.Stores.Audit.ListAsync()).Single(a => a.Type == "widget.grammar_checked");
        Assert.DoesNotContain("hope", audit.Details); // counts only, never the text
        await Assert.ThrowsAsync<ForbiddenException>(() => w.FixGrammarAsync(env.Bob, s.Id, "hello"));
    }

    [Fact]
    public async Task A_failed_grammar_check_gives_a_plain_sentence_and_a_reference()
    {
        using var env = new Env();
        var live = new LiveSessions(env.Stores, new StandInLiveSpeech(), env.Clock);
        var w = new WidgetService(env.Stores, live, env.Clock, new FakeGrammar(_ => null, fail: true), env.Reporter);
        var s = await w.StartAsync(env.Alice, "outlook.office.com", "Chrome on Windows", "en-GB");
        var ex = await Assert.ThrowsAsync<DomainException>(() => w.FixGrammarAsync(env.Alice, s.Id, "hello there"));
        Assert.DoesNotContain("down", ex.Message);
        Assert.Contains("VF-", ex.Message);
        Assert.Contains((await env.Stores.Audit.ListAsync()), a => a.Type == "widget.grammar_failed");
    }

    [Fact]
    public void The_history_check_refuses_anything_that_could_be_text()
    {
        WidgetService.RequireContentFree([("site", "mail.example.com"), ("words", 12), ("languages", "en-GB,af-ZA"), ("device", "Edge on Windows")]);
        Assert.Throws<InvalidOperationException>(() => WidgetService.RequireContentFree([("site", "Dear Mr Smith, thank you")]));
        Assert.Throws<InvalidOperationException>(() => WidgetService.RequireContentFree([("text", "anything")]));
        Assert.Throws<InvalidOperationException>(() => WidgetService.RequireContentFree([("device", "Dear Mr Smith, please find attached the report")]));
        Assert.Equal("", WidgetService.SiteOf("javascript:alert(1)"));
        Assert.Equal("Edge 129 on Windows", WidgetService.DeviceOf("Edge 129 on Windows"));
    }
}
