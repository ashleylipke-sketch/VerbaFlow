using System.Security.Cryptography;
using System.Text;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class CaptureTests
{
    [Fact]
    public async Task A_recording_is_stored_hashed_and_sits_with_the_author_while_processing()
    {
        using var env = new Env();
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording());
        var item = await env.Item(id);
        Assert.Equal(ItemStatus.WithAuthor, item.Status);
        Assert.Equal(ProcessingState.Running, item.Processing);
        var asset = await env.Stores.Media.FindOriginalAsync(id);
        Assert.NotNull(asset);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pretend this is audio"))).ToLowerInvariant(), asset!.Sha256);
        Assert.True(await env.Stores.Media.VerifyAsync(asset));
    }

    [Fact]
    public async Task Processing_makes_machine_version_1_the_outputs_and_clears_the_progress_flag()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var item = await env.Item(id);
        Assert.Equal(ProcessingState.Idle, item.Processing);
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.Equal(1, t.VersionNo);
        Assert.Equal("MachineV1", t.Versions[0].Kind);
        Assert.NotEmpty(t.Segments);
        Assert.Contains(t.Segments, s => s.Language == "fr");     // language is tagged per passage
        Assert.True(t.Speakers.Count >= 2);                       // diarization labels
        Assert.NotNull(await env.Meeting.GetOutputsAsync(env.Alice, id));
    }

    [Fact]
    public async Task Saving_needs_confirmation_that_the_notice_was_given()
    {
        using var env = new Env();
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.CreateRecordingAsync(env.Alice, env.Recording(consent: false)));
    }

    [Fact]
    public async Task Objections_are_logged_and_flag_the_item_but_the_recording_is_kept_in_full()
    {
        using var env = new Env();
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording(markers:
            [new MarkerInput(MarkerType.Objection, 20_000, "Attendee objected"), new MarkerInput(MarkerType.Pause, 30_000, "Comfort break"),
             new MarkerInput(MarkerType.Resume, 45_000, null)]));
        var item = await env.Item(id);
        Assert.True(item.ObjectionFlag);
        Assert.Equal(60_000, item.LengthMs);
        Assert.Contains(item.Markers, m => m.Type == MarkerType.ConsentNotice);
        var audit = await env.Meeting.GetAuditAsync(env.Alice, id);
        Assert.Contains(audit, e => e.Type == "recording.objection_logged");
        Assert.Contains(audit, e => e.Type == "recording.paused");
    }

    [Fact]
    public async Task A_pause_without_a_reason_is_refused()
    {
        using var env = new Env();
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.CreateRecordingAsync(env.Alice,
            env.Recording(markers: [new MarkerInput(MarkerType.Pause, 1000, null)])));
    }

    [Fact]
    public async Task An_import_lands_in_Imported_with_external_provenance_and_returns_there()
    {
        using var env = new Env();
        var id = await env.ProcessedImportAsync();
        var item = await env.Item(id);
        Assert.Equal(ItemStatus.WithImporter, item.Status);
        var detail = await env.Meeting.GetItemAsync(env.Alice, id);
        Assert.Equal("Imported", detail.Row.Status);
        Assert.Equal("Imported (external source)", detail.Provenance);
        Assert.Equal("Importer", detail.OwnerRole);
        Assert.Contains("Imported", detail.Row.Tags);

        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        await env.Meeting.AcceptAsync(env.Bob, id);
        await env.Meeting.ReturnAsync(env.Bob, id);
        Assert.Equal(ItemStatus.WithImporter, (await env.Item(id)).Status);
    }

    [Fact]
    public async Task An_import_needs_the_rights_confirmation_and_must_be_audio_or_video()
    {
        using var env = new Env();
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ImportAsync(env.Alice, env.Import(rights: false)));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ImportAsync(env.Alice,
            new ImportUpload(Env.Audio(), "notes.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", null, 0, "en", true, null)));
    }

    [Fact]
    public async Task An_infected_upload_is_rejected_is_not_stored_and_is_logged()
    {
        using var env = new Env();
        var eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ImportAsync(env.Alice, env.Import(content: "header " + eicar)));
        Assert.Empty(await env.Stores.Items.ListAsync());
        Assert.Empty(await env.Stores.MediaAssets.ListAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(env.Dir, "media", ".quarantine")));
        var events = await env.Stores.Audit.ListAsync();
        Assert.Contains(events, e => e.Type == "upload.rejected" && e.Details.Contains("Rejected"));
    }

    [Fact]
    public async Task An_unscannable_empty_upload_is_blocked()
    {
        using var env = new Env();
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ImportAsync(env.Alice, env.Import(content: "")));
        Assert.Empty(await env.Stores.Items.ListAsync());
    }

    [Fact]
    public async Task Uploads_over_the_company_size_limit_are_refused()
    {
        using var env = new Env(new PlatformPolicy(MaxUploadBytes: 10));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.CreateRecordingAsync(env.Alice, env.Recording()));
        Assert.Empty(await env.Stores.Items.ListAsync());
    }

    [Fact]
    public async Task A_failed_conversion_goes_to_Conversion_Failed_and_a_retry_recovers_it()
    {
        using var env = new Env();
        env.Speech.Fail = true;
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording());
        await env.Processing.ProcessAsync(id);
        var failed = await env.Item(id);
        Assert.Equal(ItemStatus.ConversionFailed, failed.Status);
        Assert.Contains("Reference: VF-", failed.FailureReason);
        Assert.DoesNotContain("unavailable", failed.FailureReason);
        Assert.NotNull(await env.Stores.Media.FindOriginalAsync(id));   // the original is never lost

        env.Speech.Fail = false;
        await env.Meeting.RetryAsync(env.Alice, id);
        await env.Processing.ProcessAsync(id);
        Assert.Equal(ItemStatus.WithAuthor, (await env.Item(id)).Status);
        Assert.Equal(ProcessingState.Idle, (await env.Item(id)).Processing);
    }

    [Fact]
    public async Task Tampering_with_the_stored_recording_is_detected_by_its_hash()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var asset = (await env.Stores.Media.FindOriginalAsync(id))!;
        var path = Directory.GetFiles(Path.Combine(env.Dir, "media", id.ToString("N"))).Single();
        File.SetAttributes(path, FileAttributes.Normal);
        File.AppendAllText(path, "x");
        Assert.False(await env.Stores.Media.VerifyAsync(asset));
    }
}
