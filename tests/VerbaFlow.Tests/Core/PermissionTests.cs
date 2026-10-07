using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Security;

namespace VerbaFlow.Tests.Core;

public class PermissionTests
{
    private static bool Can(User u, Item i, Capability c) => PermissionEvaluator.Evaluate(u, i, c).Allowed;

    [Fact]
    public void The_author_has_read_and_write_until_approval()
    {
        var a = T.U("Alice"); var i = T.Recorded(a);
        Assert.True(Can(a, i, Capability.Read));
        Assert.True(Can(a, i, Capability.Write));
        i.Approve(a.Id, false, false, T.Now);
        Assert.True(Can(a, i, Capability.Read));
        Assert.False(Can(a, i, Capability.Write));
    }

    [Fact]
    public void The_assignee_can_edit_the_transcript_only_while_working_on_it()
    {
        var a = T.U("Alice"); var b = T.U("Bob"); var i = T.Recorded(a);
        i.Assign(b.Id);
        Assert.True(Can(b, i, Capability.Read));
        Assert.False(Can(b, i, Capability.Write));   // awaiting, not opened yet
        i.OpenByAssignee(b.Id);
        Assert.True(Can(b, i, Capability.Write));
        i.ReturnToOwner(b.Id);
        Assert.False(Can(b, i, Capability.Write));
    }

    [Fact]
    public void An_assignee_cannot_approve_reassign_or_export_audio_by_default()
    {
        var a = T.U("Alice"); var b = T.U("Bob"); var i = T.Recorded(a);
        i.Assign(b.Id); i.OpenByAssignee(b.Id);
        Assert.False(Can(b, i, Capability.Approve));
        Assert.False(Can(b, i, Capability.Reassign));
        Assert.False(Can(b, i, Capability.ExportAudio));
    }

    [Fact]
    public void Strangers_see_and_do_nothing()
    {
        var a = T.U("Alice"); var x = T.U("Xavier"); var i = T.Recorded(a);
        Assert.False(Can(x, i, Capability.Read));
        Assert.False(PermissionEvaluator.CanSee(x, i));
    }

    [Fact]
    public void Admins_can_read_reassign_and_approve_but_not_download_audio_without_a_grant()
    {
        var a = T.U("Alice"); var admin = T.U("Carol", admin: true); var i = T.Recorded(a);
        Assert.True(Can(admin, i, Capability.Read));
        Assert.True(Can(admin, i, Capability.Reassign));
        Assert.True(Can(admin, i, Capability.Approve));
        Assert.False(Can(admin, i, Capability.ExportAudio));
    }

    [Fact]
    public void Grants_add_capabilities_independently()
    {
        var a = T.U("Alice");
        var reader = T.U("Rita", grants: Capability.Read | Capability.Export);
        var i = T.Recorded(a);
        Assert.True(Can(reader, i, Capability.Export));
        Assert.True(Can(reader, i, Capability.Read));
        Assert.False(Can(reader, i, Capability.Write));
    }

    [Fact]
    public void Locks_beat_grants_and_admin_rights()
    {
        var a = T.U("Alice");
        var admin = T.U("Carol", admin: true, grants: Capability.Write | Capability.EditLockedContent | Capability.Reassign);
        var i = T.Recorded(a);
        i.Approve(a.Id, false, false, T.Now);
        Assert.False(Can(admin, i, Capability.Write));
        Assert.False(Can(admin, i, Capability.EditLockedContent));
        Assert.False(Can(admin, i, Capability.Reassign));
        Assert.True(Can(admin, i, Capability.Read));
    }

    [Fact]
    public void Require_throws_Forbidden_with_a_reason()
    {
        var a = T.U("Alice"); var x = T.U("Xavier"); var i = T.Recorded(a);
        Assert.Throws<ForbiddenException>(() => PermissionEvaluator.Require(x, i, Capability.Read));
    }
}
