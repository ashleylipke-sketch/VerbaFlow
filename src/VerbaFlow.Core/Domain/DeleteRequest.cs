namespace VerbaFlow.Core.Domain;

/// <summary>
/// Deleting a recording that was approved before (and has since been reopened) needs two different administrators.
/// An administrator who asks counts as the first of the two; an owner who asks needs two administrators.
/// </summary>
public sealed class DeleteRequest
{
    public Guid Id { get; init; }
    public Guid ItemId { get; init; }
    public Guid RequestedBy { get; init; }
    public string Reason { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public List<Guid> Approvals { get; init; } = [];
    public Guid? RejectedBy { get; set; }
    public string? RejectionReason { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsApproved => Approvals.Count >= 2;
    public bool IsOpen => RejectedBy is null && DeletedAt is null;

    public static DeleteRequest Create(Guid id, Guid itemId, User requester, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to ask for a deletion.");
        var r = new DeleteRequest { Id = id, ItemId = itemId, RequestedBy = requester.Id, Reason = reason.Trim(), CreatedAt = now };
        if (requester.IsAdmin) r.Approvals.Add(requester.Id);
        return r;
    }

    public void Approve(User admin)
    {
        if (!IsOpen) throw new DomainException("This request is already closed.");
        if (!admin.IsAdmin) throw new DomainException("Only administrators can approve a deletion.");
        if (Approvals.Contains(admin.Id)) throw new DomainException("This administrator has already approved.");
        Approvals.Add(admin.Id);
    }

    public void Reject(User admin, string reason)
    {
        if (!IsOpen) throw new DomainException("This request is already closed.");
        if (!admin.IsAdmin) throw new DomainException("Only administrators can reject a deletion.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to reject.");
        RejectedBy = admin.Id;
        RejectionReason = reason.Trim();
    }
}
