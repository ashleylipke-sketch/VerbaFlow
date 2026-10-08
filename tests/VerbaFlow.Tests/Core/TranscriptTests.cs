using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Tests.Core;

public class TranscriptTests
{
    private static (Transcript t, Speaker s1, Speaker s2, Segment seg1) Make()
    {
        var s1 = new Speaker(Guid.NewGuid(), "Speaker 1"); var s2 = new Speaker(Guid.NewGuid(), "Speaker 2");
        var seg1 = new Segment(Guid.NewGuid(), s1.Id, "en", 0, 1000, "Hello everyone", 0.9, false);
        var seg2 = new Segment(Guid.NewGuid(), s2.Id, "fr", 1000, 2000, "Bonjour tout le monde", 0.8, true);
        return (Transcript.CreateMachineV1(Guid.NewGuid(), "test", [s1, s2], [seg2, seg1], T.Now), s1, s2, seg1);
    }

    [Fact]
    public void Version_1_is_the_machine_output_in_time_order()
    {
        var (t, _, _, _) = Make();
        Assert.Equal(VersionKind.MachineV1, t.Versions[0].Kind);
        Assert.Equal(["en", "fr"], t.Render().Select(r => r.Segment.Language));
    }

    [Fact]
    public void An_edit_adds_a_version_and_never_changes_the_machine_original()
    {
        var (t, _, _, seg1) = Make();
        var user = Guid.NewGuid();
        t.EditSegment(seg1.Id, "Hello, everyone.", user, "assignee", T.Now);
        Assert.Equal(2, t.Current.No);
        Assert.Equal("Hello everyone", t.Render(1)[0].Segment.Text);
        Assert.Equal("Hello, everyone.", t.Render()[0].Segment.Text);
        Assert.Equal("Hello everyone", t.Segments[seg1.Id].Text);
    }

    [Fact]
    public void An_edit_records_who_and_in_what_capacity()
    {
        var (t, _, _, seg1) = Make();
        var user = Guid.NewGuid();
        var v = t.EditSegment(seg1.Id, "Hi all", user, "importer", T.Now);
        Assert.Equal(user, v.CreatedBy);
        Assert.Equal("importer", v.Capacity);
    }

    [Fact]
    public void Renaming_a_speaker_is_a_version_and_applies_everywhere()
    {
        var (t, s1, _, _) = Make();
        t.RenameSpeaker(s1.Id, "Alice Smith", Guid.NewGuid(), "author", T.Now);
        Assert.Equal("Alice Smith", t.Render()[0].SpeakerName);
        Assert.Equal("Speaker 1", t.Render(1)[0].SpeakerName);
        Assert.Equal(VersionKind.SpeakerCorrection, t.Current.Kind);
    }

    [Fact]
    public void Restoring_an_old_version_creates_a_new_one()
    {
        var (t, _, _, seg1) = Make();
        t.EditSegment(seg1.Id, "Changed", Guid.NewGuid(), "author", T.Now);
        t.Restore(1, Guid.NewGuid(), "author", T.Now);
        Assert.Equal(3, t.Current.No);
        Assert.Equal("Hello everyone", t.Render()[0].Segment.Text);
    }

    [Fact]
    public void Empty_or_unchanged_edits_are_refused()
    {
        var (t, _, _, seg1) = Make();
        Assert.Throws<DomainException>(() => t.EditSegment(seg1.Id, "  ", Guid.NewGuid(), "author", T.Now));
        Assert.Throws<DomainException>(() => t.EditSegment(seg1.Id, "Hello everyone", Guid.NewGuid(), "author", T.Now));
    }

    [Fact]
    public void A_fork_for_a_reopened_item_keeps_the_whole_history_including_machine_v1()
    {
        var (t, _, _, seg1) = Make();
        t.EditSegment(seg1.Id, "Edited", Guid.NewGuid(), "author", T.Now);
        var fork = t.ForkFor(Guid.NewGuid(), Guid.NewGuid(), T.Now);
        Assert.Equal(VersionKind.MachineV1, fork.Versions[0].Kind);
        Assert.Equal(t.Versions.Count + 1, fork.Versions.Count);
        Assert.Equal(VersionKind.ReopenCopy, fork.Current.Kind);
        Assert.Equal("Edited", fork.Render()[0].Segment.Text);
        fork.EditSegment(fork.Current.SegmentIds[0], "Edited again", Guid.NewGuid(), "author", T.Now);
        Assert.Equal("Edited", t.Render()[0].Segment.Text); // the original transcript is unaffected
    }
}

public class WordTimingTests
{
    private static (VerbaFlow.Core.Transcripts.Transcript T, VerbaFlow.Core.Transcripts.Segment Seg) Make()
    {
        var sp = new VerbaFlow.Core.Transcripts.Speaker(Guid.NewGuid(), "Speaker 1");
        var seg = new VerbaFlow.Core.Transcripts.Segment(Guid.NewGuid(), sp.Id, "en", 0, 3000, "Mannchester United win", 0.9, false,
            [new("Mannchester", 0, 1000), new("United", 1000, 2000), new("win", 2000, 3000)]);
        return (VerbaFlow.Core.Transcripts.Transcript.CreateMachineV1(Guid.NewGuid(), "e", [sp], [seg], T.Now), seg);
    }

    [Fact]
    public void A_spelling_fix_keeps_every_words_timing()
    {
        var (t, seg) = Make();
        t.EditSegment(seg.Id, "Manchester United win", Guid.NewGuid(), "Owner", T.Now);
        var words = t.Render().Single().Segment.Words!;
        Assert.Equal(["Manchester", "United", "win"], words.Select(w => w.Text));
        Assert.Equal([0, 1000, 2000], words.Select(w => w.StartMs));
    }

    [Fact]
    public void Adding_or_removing_words_drops_the_timings_and_the_original_version_keeps_them()
    {
        var (t, seg) = Make();
        t.EditSegment(seg.Id, "Manchester United will win", Guid.NewGuid(), "Owner", T.Now);
        Assert.Null(t.Render().Single().Segment.Words);
        Assert.Equal(3, t.Render(1).Single().Segment.Words!.Count);
    }
}
