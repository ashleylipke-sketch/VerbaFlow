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
    TimeProvider clock, FailureReporter reporter, ISpeakerDiarizer? diarizer = null, DiarizationOptions? diarization = null)
{

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
            var notices = new List<string>(); // plain, customer-safe notes recorded in the history
            // The owner can ask for a run on the untouched recording, to compare it with the cleaned-up copy.
            var useOriginal = item.SpeechAudio == "original";
            var enhanced = useOriginal ? new EnhancedAudio(stores.Media.OpenRead(asset), "none (original recording chosen)")
                : await enhancer.EnhanceAsync(original, ct);
            if (enhanced.Warning is not null)
            {
                var rep = await reporter.ReportAsync("audio-preparation", itemId, new InvalidOperationException(enhanced.Warning));
                notices.Add($"audio clean-up was skipped ({rep.Reference})");
            }
            await using var copy = enhanced.Audio;
            var azureOnly = item.SpeakerMethod == "azure"; // Azure's own speaker labels, with the speaker limit it was given
            var phrases = (await stores.Vocabulary.ListAsync()).Select(v => v.Text).ToList();
            var result = await speech.TranscribeAsync(copy, new TranscribeOptions(item.SpokenLanguages == "auto" ? [] : item.SpokenLanguages.Split(','), azureOnly ? item.NumSpeakers ?? 8 : 8, true, phrases), ct);

            // Azure's own speaker labels can merge or split voices. When a local diarizer is available, its turns decide who spoke each word.
            var separation = "azure";
            int? localTurns = null, localSpeakers = null, mergedSmall = null; string? voiceSeconds = null;
            if (diarizer is not null && copy.CanSeek && !azureOnly)
            {
                try
                {
                    // Normally the levelled copy; with Diarization:Audio=original, the untouched recording.
                    await using var untouched = diarization?.UseOriginalAudio == true && !useOriginal ? stores.Media.OpenRead(asset) : null;
                    Stream heard = untouched ?? copy;
                    if (heard.CanSeek) heard.Position = 0;
                    var turns = await diarizer.DiarizeAsync(heard, item.NumSpeakers, item.SpeakerMethod, ct);
                    localTurns = turns.Count; localSpeakers = turns.Select(t => t.Speaker).Distinct().Count();
                    voiceSeconds = string.Join(" ", turns.GroupBy(t => t.Speaker).OrderByDescending(g => g.Sum(t => t.EndMs - t.StartMs))
                        .Select(g => $"{g.Sum(t => t.EndMs - t.StartMs) / 1000}s"));
                    if (item.NumSpeakers is null && diarization is not null) // a count the owner gave is trusted as it is
                    {
                        var cleaned = SpeakerTurnCleaner.AbsorbSmallVoices(turns, (int)(diarization.MinSpeakerSeconds * 1000));
                        turns = cleaned.Turns; mergedSmall = cleaned.Merged;
                    }
                    if (turns.Count > 0)
                    {
                        result = SpeakerAligner.Relabel(result, turns) with { Engine = result.Engine + "+" + diarizer.Name };
                        separation = diarizer.Name;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var rep = await reporter.ReportAsync("speaker-separation", itemId, ex);
                    notices.Add($"speaker separation fell back to the speech service's own labels ({rep.Reference})");
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
                var rep = await reporter.ReportAsync("summary", itemId, ex);
                await stores.Audit.AppendAsync(null, "system", "speak", itemId, "outputs.failed", AuditChain.Details(("kind", rep.Kind.ToString()), ("reference", rep.Reference)));
            }

            item = await stores.RequireItemAsync(itemId);
            if (item.LengthMs == 0 && result.Segments.Count > 0) item.SetLength(result.Segments.Max(s => s.EndMs));
            item.CompleteProcessing();
            await stores.Items.UpsertAsync(itemId, item);
            await stores.Audit.AppendAsync(null, "system", "speak", itemId, "processing.completed",
                AuditChain.Details(("speechEngine", result.Engine), ("aiEngine", outputs?.Engine), ("enhancer", enhancer.Name), ("audioPrep", enhanced.Applied),
                    ("segments", result.Segments.Count), ("speakers", result.Speakers.Count), ("speakerSeparation", separation),
                    ("localSpeakersFound", localSpeakers), ("localVoiceTurns", localTurns), ("localVoiceSeconds", voiceSeconds), ("smallVoicesFolded", mergedSmall), ("audioSentToSpeech", useOriginal ? "original" : "prepared"), ("voicesHeardFrom", localTurns is null ? null : useOriginal || diarization?.UseOriginalAudio == true ? "original" : "prepared"), ("speakersToldTo", item.NumSpeakers), ("groupingMethod", azureOnly ? "azure" : localTurns is null ? null : item.SpeakerMethod ?? diarization?.Method ?? "standard"),
                    ("languagesSent", item.SpokenLanguages), ("perLanguage", result.Detail), ("languagesHeard", string.Join(",", result.Segments.Select(s => s.Language).Distinct())),
                    ("vocabularyTerms", phrases.Count), ("notices", string.Join("; ", result.Warning is null ? notices : [.. notices, "the custom vocabulary was not applied"]))));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            item = await stores.RequireItemAsync(itemId);
            var rep = await reporter.ReportAsync("transcription", itemId, ex);
            item.FailProcessing(rep.UserMessage);
            await stores.Items.UpsertAsync(itemId, item);
            await stores.Audit.AppendAsync(null, "system", "speak", itemId, "processing.failed",
                AuditChain.Details(("kind", rep.Kind.ToString()), ("reference", rep.Reference)));
        }
    }
}
