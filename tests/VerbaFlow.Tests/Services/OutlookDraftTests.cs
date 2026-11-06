using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Calendars;
using VerbaFlow.Infrastructure.Export;
using VerbaFlow.Infrastructure.Mail;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

public class OutlookDraftTests
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

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly GraphOptions Options = new("tenant-1", "client-1", "the-secret");
    private const string TokenReply = """{"access_token":"graph-token","expires_in":3600}""";

    private static (GraphMailDrafts Drafts, Handler Handler) Graph(Func<HttpRequestMessage, byte[], HttpResponseMessage>? graph = null)
    {
        var handler = new Handler((r, body) =>
        {
            var url = r.RequestUri!.ToString();
            if (url.Contains("/oauth2/v2.0/token")) return Json(HttpStatusCode.OK, TokenReply);
            if (graph is not null) return graph(r, body);
            if (url.EndsWith("/createUploadSession")) return Json(HttpStatusCode.OK, """{"uploadUrl":"https://upload.example/u1"}""");
            if (url.StartsWith("https://upload.example")) return new HttpResponseMessage(HttpStatusCode.OK);
            return Json(HttpStatusCode.Created, """{"id":"msg-1","webLink":"https://outlook.office365.com/owa/?ItemID=msg-1"}""");
        });
        return (new GraphMailDrafts(new HttpClient(handler), Options, new FixedClock()), handler);
    }

    private static readonly User Signed = T.U("Alice") with { EntraObjectId = "oid-1" };

    [Fact]
    public async Task A_draft_is_made_in_the_persons_own_mailbox_acting_as_them()
    {
        var (drafts, handler) = Graph();
        Assert.False(drafts.CanUse(T.U("Bob"), "token")); // a test user has no Microsoft mailbox
        Assert.False(drafts.CanUse(Signed, null));
        Assert.True(drafts.CanUse(Signed, "user-token"));

        var small = new EmailAttachment("Minutes (approved).pdf", "application/pdf", new byte[1000]);
        var d = await drafts.CreateAsync(Signed, "user-token", new EmailMessage([], "Meeting minutes: Board", "Please find attached", [small]), default);
        Assert.Equal("msg-1", d.Reference);
        Assert.StartsWith("https://outlook.office365.com/", d.WebLink);

        var token = Encoding.UTF8.GetString(handler.Calls[0].Body);
        Assert.Contains("on_behalf_of", token);
        Assert.Contains("assertion=user-token", token);
        Assert.Contains("Mail.ReadWrite", token);
        var create = handler.Calls[1];
        Assert.Equal("https://graph.microsoft.com/v1.0/me/messages", create.Req.RequestUri!.ToString()); // only ever the person's own mailbox
        Assert.Equal("graph-token", create.Req.Headers.Authorization!.Parameter);
        var json = JsonNode.Parse(create.Body)!;
        Assert.Equal("Meeting minutes: Board", json["subject"]!.GetValue<string>());
        Assert.Equal("Minutes (approved).pdf", json["attachments"]![0]!["name"]!.GetValue<string>());
        Assert.Empty(json["toRecipients"]!.AsArray());

        // The token is kept: a second draft asks Microsoft sign-in only once.
        await drafts.CreateAsync(Signed, "user-token", new EmailMessage([], "Again", "x", [small]), default);
        Assert.Single(handler.Calls, c => c.Req.RequestUri!.ToString().Contains("/token"));
    }

    [Fact]
    public async Task A_large_file_is_uploaded_in_pieces_without_the_sign_in_token()
    {
        var (drafts, handler) = Graph();
        var big = new byte[GraphMailDrafts.ChunkSize + 1234];
        new Random(1).NextBytes(big);
        await drafts.CreateAsync(Signed, "user-token", new EmailMessage([], "Big", "x", [new("Long meeting.pdf", "application/pdf", big)]), default);
        var create = JsonNode.Parse(handler.Calls[1].Body)!;
        Assert.Empty(create["attachments"]!.AsArray());
        Assert.EndsWith("/me/messages/msg-1/attachments/createUploadSession", handler.Calls[2].Req.RequestUri!.ToString());
        var puts = handler.Calls.Where(c => c.Req.Method == HttpMethod.Put).ToList();
        Assert.Equal(2, puts.Count);
        Assert.All(puts, p => Assert.Null(p.Req.Headers.Authorization));
        Assert.Equal($"bytes 0-{GraphMailDrafts.ChunkSize - 1}/{big.Length}", puts[0].Req.Content!.Headers.ContentRange!.ToString());
        Assert.Equal(big, puts.SelectMany(p => p.Body).ToArray());
    }

    [Fact]
    public async Task No_mailbox_and_no_consent_are_told_apart_and_the_secret_never_shows()
    {
        var (drafts, _) = Graph((r, _) => Json(HttpStatusCode.NotFound, """{"error":{"code":"MailboxNotEnabledForRESTAPI","message":"REST API is not yet supported for this mailbox."}}"""));
        await Assert.ThrowsAsync<NoMailboxException>(() => drafts.CreateAsync(Signed, "user-token", new EmailMessage([], "s", "t", []), default));
        // A directory with no Exchange Online subscription (seen on the owner's own directory, 2026-11-06).
        var (noExchange, _) = Graph((r, _) => Json(HttpStatusCode.BadRequest, """{"error":{"code":"AuthenticationError","message":"AADSTS500014: The service principal for resource 'https://outlook.office365.com/' is disabled."}}"""));
        await Assert.ThrowsAsync<NoMailboxException>(() => noExchange.CreateAsync(Signed, "user-token", new EmailMessage([], "s", "t", []), default));

        var refusing = new GraphMailDrafts(new HttpClient(new Handler((r, _) =>
            Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"AADSTS65001: The user or administrator has not consented."}"""))), Options, new FixedClock());
        var ex = await Assert.ThrowsAsync<ProviderException>(() => refusing.CreateAsync(Signed, "user-token", new EmailMessage([], "s", "t", []), default));
        Assert.Equal(FaultKind.Settings, ex.Kind);
        Assert.Contains("AADSTS65001", ex.Message);
        Assert.DoesNotContain("the-secret", ex.Message);
        Assert.DoesNotContain("user-token", ex.Message);
    }

    private sealed class NoMailbox : IMailDrafts
    {
        public string Name => "no-mailbox";
        public bool CanUse(User user, string? userToken) => true;
        public Task<MailDraft> CreateAsync(User user, string? userToken, EmailMessage message, CancellationToken ct) =>
            throw new NoMailboxException("Microsoft Graph found no mailbox (404 MailboxNotEnabledForRESTAPI)");
    }

    [Fact]
    public async Task An_export_goes_into_the_drafts_and_is_recorded_or_a_plain_sentence_says_why_not()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var folder = Path.Combine(env.Dir, "drafts");
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, null, new StandInMailDrafts(folder));
        Assert.True(exports.CanDraftInOutlook(env.Alice, null));
        Assert.False(new ExportService(env.Stores, env.Clock).CanDraftInOutlook(env.Alice, null));

        var d = await exports.OutlookDraftAsync(env.Alice, null, id, "pdf");
        var eml = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(folder))));
        Assert.Contains("X-Unsent: 1", eml);
        Assert.Contains("Board meeting", eml);
        var history = await env.Stores.Audit.ListAsync(id);
        Assert.Contains(history, a => a.Type == "item.outlook_draft" && a.Details.Contains(d.Reference));
        Assert.Contains(history, a => a.Type == "item.exported" && a.Details.Contains("outlook-draft"));
        await Assert.ThrowsAsync<ForbiddenException>(() => exports.OutlookDraftAsync(env.Xavier, null, id, "pdf"));

        var failing = new ExportService(env.Stores, env.Clock, null, env.Reporter, null, new NoMailbox());
        var ex = await Assert.ThrowsAsync<DomainException>(() => failing.OutlookDraftAsync(env.Alice, "t", id, "docx"));
        Assert.Contains("no Outlook mailbox", ex.Message);
        Assert.Contains("VF-", ex.Message);
        Assert.DoesNotContain("REST", ex.Message);
        Assert.Contains(await env.Stores.Audit.ListAsync(id), a => a.Type == "outlook_draft.failed" && a.Details.Contains("no mailbox"));
    }
}
