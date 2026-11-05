using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class NotificationTests
{
    private sealed class Mailbox : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public string Name => "test-mail";
        public Task<string> SendAsync(EmailMessage message, CancellationToken ct) { Sent.Add(message); return Task.FromResult("ref"); }
    }

    private static (NotificationService N, Mailbox Mail) Make(Env env)
    {
        var mail = new Mailbox();
        return (new NotificationService(env.Stores, mail, env.Clock, env.Reporter, "https://verbaflow.example"), mail);
    }

    private static async Task<List<string>> Texts(NotificationService n, User u) =>
        (await n.ListAsync(u)).Items.Select(i => i.Text).ToList();

    [Fact]
    public async Task The_workflow_tells_the_right_people_once_each_and_by_email()
    {
        using var env = new Env();
        var (n, mail) = Make(env);
        await n.ProcessAsync(); // starts at the end of the history
        var id = await env.ProcessedRecordingAsync();
        await n.ProcessAsync();
        Assert.Equal(["“Board meeting” is ready to check."], await Texts(n, env.Alice));

        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        await n.ProcessAsync();
        await n.ProcessAsync(); // nothing new the second time
        Assert.Equal(["Alice assigned “Board meeting” to you."], await Texts(n, env.Bob));
        var toBob = Assert.Single(mail.Sent, m => m.To.Contains(env.Bob.Email));
        Assert.Contains("https://verbaflow.example/#/items/" + id, toBob.PlainText);

        await env.Meeting.AcceptAsync(env.Bob, id);
        await env.Meeting.ReturnAsync(env.Bob, id);
        await env.Meeting.ApproveAsync(env.Alice, id);
        await n.ProcessAsync();
        Assert.Equal("Bob returned “Board meeting” to you.", (await Texts(n, env.Alice))[0]);
        Assert.Equal("“Board meeting” was approved by Alice.", (await Texts(n, env.Bob))[0]);
        Assert.DoesNotContain(await Texts(n, env.Alice), t => t.Contains("approved")); // not told about what they did themselves

        var list = await n.ListAsync(env.Bob);
        Assert.Equal(2, list.Unread);
        Assert.Equal(1, (await n.MarkReadAsync(env.Bob, list.Items[0].Id)).Unread);
        Assert.Equal(0, (await n.MarkReadAsync(env.Bob, null)).Unread);
        Assert.Empty(await Texts(n, env.Xavier));
    }

    [Fact]
    public async Task Nothing_from_before_notifications_existed_and_email_can_be_switched_off()
    {
        using var env = new Env();
        var (n, mail) = Make(env);
        var old = await env.ProcessedRecordingAsync();
        await env.Meeting.AssignAsync(env.Alice, old, env.Bob.Id, null);
        await n.ProcessAsync();
        Assert.Empty(await Texts(n, env.Bob));

        var settings = await n.SetEmailAsync(env.Bob, [NotificationGroups.Ready, NotificationGroups.Due]);
        Assert.False(settings.Groups.Single(g => g.Key == NotificationGroups.Work).Email);
        Assert.DoesNotContain(settings.Groups, g => g.Key == NotificationGroups.Approvals); // Bob is not an administrator
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        await n.ProcessAsync();
        Assert.Single(await Texts(n, env.Bob));
        Assert.DoesNotContain(mail.Sent, m => m.To.Contains(env.Bob.Email));
        Assert.Contains(mail.Sent, m => m.To.Contains(env.Alice.Email)); // Alice still gets "ready" by email
    }

    [Fact]
    public async Task Due_dates_are_told_once_to_whoever_has_the_recording()
    {
        using var env = new Env();
        var (n, _) = Make(env);
        await n.ProcessAsync();
        var id = await env.ProcessedRecordingAsync();
        var tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(env.Clock.Now, TimeZoneInfo.Local).Date).AddDays(1);
        await env.Meeting.UpdateDetailsAsync(env.Alice, id, new DetailsUpdate(null, null, null, null, tomorrow, false));
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        env.Clock.Now += TimeSpan.FromMinutes(11); // due dates are looked at every 10 minutes
        await n.ProcessAsync();
        Assert.Contains("“Board meeting” is due tomorrow.", await Texts(n, env.Bob));

        env.Clock.Now += TimeSpan.FromMinutes(11);
        await n.ProcessAsync();
        Assert.Single(await Texts(n, env.Bob), t => t.Contains("due"));

        env.Clock.Now += TimeSpan.FromDays(2);
        await n.ProcessAsync();
        Assert.Contains(await Texts(n, env.Bob), t => t.StartsWith("“Board meeting” is overdue."));
        Assert.DoesNotContain(await Texts(n, env.Alice), t => t.Contains("due"));
    }

    [Fact]
    public async Task Requests_reach_the_other_administrators_and_answers_reach_the_requester()
    {
        using var env = new Env();
        var (n, _) = Make(env);
        await n.ProcessAsync();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        var req = await env.Meeting.RequestReopenAsync(env.Carol, id, "Wrong speaker names");
        await n.ProcessAsync();
        Assert.Contains("Carol asks to reopen “Board meeting”. Two administrators must agree.", await Texts(n, env.Dave));
        Assert.DoesNotContain(await Texts(n, env.Carol), t => t.Contains("asks to reopen"));
        Assert.DoesNotContain(await Texts(n, env.Alice), t => t.Contains("asks to reopen"));

        await env.Meeting.RejectReopenAsync(env.Dave, req, "Not needed");
        await n.ProcessAsync();
        Assert.Contains("Dave turned down reopening “Board meeting”.", await Texts(n, env.Carol));
    }
}
