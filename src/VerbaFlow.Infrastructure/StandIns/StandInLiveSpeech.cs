using System.Threading.Channels;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Infrastructure.StandIns;

/// <summary>
/// STAND-IN live recogniser for Speech mode. It does not listen: it "hears" one placeholder word for every 400 ms of
/// audio written, in phrases of up to eight words, including spoken commands so the live page can be tried end to end.
/// After a lost connection it carries on with the word that belongs at that point of the recording.
/// Replace with Azure AI Speech real-time recognition (set Speech:Endpoint and Speech:Key).
/// </summary>
public sealed class StandInLiveSpeech : ILiveSpeech
{
    public const string EngineName = "stand-in-live-v0";
    public string Name => EngineName;
    public const int MsPerWord = 400;

    private static readonly string[] Script =
        ("stand-in live text comma the speech service is not connected full stop " +
         "each word stands for a little of what you said full stop new paragraph").Split(' ');

    public Task<ILiveRecognition> StartAsync(LiveOptions options, CancellationToken ct) =>
        Task.FromResult<ILiveRecognition>(new Recognition(options.Languages.FirstOrDefault()?.Split('-')[0].ToLowerInvariant() ?? "en",
            (int)Math.Round(options.FromMs / (double)MsPerWord)));

    private sealed class Recognition(string language, int firstWord) : ILiveRecognition
    {
        private readonly Channel<LiveEvent> _events = Channel.CreateUnbounded<LiveEvent>();
        private readonly List<WordTiming> _phrase = [];
        private readonly Guid _voice = Guid.NewGuid();
        private long _bytes;
        private int _words;
        private bool _closed;

        public ChannelReader<LiveEvent> Events => _events.Reader;

        public void Write(ReadOnlySpan<byte> pcm)
        {
            if (_closed) return;
            _bytes += pcm.Length;
            var heardMs = (int)(_bytes / 32); // 16 000 samples a second, two bytes each
            while ((_words + 1) * MsPerWord <= heardMs)
            {
                var start = _words * MsPerWord;
                _phrase.Add(new WordTiming(Script[(firstWord + _words) % Script.Length], start, start + MsPerWord - 50));
                _words++;
                if (_phrase.Count >= 8) Finish();
                else _events.Writer.TryWrite(new LiveEvent(LiveEventKind.Partial, string.Join(" ", _phrase.Select(w => w.Text)), _phrase[0].StartMs, _phrase[^1].EndMs));
            }
        }

        private void Finish()
        {
            if (_phrase.Count == 0) return;
            var text = string.Join(" ", _phrase.Select(w => w.Text));
            var seg = new Segment(Guid.NewGuid(), _voice, language, _phrase[0].StartMs, _phrase[^1].EndMs, text, 0.9, false, _phrase.ToList());
            _events.Writer.TryWrite(new LiveEvent(LiveEventKind.Final, text, seg.StartMs, seg.EndMs, seg));
            _phrase.Clear();
        }

        public Task CompleteAsync(CancellationToken ct)
        {
            if (!_closed) { _closed = true; Finish(); }
            _events.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() { _closed = true; _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
