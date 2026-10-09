using System.Diagnostics;
using System.Security.Cryptography;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Readers.Tar;
using SherpaOnnx;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Services;

/// <param name="Threshold">Higher means fewer speakers. 0.8 found all four voices in the test recording. Used only when NumSpeakers is -1.</param>
/// <param name="NumSpeakers">The exact number of speakers if it is known, otherwise -1 to work it out.</param>
/// <param name="MinSpeakerSeconds">With the count left automatic, a voice group that spoke less than this in total is folded into the nearest real speaker. 0 switches this off.</param>
/// <param name="UseOriginalAudio">Tell voices apart from the untouched recording instead of the levelled processing copy.</param>
/// <param name="EmbeddingModel">Which voice-recognition model to use: titanet-small, wespeaker-resnet34, wespeaker-resnet34-lm or titanet-large.</param>
public sealed record DiarizationOptions(string ModelFolder, string? FfmpegPath, float Threshold = 0.8f, int NumSpeakers = -1, string EmbeddingModel = "titanet-small", bool UseOriginalAudio = false, float MinSpeakerSeconds = 8f);

/// <summary>
/// Finds who spoke when, locally, with no cloud service. Uses two small open models (Pyannote segmentation 3.0 and
/// NeMo TitaNet-small) run through sherpa-onnx. The models are downloaded once, checked against fixed checksums, and cached.
/// </summary>
public sealed class SherpaSpeakerDiarizer(DiarizationOptions options, HttpClient http) : ISpeakerDiarizer, IDisposable
{
    public string Name => "sherpa-onnx:pyannote-3.0+" + options.EmbeddingModel;

    private const string Release = "https://github.com/k2-fsa/sherpa-onnx/releases/download/";
    internal static readonly (string Url, string File, string Sha256) Segmentation = (
        Release + "speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2", "pyannote-segmentation-3-0.onnx",
        "24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488"); // checksum of the downloaded archive
    private const string EmbeddingRelease = Release + "speaker-recongition-models/"; // sic: the release tag is spelled this way
    /// <summary>The voice-recognition models that can be chosen. Each is checked against a fixed checksum after download.</summary>
    internal static readonly IReadOnlyDictionary<string, (string Url, string File, string Sha256)> EmbeddingModels =
        new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["titanet-small"] = (EmbeddingRelease + "nemo_en_titanet_small.onnx", "nemo_en_titanet_small.onnx",
                "ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e"),
            ["titanet-large"] = (EmbeddingRelease + "nemo_en_titanet_large.onnx", "nemo_en_titanet_large.onnx",
                "d51abcf31717ef28162f26acb9d44dd4127c3d44c9b8624f699f3425daca8e77"),
            ["wespeaker-resnet34"] = (EmbeddingRelease + "wespeaker_en_voxceleb_resnet34.onnx", "wespeaker_en_voxceleb_resnet34.onnx",
                "5ef208a9da1453335308a6b6f4e6dfbd7e183a38b604de0a57664f45d257fe94"),
            ["wespeaker-resnet34-lm"] = (EmbeddingRelease + "wespeaker_en_voxceleb_resnet34_LM.onnx", "wespeaker_en_voxceleb_resnet34_LM.onnx",
                "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012"),
        };
    internal static (string Url, string File, string Sha256) Embedding(string name) =>
        EmbeddingModels.TryGetValue(name, out var m) ? m
            : throw new InvalidOperationException($"Unknown speaker model '{name}'. Choose one of: {string.Join(", ", EmbeddingModels.Keys)}.");

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<int, OfflineSpeakerDiarization> engines = [];
    public void Dispose() { foreach (var e in engines.Values) e.Dispose(); gate.Dispose(); }

    /// <summary>Downloads the models if they are not already in the folder. Safe to call many times.</summary>
    public async Task EnsureModelsAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.ModelFolder);
        var seg = Path.Combine(options.ModelFolder, Segmentation.File);
        var embSpec = Embedding(options.EmbeddingModel);
        var emb = Path.Combine(options.ModelFolder, embSpec.File);
        if (File.Exists(seg) && File.Exists(emb)) return;

        if (!File.Exists(emb)) { await DownloadAsync(embSpec.Url, embSpec.Sha256, emb, ct); }
        if (!File.Exists(seg))
        {
            var archive = seg + ".tar.bz2";
            await DownloadAsync(Segmentation.Url, Segmentation.Sha256, archive, ct);
            try
            {
                var tarPath = archive + ".tar";
                await using (var raw = File.OpenRead(archive))
                await using (var bz = BZip2Stream.Create(raw, SharpCompress.Compressors.CompressionMode.Decompress, false, false, false))
                await using (var t = File.Create(tarPath)) await bz.CopyToAsync(t, ct);
                await using var tar = File.OpenRead(tarPath);
                using var reader = TarReader.OpenReader(tar);
                var found = false;
                while (reader.MoveToNextEntry())
                {
                    if (reader.Entry.IsDirectory || reader.Entry.Key is null || !reader.Entry.Key.EndsWith("/model.onnx", StringComparison.Ordinal)) continue;
                    await using (var o = File.Create(seg + ".part")) { using var src = reader.OpenEntryStream(); await src.CopyToAsync(o, ct); }
                    File.Move(seg + ".part", seg, true);
                    found = true; break;
                }
                if (!found) throw new InvalidOperationException("The speaker model archive did not contain model.onnx.");
            }
            finally { foreach (var f in new[] { archive, archive + ".tar" }) try { File.Delete(f); } catch { /* leave it */ } }
        }
    }

    private async Task DownloadAsync(string url, string sha256, string target, CancellationToken ct)
    {
        var part = target + ".part";
        using (var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            await using var o = File.Create(part);
            await res.Content.CopyToAsync(o, ct);
        }
        string hash;
        await using (var f = File.OpenRead(part)) hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
        if (hash != sha256) { File.Delete(part); throw new InvalidOperationException($"A downloaded speaker model failed its checksum ({Path.GetFileName(target)}). It was not used."); }
        File.Move(part, target, true);
    }

    public async Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(Stream audio, int? numSpeakers, CancellationToken ct)
    {
        await EnsureModelsAsync(ct);
        var samples = await DecodeAsync(audio, ct);
        if (samples.Length < 16000) return [];
        return await Task.Run(async () =>
        {
            await gate.WaitAsync(ct); // the native engine handles one recording at a time
            try
            {
                var count = numSpeakers ?? options.NumSpeakers; // -1 means work it out
                if (!engines.TryGetValue(count, out var engine)) engines[count] = engine = Build(count);
                return (IReadOnlyList<SpeakerTurn>)engine.Process(samples)
                    .Select(s => new SpeakerTurn((int)(s.Start * 1000), (int)(s.End * 1000), s.Speaker)).ToList();
            }
            finally { gate.Release(); }
        }, ct);
    }

    private OfflineSpeakerDiarization Build(int numSpeakers)
    {
        var cfg = new OfflineSpeakerDiarizationConfig();
        cfg.Segmentation.Pyannote.Model = Path.Combine(options.ModelFolder, Segmentation.File);
        cfg.Embedding.Model = Path.Combine(options.ModelFolder, Embedding(options.EmbeddingModel).File);
        cfg.Clustering.NumClusters = numSpeakers;
        cfg.Clustering.Threshold = options.Threshold;
        cfg.MinDurationOn = 0.2f;
        cfg.MinDurationOff = 0.5f;
        return new OfflineSpeakerDiarization(cfg);
    }

    /// <summary>ffmpeg turns whatever the processing copy is into 16 kHz mono samples.</summary>
    private async Task<float[]> DecodeAsync(Stream audio, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(options.FfmpegPath) ? "ffmpeg" : options.FfmpegPath)
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-i", "pipe:0", "-vn", "-ac", "1", "-ar", "16000", "-f", "f32le", "pipe:1" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var feed = Task.Run(async () =>
        {
            try { await audio.CopyToAsync(p.StandardInput.BaseStream, ct); } catch (IOException) { /* ffmpeg closed early */ }
            finally { try { p.StandardInput.Close(); } catch { /* closed */ } }
        }, ct);
        var err = p.StandardError.ReadToEndAsync(ct);
        var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        await p.WaitForExitAsync(ct);
        await feed;
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg could not read the audio for speaker separation. " + (await err).Trim());
        var bytes = ms.GetBuffer(); var samples = new float[ms.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return samples;
    }
}
