using VerbaFlow.Core.Domain;

namespace VerbaFlow.Tests.Core;

public class WorkflowTests
{
    [Fact]
    public void A_new_recording_sits_with_the_author_while_processing()
    {
        var i = T.Recorded(T.U("Alice"), processed: false);
        Assert.Equal(ItemStatus.WithAuthor, i.Status);
        Assert.Equal(ProcessingState.Running, i.Processing);
    }

    [Fact]
    public void A_new_import_sits_in_Imported_never_With_Author()
    {
        var i = T.Imported(T.U("Alice"), processed: false);
        Assert.Equal(ItemStatus.WithImporter, i.Status);
        Assert.Equal("Imported", StatusLabels.For(ItemMode.Meeting, i.Status));
    }

    [Fact]
    public void Assigning_to_someone_else_moves_to_Awaiting_Assignee()
    {
        var a = T.U("Alice"); var b = T.U("Bob");
        var i = T.Recorded(a);
        i.Assign(b.Id);
        Assert.Equal(ItemStatus.AwaitingAssignee, i.Status);
        Assert.False(i.IsSelfAssigned);
    }

    [Fact]
    public void Self_assigned_items_skip_the_assignee_statuses_and_carry_the_tag()
    {
        var a = T.U("Alice");
        var i = T.Recorded(a);
        i.Assign(a.Id);
        Assert.Equal(ItemStatus.WithAuthor, i.Status);
        Assert.True(i.IsSelfAssigned);
    }

    [Fact]
    public void Assignee_opens_then_returns_a_recording_to_With_Author()
    {
        var a = T.U("Alice"); var b = T.U("Bob");
        var i = T.Recorded(a);
        i.Assign(b.Id);
        i.OpenByAssignee(b.Id);
        Assert.Equal(ItemStatus.WithAssignee, i.Status);
        i.ReturnToOwner(b.Id);
        Assert.Equal(ItemStatus.WithAuthor, i.Status);
        Assert.False(i.IsSelfAssigned); // no tag once it has come back from someone else
    }

    [Fact]
    public void An_imported_item_returns_to_Imported_not_With_Author()
    {
        var a = T.U("Alice"); var b = T.U("Bob");
        var i = T.Imported(a);
        i.Assign(b.Id); i.OpenByAssignee(b.Id); i.ReturnToOwner(b.Id);
        Assert.Equal(ItemStatus.WithImporter, i.Status);
    }

    [Fact]
    public void Only_the_named_assignee_can_open_or_return()
    {
        var a = T.U("Alice"); var b = T.U("Bob"); var c = T.U("Carol");
        var i = T.Recorded(a);
        i.Assign(b.Id);
        Assert.Throws<DomainException>(() => i.OpenByAssignee(c.Id));
        i.OpenByAssignee(b.Id);
        Assert.Throws<DomainException>(() => i.ReturnToOwner(c.Id));
    }

    [Fact]
    public void Reassigning_after_sickness_moves_it_back_to_Awaiting_Assignee()
    {
        var a = T.U("Alice"); var b = T.U("Bob"); var c = T.U("Carol");
        var i = T.Recorded(a);
        i.Assign(b.Id); i.OpenByAssignee(b.Id);
        i.Assign(c.Id);
        Assert.Equal(ItemStatus.AwaitingAssignee, i.Status);
        Assert.Equal(c.Id, i.AssignedUserId);
    }

    [Fact]
    public void Items_cannot_be_assigned_while_processing()
    {
        var a = T.U("Alice");
        var i = T.Recorded(a, processed: false);
        Assert.Throws<DomainException>(() => i.Assign(a.Id));
    }

    [Fact]
    public void Approval_locks_the_item_against_every_change()
    {
        var a = T.U("Alice");
        var i = T.Recorded(a);
        i.Approve(a.Id, false, false, T.Now);
        Assert.Equal(ItemStatus.Completed, i.Status);
        Assert.True(i.IsLocked);
        Assert.Throws<DomainException>(() => i.UpdateDetails("New", null, null, null, null, false));
        Assert.Throws<DomainException>(() => i.Assign(a.Id));
        Assert.Throws<DomainException>(() => i.AddMarker(new TimelineMarker { Type = MarkerType.Objection, OffsetMs = 1, At = T.Now }));
    }

    [Fact]
    public void Approval_is_only_possible_from_the_owners_stage()
    {
        var a = T.U("Alice"); var b = T.U("Bob");
        var i = T.Recorded(a);
        i.Assign(b.Id);
        Assert.Throws<DomainException>(() => i.Approve(a.Id, false, false, T.Now));
    }

    [Fact]
    public void Separation_of_duties_blocks_self_approval_unless_an_admin_approves()
    {
        var a = T.U("Alice");
        var i = T.Recorded(a);
        i.Assign(a.Id);
        Assert.Throws<DomainException>(() => i.Approve(a.Id, false, blockSelfApproval: true, T.Now));
        i.Approve(Guid.NewGuid(), approverIsAdmin: true, blockSelfApproval: true, T.Now);
        Assert.Equal(ItemStatus.Completed, i.Status);
    }

    [Fact]
    public void A_failed_conversion_can_be_retried_and_returns_to_where_it_was()
    {
        var a = T.U("Alice");
        var i = T.Imported(a, processed: false);
        i.FailProcessing("speech service unavailable");
        Assert.Equal(ItemStatus.ConversionFailed, i.Status);
        i.Retry();
        Assert.Equal(ItemStatus.WithImporter, i.Status);
        Assert.Equal(ProcessingState.Running, i.Processing);
    }

    [Fact]
    public void A_pause_needs_a_reason()
    {
        var i = T.Recorded(T.U("Alice"));
        Assert.Throws<DomainException>(() => i.AddMarker(new TimelineMarker { Type = MarkerType.Pause, OffsetMs = 5000, At = T.Now }));
        i.AddMarker(new TimelineMarker { Type = MarkerType.Pause, OffsetMs = 5000, Note = "Comfort break", At = T.Now });
        Assert.Single(i.Markers);
    }

    [Fact]
    public void An_objection_is_logged_and_flags_the_item_without_stopping_anything()
    {
        var i = T.Recorded(T.U("Alice"));
        i.AddMarker(new TimelineMarker { Type = MarkerType.Objection, OffsetMs = 12_000, Note = "Attendee objected", At = T.Now });
        Assert.True(i.ObjectionFlag);
        Assert.Equal(ItemStatus.WithAuthor, i.Status);
    }

    [Fact]
    public void Meeting_mode_has_exactly_the_agreed_views()
    {
        var labels = StatusLabels.ViewsFor(ItemMode.Meeting).Select(s => StatusLabels.For(ItemMode.Meeting, s)).ToArray();
        Assert.Equal(["With Author", "Imported", "Awaiting Assignee", "With Assignee", "Completed", "Conversion Failed", "Purged"], labels);
    }

    [Fact]
    public void Reopening_creates_a_linked_new_version_and_leaves_the_original_untouched()
    {
        var a = T.U("Alice");
        var original = T.Recorded(a);
        original.Approve(a.Id, false, false, T.Now);
        var next = original.CreateReopenedVersion(Guid.NewGuid(), T.Now);
        Assert.Equal(ItemStatus.Completed, original.Status);
        Assert.Equal(2, next.VersionNo);
        Assert.Equal(original.Id, next.SupersedesItemId);
        Assert.Equal(original.ChainId, next.ChainId);
        Assert.Equal(original.MediaSourceItemId, next.MediaSourceItemId);
        Assert.Equal(ItemStatus.WithAuthor, next.Status);
    }

    [Fact]
    public void Only_completed_items_can_be_reopened()
    {
        var i = T.Recorded(T.U("Alice"));
        Assert.Throws<DomainException>(() => i.CreateReopenedVersion(Guid.NewGuid(), T.Now));
    }

    [Fact]
    public void Reopen_needs_two_different_admins_and_not_the_requester()
    {
        var requester = T.U("Reqadmin", admin: true);
        var admin1 = T.U("Admin1", admin: true);
        var admin2 = T.U("Admin2", admin: true);
        var nonAdmin = T.U("Nigel");
        var r = ReopenRequest.Create(Guid.NewGuid(), Guid.NewGuid(), requester.Id, "Wrong client name", T.Now);
        Assert.Throws<DomainException>(() => r.Approve(requester));
        Assert.Throws<DomainException>(() => r.Approve(nonAdmin));
        r.Approve(admin1);
        Assert.Throws<DomainException>(() => r.Approve(admin1));
        Assert.False(r.IsApproved);
        r.Approve(admin2);
        Assert.True(r.IsApproved);
    }

    [Fact]
    public void A_reopen_request_needs_a_reason() =>
        Assert.Throws<DomainException>(() => ReopenRequest.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), " ", T.Now));
}
