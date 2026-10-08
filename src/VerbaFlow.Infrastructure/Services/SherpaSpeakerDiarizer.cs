using System.Diagnostics;
using System.Security.Cryptography;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Readers.Tar;
using SherpaOnnx;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Services;

/// <param name="Threshold">Higher means fewer speakers. 0.8 found all four voices in the test recording. Used only when NumSpeakers is -1.</param>
/// <param name="NumSpeakers">The exact number of speakers if it is known, otherwise -1 to work it out.</param>
public sealed record DiarizationOptions(string ModelFolder, string? FfmpegPath, float Threshold = 0.8f, int NumSpeakers = -1);

/// <summary>
/// Finds who spoke when, locally, with no cloud service. Uses two small open models (Pyannote segmentation 3.0 and
/// NeMo TitaNet-small) run through sherpa-onnx. The models are downloaded once, checked against fixed checksums, and cached.
/// </summary>
public sealed class SherpaSpeakerDiarizer(DiarizationOptions options, HttpClient http) : ISpeakerDiarizer, IDisposable
{
    public string Name => "sherpa-onnx:pyannote-3.0+titanet-small";

    private const string Release = "https://github.com/k2-fsa/sherpa-onnx/releases/download/";
    internal static readonly (string Url, string File, string Sha256) Segmentation = (
        Release + "speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2", "pyannote-segmentation-3-0.onnx",
        "24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488"); // checksum of the downloaded archive
    internal static readonly (string Url, string File, string Sha256) Embedding = (
        Release + "speaker-recongition-models/nemo_en_titanet_small.onnx", "nemo_en_titanet_small.onnx", // sic: the release tag is spelled this way
        "ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e");

    private readonly SemaphoreSlim gate = new(1, 1);
    private OfflineSpeakerDiarization? engine;
    public void Dispose() { engine?.Dispose(); gate.Dispose(); }

    /// <summary>Downloads the models if they are not already in the folder. Safe to call many times.</summary>
    public async Task EnsureModelsAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.ModelFolder);
        var seg = Path.Combine(options.ModelFolder, Segmentation.File);
        var emb = Path.Combine(options.ModelFolder, Embedding.File);
        if (File.Exists(seg) && File.Exists(emb)) return;

        if (!File.Exists(emb)) { await DownloadAsync(Embedding.Url, Embedding.Sha256, emb, ct); }
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

    public async Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(Stream audio, CancellationToken ct)
    {
        await EnsureModelsAsync(ct);
        var samples = await DecodeAsync(audio, ct);
        if (samples.Length < 16000) return [];
        return await Task.Run(async () =>
        {
            await gate.WaitAsync(ct); // the native engine handles one recording at a time
            try
            {
                engine ??= Build();
                return (IReadOnlyList<SpeakerTurn>)engine.Process(samples)
                    .Select(s => new SpeakerTurn((int)(s.Start * 1000), (int)(s.End * 1000), s.Speaker)).ToList();
            }
            finally { gate.Release(); }
        }, ct);
    }

    private OfflineSpeakerDiarization Build()
    {
        var cfg = new OfflineSpeakerDiarizationConfig();
        cfg.Segmentation.Pyannote.Model = Path.Combine(options.ModelFolder, Segmentation.File);
        cfg.Embedding.Model = Path.Combine(options.ModelFolder, Embedding.File);
        cfg.Clustering.NumClusters = options.NumSpeakers;
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
