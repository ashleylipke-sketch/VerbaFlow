using System.IO.Compression;
using System.Text;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

public class AttachmentTests
{
    private static AttachmentService Service(Env env) => new(env.Stores, new StandInMalwareScanner(), env.Clock);

    private static AttachmentUpload Pdf(string name = "Agenda.pdf") => new(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4\n% agenda\n")), name);

    private static AttachmentUpload Docx(string name = "Papers.docx", bool macros = false)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open())) w.Write("<w:document/>");
            if (macros) using (var w = new StreamWriter(zip.CreateEntry("word/vbaProject.bin").Open())) w.Write("code");
        }
        ms.Position = 0;
        return new(ms, name);
    }

    private static string[] StoredFiles(Env env) =>
        Directory.GetFiles(env.Dir, "*.bin", SearchOption.AllDirectories).Where(f => !f.Contains(".quarantine")).ToArray();

    [Fact]
    public async Task The_author_adds_files_everyone_who_can_open_it_downloads_and_the_history_records_it()
    {
        using var env = new Env();
        var s = Service(env);
        var id = await env.ProcessedRecordingAsync();
        var list = await s.AddAsync(env.Alice, id, [Pdf(), Docx()]);
        Assert.Equal(["Agenda.pdf", "Papers.docx"], list.Files.Select(f => f.FileName));
        Assert.True(list.CanAdd);
        Assert.Equal("application/pdf", list.Files[0].ContentType);

        await Assert.ThrowsAsync<ForbiddenException>(() => s.ListAsync(env.Bob, id)); // not his
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        var bob = await s.ListAsync(env.Bob, id);
        Assert.False(bob.CanAdd); // assigned but not yet accepted
        await using (var f = (await s.OpenAsync(env.Bob, id, list.Files[0].Id)).Content)
            Assert.StartsWith("%PDF", new StreamReader(f).ReadToEnd());
        var types = (await env.Meeting.GetAuditAsync(env.Alice, id)).Select(e => e.Type).ToList();
        Assert.Equal(2, types.Count(t => t == "attachment.added"));
        Assert.Contains("attachment.downloaded", types);
        Assert.True((await env.Stores.Audit.VerifyAsync()).Ok);
    }

    [Fact]
    public async Task Programs_macros_and_files_that_are_not_what_their_name_says_are_refused()
    {
        using var env = new Env();
        var s = Service(env);
        var id = await env.ProcessedRecordingAsync();
        await Assert.ThrowsAsync<DomainException>(() => s.AddAsync(env.Alice, id, [new(new MemoryStream([1, 2]), "setup.exe")]));
        await Assert.ThrowsAsync<DomainException>(() => s.AddAsync(env.Alice, id, [Docx("Budget.docm")]));
        var macro = await Assert.ThrowsAsync<DomainException>(() => s.AddAsync(env.Alice, id, [Docx(macros: true)]));
        Assert.Contains("macros", macro.Message);
        var fake = await Assert.ThrowsAsync<DomainException>(() => s.AddAsync(env.Alice, id, [new(new MemoryStream([0x4D, 0x5A, 0x90, 0]), "Agenda.pdf")]));
        Assert.Contains("not the kind of file", fake.Message);
        await Assert.ThrowsAsync<DomainException>(() => s.AddAsync(env.Alice, id, [new(new MemoryStream(), "empty.txt")]));
        Assert.Empty((await s.ListAsync(env.Alice, id)).Files);
        Assert.Single(StoredFiles(env)); // only the recording: nothing refused was stored
    }

    [Fact]
    public async Task Approval_locks_the_files_and_a_reopened_version_keeps_them()
    {
        using var env = new Env();
        var s = Service(env);
        var id = await env.ProcessedRecordingAsync();
        var added = await s.AddAsync(env.Alice, id, [Pdf()]);
        await env.Meeting.ApproveAsync(env.Alice, id);
        var locked = await s.ListAsync(env.Alice, id);
        Assert.False(locked.CanAdd);
        Assert.Contains("approved", locked.Why);
        await Assert.ThrowsAsync<DomainException>(() => s.AddAsync(env.Alice, id, [Docx()]));
        await Assert.ThrowsAsync<DomainException>(() => s.RemoveAsync(env.Alice, id, added.Files[0].Id));

        var req = await env.Meeting.RequestReopenAsync(env.Alice, id, "Add the papers");
        await env.Meeting.ApproveReopenAsync(env.Carol, req);
        var next = (await env.Meeting.ApproveReopenAsync(env.Dave, req))!.Value;
        Assert.Single((await s.ListAsync(env.Alice, next)).Files);
        await Assert.ThrowsAsync<DomainException>(() => s.AddAsync(env.Alice, id, [Docx()])); // the old version stays locked
        var after = await s.RemoveAsync(env.Alice, next, added.Files[0].Id);
        Assert.Empty(after.Files);
        Assert.Contains("attachment.removed", (await env.Meeting.GetAuditAsync(env.Alice, next)).Select(e => e.Type));
    }

    [Fact]
    public async Task Deleting_the_recording_deletes_its_files_but_removing_only_its_sound_keeps_them()
    {
        using var env = new Env();
        var s = Service(env);
        var id = await env.ProcessedRecordingAsync();
        await s.AddAsync(env.Alice, id, [Pdf(), Docx()]);
        var before = StoredFiles(env).Length;
        await env.Meeting.DeleteAsync(env.Alice, id, "Not needed");
        Assert.Empty(StoredFiles(env));
        Assert.Empty(await env.Stores.Attachments.ListAsync());
        Assert.Contains("attachedFilesRemoved", (await env.Meeting.GetAuditAsync(env.Alice, id))[^1].Details);
        Assert.Equal(3, before);

        var r = new RetentionService(env.Stores, env.Meeting, env.Clock, env.Reporter);
        var other = await env.ProcessedRecordingAsync();
        await s.AddAsync(env.Alice, other, [Pdf()]);
        await env.Meeting.ApproveAsync(env.Alice, other);
        await r.ProposeAsync(env.Carol, new(new(RemoveRecordingAfterDays: 1), null, null));
        await r.AgreeAsync(env.Dave);
        env.Clock.Now += TimeSpan.FromDays(8);
        Assert.Equal(1, await r.ApplyAsync());
        var kept = (await s.ListAsync(env.Alice, other)).Files.Single();
        await using var f = (await s.OpenAsync(env.Alice, other, kept.Id)).Content;
        Assert.True(f.Length > 0);
    }

    [Fact]
    public async Task At_most_twenty_files()
    {
        using var env = new Env();
        var s = Service(env);
        var id = await env.ProcessedRecordingAsync();
        await s.AddAsync(env.Alice, id, Enumerable.Range(1, 20).Select(i => Pdf($"Paper {i}.pdf")).ToList());
        await Assert.ThrowsAsync<DomainException>(() => s.AddAsync(env.Alice, id, [Pdf()]));
    }
}
