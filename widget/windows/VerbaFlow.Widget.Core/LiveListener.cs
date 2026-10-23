using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VerbaFlow.Widget;

/// <summary>The live text so far: passages with commands applied, how many words at the end are still being heard.</summary>
public sealed record LiveText(IReadOnlyList<LivePara> Segments, int PendingWords, bool StartsWithBreak);
/// <summary>How a listen ended: whether all the words came back, how much was spoken, and how often the connection was made again.</summary>
public sealed record ListenResult(bool Complete, int SpokenMs, int Reconnects);

/// <summary>
/// One listen: the microphone's sound goes to VerbaFlow over a live connection (16 kHz, 16-bit mono, as Speech mode and
/// the browser Widget send it) and the text comes back. A lost connection is made again (after 1, 2, 4, 8, then every
/// 10 seconds) and the sound since the end of the last finished phrase is sent again, so nothing is missed or doubled.
/// The sound is held only in memory while listening and is dropped when the listen ends.
/// </summary>
public sealed class LiveListener : IAsyncDisposable
{
    public const int BytesPerMs = 32; // 16 000 samples a second, two bytes each
    private static readonly byte[] EndMessage = Encoding.UTF8.GetBytes("{\"type\":\"end\"}");

    private readonly VerbaFlowClient _client;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connect;
    private readonly Guid _liveId;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<ListenResult> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly List<byte[]> _audio = [];
    private string? _ticket;
    private long _bytes;
    private int _finalToMs, _connections, _reconnects;
    private Channel<byte[]?>? _outbox; // null message = the end message
    private bool _ready, _stopping, _finished, _failed;
    private Task? _run;
    private Timer? _giveUp;

    /// <summary>New text (on a background thread).</summary>
    public event Action<LiveText>? Text;
    /// <summary>The connection was lost (false) or is live again (true). Listening carries on either way.</summary>
    public event Action<bool>? Link;
    /// <summary>The live service failed: a plain sentence with a VF- reference. The words heard so far stay.</summary>
    public event Action<string>? Failed;

    /// <param name="connect">Opens a WebSocket; a ClientWebSocket normally, the test server's in tests.</param>
    public LiveListener(VerbaFlowClient client, WidgetStart start, Func<Uri, CancellationToken, Task<WebSocket>>? connect = null)
    {
        _client = client;
        _liveId = start.LiveId;
        _ticket = start.Ticket;
        _connect = connect ?? (async (uri, ct) => { var ws = new ClientWebSocket(); await ws.ConnectAsync(uri, ct); return ws; });
    }

    /// <summary>Finishes when the listen has ended: after <see cref="Stop"/>, or when the live service failed after it.</summary>
    public Task<ListenResult> Stopped => _stopped.Task;
    public int SpokenMs { get { lock (_gate) return (int)(_bytes / BytesPerMs); } }
    public int Reconnects => _reconnects;

    public void Start() => _run ??= Task.Run(RunAsync);

    /// <summary>Sound from the microphone, 16 kHz 16-bit mono. Called on the microphone's thread.</summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length == 0) return;
        lock (_gate)
        {
            if (_stopping || _finished) return;
            var copy = pcm.ToArray();
            _audio.Add(copy);
            _bytes += copy.Length;
            if (_ready) _outbox?.Writer.TryWrite(copy);
        }
    }

    /// <summary>The person stopped speaking: the last words are finished and sent back, then <see cref="Stopped"/> completes.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            if (_finished || _failed) { Report(_finished); return; }
            if (_ready) _outbox?.Writer.TryWrite(null);
        }
        // Without a connection the last words cannot come back; the words heard so far are kept.
        _giveUp = new Timer(_ => Report(false), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
    }

    private void Report(bool complete)
    {
        if (_stopped.TrySetResult(new ListenResult(complete, SpokenMs, _reconnects))) _cts.Cancel();
    }

    private async Task RunAsync()
    {
        var attempt = 0;
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ticket = _ticket ?? await _client.TicketAsync(_liveId, ct);
                _ticket = null;
                int fromMs;
                lock (_gate) fromMs = _finalToMs;
                using var ws = await _connect(_client.SocketUri(_liveId, ticket, fromMs), ct);
                if (_connections++ > 0) _reconnects++;
                if (await ConnectionAsync(ws, fromMs, ct)) { attempt = 0; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception) { /* not reachable now: tried again below */ }
            lock (_gate)
            {
                _ready = false; _outbox = null;
                if (_finished || _failed) break;
            }
            if (ct.IsCancellationRequested) break;
            Link?.Invoke(false);
            try { await Task.Delay(Math.Min(10_000, 1000 * (1 << Math.Min(attempt++, 4))), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One connection, until it closes. True when it got as far as ready.</summary>
    private async Task<bool> ConnectionAsync(WebSocket ws, int fromMs, CancellationToken ct)
    {
        var outbox = Channel.CreateUnbounded<byte[]?>(new UnboundedChannelOptions { SingleReader = true });
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sending = SendAsync(ws, outbox.Reader, connection.Token);
        var wasReady = false;
        try
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, connection.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                using var doc = JsonDocument.Parse(message.ToArray());
                message.SetLength(0);
                var m = doc.RootElement;
                switch (m.GetProperty("type").GetString())
                {
                    case "ready":
                        wasReady = true;
                        Link?.Invoke(true);
                        lock (_gate)
                        {
                            // The sound since the end of the last finished phrase goes again, in pieces of a second.
                            foreach (var piece in From(fromMs * (long)BytesPerMs)) outbox.Writer.TryWrite(piece);
                            _outbox = outbox; _ready = true;
                            if (_stopping) outbox.Writer.TryWrite(null);
                        }
                        break;
                    case "text":
                        lock (_gate) _finalToMs = FinalTo(m) ?? _finalToMs;
                        Text?.Invoke(TextOf(m));
                        break;
                    case "done":
                        lock (_gate) { _finished = true; _finalToMs = FinalTo(m) ?? _finalToMs; }
                        Text?.Invoke(TextOf(m));
                        Report(true);
                        return wasReady;
                    case "error":
                        lock (_gate) _failed = true;
                        Failed?.Invoke(m.TryGetProperty("message", out var e) ? e.GetString() ?? "" : "");
                        if (_stopping) Report(false);
                        return wasReady;
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        finally
        {
            lock (_gate) { if (_outbox == outbox) { _outbox = null; _ready = false; } }
            outbox.Writer.TryComplete();
            await connection.CancelAsync();
            try { await sending; } catch { /* closed */ }
        }
        return wasReady;
    }

    private static async Task SendAsync(WebSocket ws, ChannelReader<byte[]?> outbox, CancellationToken ct)
    {
        await foreach (var piece in outbox.ReadAllAsync(ct))
        {
            if (piece is null) await ws.SendAsync(EndMessage, WebSocketMessageType.Text, true, ct);
            else await ws.SendAsync(piece, WebSocketMessageType.Binary, true, ct);
        }
    }

    /// <summary>The sound held from a byte position on, in pieces of at most one second. Called holding the gate.</summary>
    private IEnumerable<byte[]> From(long start)
    {
        long at = 0;
        var second = 1000 * BytesPerMs;
        foreach (var chunk in _audio)
        {
            var from = Math.Max(0, start - at);
            at += chunk.Length;
            if (from >= chunk.Length) continue;
            for (var i = (int)from; i < chunk.Length; i += second)
                yield return chunk.AsSpan(i, Math.Min(second, chunk.Length - i)).ToArray();
        }
    }

    private static int? FinalTo(JsonElement m) => m.TryGetProperty("finalToMs", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : null;

    private static LiveText TextOf(JsonElement m)
    {
        var segs = new List<LivePara>();
        if (m.TryGetProperty("segments", out var s) && s.ValueKind == JsonValueKind.Array)
            foreach (var p in s.EnumerateArray())
                segs.Add(new LivePara(
                    p.TryGetProperty("text", out var t) ? t.GetString() : null,
                    p.TryGetProperty("newParagraph", out var n) && n.ValueKind is JsonValueKind.True or JsonValueKind.False ? n.GetBoolean() : null,
                    p.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null));
        var pending = m.TryGetProperty("pendingWords", out var pw) && pw.ValueKind == JsonValueKind.Number ? pw.GetInt32() : 0;
        var lead = m.TryGetProperty("startsWithBreak", out var b) && b.ValueKind == JsonValueKind.True;
        return new LiveText(segs, pending, lead);
    }

    /// <summary>Ends the listen at once and drops the sound held.</summary>
    public async ValueTask DisposeAsync()
    {
        Report(false);
        _giveUp?.Dispose();
        if (_run is not null) { try { await _run; } catch { /* ended */ } }
        lock (_gate) { _audio.Clear(); _outbox = null; }
        _cts.Dispose();
    }
}
