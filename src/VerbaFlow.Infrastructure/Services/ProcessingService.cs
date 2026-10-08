using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Runs the batch pipeline on a finished recording: noise-filtered copy, then transcription with diarization and
/// language detection, then the AI outputs. Transcription uses the filtered copy; the original is never touched.
/// </summary>
public sealed class ProcessingService(Stores stores, ISpeechService speech, IAiOutputService ai, IAudioEnhancer enhancer,
    TimeProvider clock, ISpeakerDiarizer? diarizer = null)
{
    private static readonly string[] CandidateLanguages = ["en", "fr"];

    public async Task ProcessAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = await stores.RequireItemAsync(itemId);
        if (item.Processing != ProcessingState.Running || item.IsLocked) return;
        await stores.Audit.AppendAsync(null, "system", "speak", itemId, "processing.started",
            AuditChain.Details(("enhancer", enhancer.Name)));
        try
        {
            var asset = await stores.Media.FindOriginalAsync(item.MediaSourceItemId)
                ?? throw new InvalidOperationException("The original recording is missing.");
            await using var original = stores.Media.OpenRead(asset);
            var enhanced = await enhancer.EnhanceAsync(original, ct);
            await using var copy = enhanced.Audio;
            var phrases = (await stores.Vocabulary.ListAsync()).Select(v => v.Text).ToList();
            var result = await speech.TranscribeAsync(copy, new TranscribeOptions(CandidateLanguages, 8, true, phrases), ct);

            // Azure's own speaker labels can merge or split voices. When a local diarizer is available, its turns decide who spoke each word.
            var separation = "azure";
            if (diarizer is not null && copy.CanSeek)
            {
                try
                {
                    copy.Position = 0;
                    var turns = await diarizer.DiarizeAsync(copy, ct);
                    if (turns.Count > 0)
                    {
                        result = SpeakerAligner.Relabel(result, turns) with { Engine = result.Engine + "+" + diarizer.Name };
                        separation = diarizer.Name;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = result with { Warning = string.Join(" ", new[] { result.Warning, "Local speaker separation failed, so Azure's speaker labels were used: " + ex.Message }.Where(x => x is not null)) };
                }
            }

            var transcript = Transcript.CreateMachineV1(itemId, result.Engine, result.Speakers, result.Segments, clock.GetUtcNow());
            await stores.Transcripts.UpsertAsync(itemId, transcript);
            AiOutputs? outputs = null;
            try
            {
                outputs = await ai.GenerateAsync(transcript.Render(), item.OutputLanguage, ct);
                await stores.Outputs.UpsertAsync(itemId, new StoredOutputs(itemId, outputs));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The transcript is safe and the item still completes. The summary can be created later from the item page.
                await stores.Audit.AppendAsync(null, "system", "speak", itemId, "outputs.failed", AuditChain.Details(("reason", ex.Message)));
            }

            item = await stores.RequireItemAsync(itemId);
            if (item.LengthMs == 0 && result.Segments.Count > 0) item.SetLength(result.Segments.Max(s => s.EndMs));
            item.CompleteProcessing();
            await stores.Items.UpsertAsync(itemId, item);
            await stores.Audit.AppendAsync(null, "system", "speak", itemId, "processing.completed",
                AuditChain.Details(("speechEngine", result.Engine), ("aiEngine", outputs?.Engine), ("enhancer", enhancer.Name), ("audioPrep", enhanced.Applied), ("audioPrepWarning", enhanced.Warning),
                    ("segments", result.Segments.Count), ("speakers", result.Speakers.Count), ("speakerSeparation", separation),
                    ("vocabularyTerms", phrases.Count), ("warning", result.Warning)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            item = await stores.RequireItemAsync(itemId);
            item.FailProcessing(ex.Message);
            await stores.Items.UpsertAsync(itemId, item);
            await stores.Audit.AppendAsync(null, "system", "speak", itemId, "processing.failed",
                AuditChain.Details(("reason", ex.Message)));
        }
    }
}
