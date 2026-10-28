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
    public void Unchanged_edits_are_refused()
    {
        var (t, _, _, seg1) = Make();
        Assert.Throws<DomainException>(() => t.EditSegment(seg1.Id, "Hello everyone", Guid.NewGuid(), "author", T.Now));
    }

    [Fact]
    public void A_passage_can_be_emptied_and_restored()
    {
        var (t, _, _, seg1) = Make();
        t.EditSegment(seg1.Id, "  ", Guid.NewGuid(), "assignee", T.Now);
        Assert.Equal("", t.Render().Single(r => r.Segment.StartMs == 0).Segment.Text);
        Assert.Equal(2, t.Render().Count); // still there, so the tracked changes show what was taken out
        Assert.Throws<DomainException>(() => t.EditSegment(t.Render()[0].Segment.Id, "", Guid.NewGuid(), "assignee", T.Now));
        t.Restore(1, Guid.NewGuid(), "author", T.Now);
        Assert.Equal("Hello everyone", t.Render()[0].Segment.Text);
    }

    [Fact]
    public void A_passage_can_be_joined_to_the_paragraph_before_without_changing_its_words()
    {
        var (t, _, _, seg1) = Make();
        var second = t.Render()[1].Segment;
        Assert.Throws<DomainException>(() => t.JoinWithPrevious(seg1.Id, Guid.NewGuid(), "assignee", T.Now)); // nothing before the first
        var v = t.JoinWithPrevious(second.Id, Guid.NewGuid(), "assignee", T.Now);
        Assert.Equal(VersionKind.HumanEdit, v.Kind);
        Assert.False(t.Render()[1].Segment.NewParagraph);
        Assert.Equal(second.Text, t.Render()[1].Segment.Text);
        Assert.Throws<DomainException>(() => t.JoinWithPrevious(t.Render()[1].Segment.Id, Guid.NewGuid(), "assignee", T.Now));
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

    [Fact]
    public void Joining_a_box_to_the_one_above_moves_its_words_and_timings_up_and_keeps_the_count_of_boxes()
    {
        var a = new Speaker(Guid.NewGuid(), "Reiss"); var b = new Speaker(Guid.NewGuid(), "Pat");
        var s1 = new Segment(Guid.NewGuid(), a.Id, "en", 0, 1000, "I think so", 0.9, false, [new("I", 0, 200), new("think", 200, 600), new("so", 600, 1000)]);
        var s2 = new Segment(Guid.NewGuid(), b.Id, "en", 1000, 1500, "too.", 0.9, false, [new("too.", 1000, 1500)]);
        var s3 = new Segment(Guid.NewGuid(), b.Id, "en", 1600, 2000, "Next point", 0.9, false);
        var t = Transcript.CreateMachineV1(Guid.NewGuid(), "test", [a, b], [s1, s2, s3], T.Now);
        t.MergeWithPrevious(s2.Id, Guid.NewGuid(), "Owner", T.Now);
        var now = t.Render();
        Assert.Equal(3, now.Count);
        Assert.Equal("I think so too.", now[0].Segment.Text);
        Assert.Equal(a.Id, now[0].Segment.SpeakerId);
        Assert.Equal(1500, now[0].Segment.EndMs);
        Assert.Equal(["I", "think", "so", "too."], now[0].Segment.Words!.Select(w => w.Text));
        Assert.Equal("", now[1].Segment.Text);
        // The next box skips the empty one and joins the nearest box with text.
        t.MergeWithPrevious(now[2].Segment.Id, Guid.NewGuid(), "Owner", T.Now);
        Assert.Equal("I think so too. Next point", t.Render()[0].Segment.Text);
        Assert.Throws<DomainException>(() => t.MergeWithPrevious(t.Render()[0].Segment.Id, Guid.NewGuid(), "Owner", T.Now));
        Assert.Equal("too.", t.Render(1)[1].Segment.Text); // the machine version is untouched
    }

    [Fact]
    public void A_correction_can_be_made_in_every_box_at_once()
    {
        var sp = new Speaker(Guid.NewGuid(), "Pat");
        var segs = new[]
        {
            new Segment(Guid.NewGuid(), sp.Id, "en", 0, 1000, "Rhys said all notes are done.", 0.9, false),
            new Segment(Guid.NewGuid(), sp.Id, "en", 1000, 2000, "Thanks, rhys. All  notes and Rhyss.", 0.9, false),
            new Segment(Guid.NewGuid(), sp.Id, "en", 2000, 3000, "Nothing here.", 0.9, false),
        };
        var t = Transcript.CreateMachineV1(Guid.NewGuid(), "test", [sp], segs, T.Now);
        var r = t.ReplaceEverywhere("Rhys", "Reiss", Guid.NewGuid(), "Owner", T.Now)!.Value;
        Assert.Equal((2, 2), (r.Passages, r.Changes));
        var r2 = t.ReplaceEverywhere("all notes", "AllNote", Guid.NewGuid(), "Owner", T.Now)!.Value;
        Assert.Equal(2, r2.Changes);
        Assert.Equal(["Reiss said AllNote are done.", "Thanks, Reiss. AllNote and Rhyss.", "Nothing here."], t.Render().Select(x => x.Segment.Text));
        Assert.Equal(3, t.Current.No); // one version per change, whatever the number of boxes
        Assert.Null(t.ReplaceEverywhere("nobody", "Somebody", Guid.NewGuid(), "Owner", T.Now));
    }
}
