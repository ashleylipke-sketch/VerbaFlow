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
    TimeProvider clock, FailureReporter reporter, ISpeakerDiarizer? diarizer = null, DiarizationOptions? diarization = null,
    IDictationSpeech? wordForWord = null)
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
            // Azure's own speaker labels, with the speaker limit it was given. "azure-split" was a short-lived trial (removed); items that still carry it run as "azure".
            var azureOnly = item.SpeakerMethod is "azure" or "azure-split";
            // A dictation is one person: no speaker separation, and spoken commands ("comma", "new paragraph") are applied.
            var dictation = item.Mode == ItemMode.Dictate;
            var phrases = (await stores.Vocabulary.ListAsync()).Select(v => v.Text).ToList();
            var request = new TranscribeOptions(item.SpokenLanguages == "auto" ? [] : item.SpokenLanguages.Split(','),
                azureOnly ? item.NumSpeakers ?? 8 : 8, !dictation, phrases);
            // A dictation in one language goes word for word, so spoken commands reach VerbaFlow; the fast service is the fallback.
            string? dictationService = null;
            TranscriptionResult? result = null;
            if (dictation && wordForWord is not null && item.DictationService != "fast" && request.CandidateLanguages.Length == 1)
            {
                try { result = await wordForWord.TranscribeAsync(copy, request, ct); dictationService = "word for word"; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var rep = await reporter.ReportAsync("dictation-speech", itemId, ex);
                    notices.Add($"the word-for-word speech service could not be used, so the fast one was ({rep.Reference})");
                    dictationService = "fast (word for word failed)";
                    if (copy.CanSeek) copy.Position = 0;
                }
            }
            else if (dictation) dictationService = wordForWord is null ? "fast (word for word not available)" : request.CandidateLanguages.Length != 1 ? "fast (more than one language)" : "fast (chosen)";
            result ??= await speech.TranscribeAsync(copy, request, ct);
            int? commands = null; string? commandsHeard = null, paragraphsMade = null;
            if (dictation)
            {
                var (segments, applied, heard) = DictationCommands.ApplyCounted(result.Segments);
                commands = applied;
                commandsHeard = heard.Count == 0 ? "none" : string.Join(", ", heard.Select(h => $"{h.Key} {h.Value}"));
                // Counts only, never words: how the speech service's passages became paragraphs, to see where a break went missing.
                var made = segments.Count(x => x.NewParagraph == true);
                paragraphsMade = $"{result.Segments.Count} passage{(result.Segments.Count == 1 ? "" : "s")} from the speech service, {made} paragraph{(made == 1 ? "" : "s")}";
                // The one voice is the person who dictated.
                var author = (await stores.Users.GetAsync(item.OwnerId))?.Name ?? "Author";
                var voice = new Speaker(Guid.NewGuid(), author);
                result = result with { Speakers = [voice], Segments = segments.Select(x => x with { SpeakerId = voice.Id }).ToList() };
            }

            // Azure's own speaker labels can merge or split voices. When a local diarizer is available, its turns decide who spoke each word.
            var separation = "azure";
            int? localTurns = null, localSpeakers = null, mergedSmall = null; string? voiceSeconds = null;
            if (diarizer is not null && copy.CanSeek && !azureOnly && !dictation)
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
            int? stumbles = null;
            if (dictation)
            {
                // Sentences the speaker abandoned and started again are crossed out, visibly, as the app's own correction.
                var tidied = new Dictionary<Guid, Segment>();
                stumbles = 0;
                foreach (var s in result.Segments)
                    if (RestartedSentences.Remove(s) is { } r) { tidied[s.Id] = r.Segment; stumbles += r.Removed; }
                transcript.AddMachineTidy(tidied, clock.GetUtcNow());
            }
            await stores.Transcripts.UpsertAsync(itemId, transcript);
            AiOutputs? outputs = null;
            if (!dictation) try
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
                    ("localSpeakersFound", localSpeakers), ("localVoiceTurns", localTurns), ("localVoiceSeconds", voiceSeconds), ("smallVoicesFolded", mergedSmall), ("audioSentToSpeech", useOriginal ? "original" : "prepared"), ("voicesHeardFrom", localTurns is null ? null : useOriginal || diarization?.UseOriginalAudio == true ? "original" : "prepared"), ("speakersToldTo", item.NumSpeakers), ("groupingMethod", dictation ? null : azureOnly ? "azure" : localTurns is null ? null : item.SpeakerMethod ?? diarization?.Method ?? "standard"),
                    ("languagesSent", item.SpokenLanguages), ("perLanguage", result.Detail), ("languagesHeard", string.Join(",", result.Segments.Select(s => s.Language).Distinct())),
                    ("vocabularyTerms", phrases.Count), ("dictationCommands", commands), ("speechServiceForDictation", dictationService), ("commandsHeard", commandsHeard), ("paragraphs", paragraphsMade), ("restartedSentencesCrossedOut", stumbles), ("notices", string.Join("; ", result.Warning is null ? notices : [.. notices, "the custom vocabulary was not applied"]))));
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
