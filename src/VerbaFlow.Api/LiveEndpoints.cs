using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>
/// Speech mode's live connection. The browser opens a session (signed in as usual), then connects to it with a
/// single-use ticket and sends the microphone as 16 kHz, 16-bit mono PCM. The text comes back as it is heard, with
/// spoken commands already applied. A lost connection is made again by the browser, which sends the audio again from
/// the end of the last finished phrase, so nothing is missed.
/// </summary>
public static class LiveEndpoints
{
    public sealed record StartBody(string? SpokenLanguages, Guid? CarryOn = null);
    private sealed record Para(string Text, bool? NewParagraph, string? List);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapLiveEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/live", async (HttpContext h, LiveSessions l, Stores stores, StartBody b) =>
        {
            var user = h.Current();
            if (b.CarryOn is not { } itemId)
            {
                var (s, ticket) = await l.StartAsync(user, b.SpokenLanguages);
                return Results.Ok(new { id = s.Id, ticket, engine = l.EngineName });
            }
            // Carrying on with an unfinished Speech item: its languages, and the text heard before, come back.
            var item = await stores.RequireItemAsync(itemId);
            if (item.OwnerId != user.Id) throw new ForbiddenException("Only the person who spoke this can carry on with it.");
            if (item.Mode != ItemMode.Speech || item.Status != ItemStatus.Draft) throw new DomainException("This item cannot be carried on.");
            var saved = await stores.LiveText.GetAsync(itemId);
            var (c, t) = await l.StartAsync(user, item.SpokenLanguages, saved ?? new LiveTranscript(itemId, l.EngineName, [], true, 0, 0, null), item.LengthMs);
            var (text, heardTo) = c.Snapshot();
            return Results.Ok(new { id = c.Id, ticket = t, engine = l.EngineName, segments = Shown(text), finalToMs = heardTo, spokenLanguages = item.SpokenLanguages });
        });
        api.MapPost("/live/{id:guid}/resume", (HttpContext h, LiveSessions l, Guid id) => { l.Resume(h.Current(), id); return Results.NoContent(); });
        api.MapPost("/live/{id:guid}/ticket", (HttpContext h, LiveSessions l, Guid id) => Results.Ok(new { ticket = l.NewTicket(h.Current(), id) }));
        api.MapPost("/live/{id:guid}/discard", (HttpContext h, LiveSessions l, Guid id) => { l.Discard(h.Current(), id); return Results.NoContent(); });
    }

    /// <summary>The live connection itself. It sits outside the signed-in API group: the ticket is the sign-in.</summary>
    public static void MapLiveSocket(this WebApplication app) => app.Map("/api/live/{id:guid}/socket", Socket);

    private static async Task Socket(HttpContext h, Guid id, LiveSessions sessions, FailureReporter reporter)
    {
        if (!h.WebSockets.IsWebSocketRequest) { h.Response.StatusCode = 400; return; }
        var session = sessions.Redeem(id, h.Request.Query["ticket"]);
        if (session is null) { h.Response.StatusCode = 401; return; }
        _ = int.TryParse(h.Request.Query["fromMs"], out var fromMs);
        using var ws = await h.WebSockets.AcceptWebSocketAsync();
        var (gen, stop) = session.Connect(fromMs);
        var offset = Math.Max(0, fromMs);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop, h.RequestAborted);
        ILiveRecognition rec;
        try { rec = await sessions.RecogniseAsync(session, offset, cts.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailAsync(ws, session, gen, reporter, ex);
            return;
        }
        await using var recognition = rec;

        var (finals, finalTo) = session.Snapshot();
        var finalWords = Words(finals);
        await SendAsync(ws, new { type = "ready", segments = Shown(finals), finalToMs = finalTo, engine = sessions.EngineName }, cts.Token);
        var receive = ReceiveAsync(ws, rec, session, gen, cts);
        try
        {
            await foreach (var e in rec.Events.ReadAllAsync(cts.Token))
            {
                if (e.Kind == LiveEventKind.Failed)
                {
                    await FailAsync(ws, session, gen, reporter, new InvalidOperationException(e.Failure ?? "Live recognition failed."));
                    await cts.CancelAsync();
                    break;
                }
                if (e.Kind == LiveEventKind.Final && e.Phrase is { } p)
                {
                    if (!session.AddFinal(gen, Shift(p, offset))) break; // a newer connection has taken over
                    (finals, finalTo) = session.Snapshot();
                    finalWords = Words(finals);
                    await SendAsync(ws, new { type = "text", segments = Shown(finals), pendingWords = 0, finalToMs = finalTo, heardToMs = offset + e.EndMs }, cts.Token);
                }
                else if (e.Kind == LiveEventKind.Partial && !string.IsNullOrWhiteSpace(e.Text))
                {
                    // The words still being heard go through the same commands, so "comma" shows as "," straight away.
                    var partial = new Segment(Guid.Empty, Guid.Empty, "", offset + e.StartMs, offset + e.EndMs, e.Text.Trim(), 1, false);
                    List<Segment> with = [.. session.PhrasesSoFar(), partial];
                    var shown = DictationContext.Apply(with, session.Saved, session.Phrases);
                    await SendAsync(ws, new { type = "text", segments = Shown(shown), pendingWords = Math.Max(0, Words(shown) - finalWords), finalToMs = finalTo, heardToMs = offset + e.EndMs }, cts.Token);
                }
            }
        }
        catch (OperationCanceledException) { /* the connection closed, or a newer one took over */ }
        catch (WebSocketException) { /* the browser went away */ }

        var ended = await receive;
        if (ended && !session.Failed && ws.State == WebSocketState.Open)
        {
            session.Finish(gen);
            (finals, finalTo) = session.Snapshot();
            try
            {
                await SendAsync(ws, new { type = "done", segments = Shown(finals), finalToMs = finalTo }, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch (WebSocketException) { /* gone already */ }
        }
        else if (ws.State == WebSocketState.Open)
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch (WebSocketException) { }
    }

    /// <summary>Feeds the audio to the recogniser until the browser says it has finished (true) or goes away (false).</summary>
    private static async Task<bool> ReceiveAsync(WebSocket ws, ILiveRecognition rec, LiveSession session, int gen, CancellationTokenSource cts)
    {
        var buffer = new byte[64 * 1024];
        var text = new StringBuilder();
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    rec.Write(buffer.AsSpan(0, r.Count));
                    session.Heard(gen, r.Count);
                    continue;
                }
                text.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                if (!r.EndOfMessage) continue;
                var message = text.ToString(); text.Clear();
                if (message.Contains("\"end\""))
                {
                    await rec.CompleteAsync(cts.Token); // the last phrase is finished and sent before this returns
                    return true;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        await cts.CancelAsync();
        return false;
    }

    private static async Task FailAsync(WebSocket ws, LiveSession session, int gen, FailureReporter reporter, Exception ex)
    {
        var rep = await reporter.ReportAsync("live-speech", null, ex);
        session.Fail(gen, rep.Reference);
        try
        {
            await SendAsync(ws, new { type = "error", message = rep.UserMessage, reference = rep.Reference }, CancellationToken.None);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "failed", CancellationToken.None);
        }
        catch (WebSocketException) { }
    }

    private static Segment Shift(Segment p, int offset) => offset == 0 ? p : p with
    {
        StartMs = p.StartMs + offset, EndMs = p.EndMs + offset,
        Words = p.Words?.Select(w => w with { StartMs = w.StartMs + offset, EndMs = w.EndMs + offset }).ToList(),
    };

    private static List<Para> Shown(IEnumerable<Segment> segments) => segments.Select(s => new Para(s.Text, s.NewParagraph, s.List)).ToList();
    private static int Words(IEnumerable<Segment> segments) => segments.Sum(s => s.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    private static Task SendAsync(WebSocket ws, object message, CancellationToken ct) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, Web), WebSocketMessageType.Text, true, ct);
}
