using System.Net;
using System.Text;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Export;
using VerbaFlow.Infrastructure.Mail;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

/// <summary>Outlook.com and Gmail drafts, made with the mail permission the person's own browser got.</summary>
public class OwnMailDraftTests
{
    private sealed class Handler(Func<HttpRequestMessage, byte[], HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, byte[] Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? [] : await r.Content.ReadAsByteArrayAsync(ct);
            Calls.Add((r, body));
            return respond(r, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly EmailMessage Message = new([], "Dictation: Letter", "Please find attached",
        [new EmailAttachment("Letter (draft).pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7 pretend"))]);

    [Fact]
    public async Task Gmail_gets_the_whole_email_with_the_persons_own_permission()
    {
        var handler = new Handler((r, _) => Json(HttpStatusCode.OK, """{"id":"r-123","message":{"id":"m-1"}}"""));
        var drafts = new OwnMailDrafts(new HttpClient(handler)) { Gmail = true };
        var d = await drafts.CreateAsync(MailServices.Gmail, "google-token", Message, default);
        Assert.Equal("r-123", d.Reference);
        Assert.StartsWith("https://mail.google.com/", d.WebLink);
        var (req, body) = Assert.Single(handler.Calls);
        Assert.Equal("https://gmail.googleapis.com/upload/gmail/v1/users/me/drafts?uploadType=media", req.RequestUri!.ToString());
        Assert.Equal("google-token", req.Headers.Authorization!.Parameter);
        Assert.Equal("message/rfc822", req.Content!.Headers.ContentType!.MediaType);
        var eml = Encoding.UTF8.GetString(body);
        Assert.Contains("Letter (draft).pdf", eml);
        Assert.Contains("Subject:", eml);
    }

    [Fact]
    public async Task Outlook_com_drafts_go_straight_to_graph_with_the_persons_own_permission()
    {
        var handler = new Handler((r, _) => Json(HttpStatusCode.Created, """{"id":"msg-9","webLink":"https://outlook.live.com/mail/?ItemID=msg-9"}"""));
        var d = await new OwnMailDrafts(new HttpClient(handler)).CreateAsync(MailServices.Outlook, "graph-token", Message, default);
        Assert.Equal("msg-9", d.Reference);
        var (req, _) = Assert.Single(handler.Calls); // no token exchange, no client secret
        Assert.Equal("https://graph.microsoft.com/v1.0/me/messages", req.RequestUri!.ToString());
        Assert.Equal("graph-token", req.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task A_google_account_without_gmail_is_told_plainly_and_the_token_is_never_kept()
    {
        using var env = new Env();
        var sam = await new EntraUsers(env.Stores, env.Clock).ResolvePersonalAsync(new PersonalIdentity("microsoft:9188040d:sam", "Sam", "sam@outlook.test", "personal Microsoft account"));
        var id = await env.ProcessedRecordingAsync(sam);
        var handler = new Handler((r, _) => Json(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"Mail service not enabled","status":"FAILED_PRECONDITION"}}"""));
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, null, null, new OwnMailDrafts(new HttpClient(handler)) { Gmail = true });

        Assert.Equal((true, true), exports.OwnDrafts(sam));
        Assert.Equal((false, true), exports.OwnDrafts(env.Alice)); // Gmail for anyone; Outlook.com only for a personal Microsoft account
        Assert.Equal((false, false), new ExportService(env.Stores, env.Clock).OwnDrafts(sam));
        await Assert.ThrowsAsync<DomainException>(() => exports.OwnDraftAsync(env.Alice, MailServices.Outlook, "t", id, "pdf"));
        await Assert.ThrowsAsync<ForbiddenException>(() => exports.OwnDraftAsync(env.Alice, MailServices.Gmail, "t", id, "pdf")); // not Alice's recording

        var ex = await Assert.ThrowsAsync<DomainException>(() => exports.OwnDraftAsync(sam, MailServices.Gmail, "secret-google-token", id, "pdf"));
        Assert.Contains("no Gmail mailbox", ex.Message);
        Assert.Contains("VF-", ex.Message);
        var history = await env.Stores.Audit.ListAsync(id);
        Assert.Contains(history, a => a.Type == "gmail_draft.failed" && a.Details.Contains("no mailbox"));
        Assert.DoesNotContain(history, a => a.Details.Contains("secret-google-token"));
        Assert.DoesNotContain(await env.Stores.SupportErrors.ListAsync(), e => e.Detail.Contains("secret-google-token"));
    }

    [Fact]
    public async Task A_made_draft_is_recorded_in_history()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync(env.Alice);
        var handler = new Handler((r, _) => Json(HttpStatusCode.OK, """{"id":"r-7"}"""));
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, null, null, new OwnMailDrafts(new HttpClient(handler)) { Gmail = true });
        await exports.OwnDraftAsync(env.Alice, MailServices.Gmail, "t", id, "pdf");
        var history = await env.Stores.Audit.ListAsync(id);
        Assert.Contains(history, a => a.Type == "item.gmail_draft" && a.Details.Contains("r-7"));
        Assert.Contains(history, a => a.Type == "item.exported" && a.Details.Contains("gmail-draft"));
    }
}
