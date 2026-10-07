namespace VerbaFlow.Core.Domain;

/// <summary>Reopening an approved item needs two different admins, neither of them the requester.</summary>
public sealed class ReopenRequest
{
    public Guid Id { get; init; }
    public Guid ItemId { get; init; }
    public Guid RequestedBy { get; init; }
    public string Reason { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public List<Guid> Approvals { get; init; } = [];
    public Guid? RejectedBy { get; set; }
    public string? RejectionReason { get; set; }
    public Guid? NewItemId { get; set; }

    public bool IsApproved => Approvals.Count >= 2;
    public bool IsOpen => RejectedBy is null && NewItemId is null;

    public static ReopenRequest Create(Guid id, Guid itemId, Guid requestedBy, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to request a reopen.");
        return new ReopenRequest { Id = id, ItemId = itemId, RequestedBy = requestedBy, Reason = reason.Trim(), CreatedAt = now };
    }

    public void Approve(User admin)
    {
        if (!IsOpen) throw new DomainException("This request is already closed.");
        if (!admin.IsAdmin) throw new DomainException("Only administrators can approve a reopen.");
        if (admin.Id == RequestedBy) throw new DomainException("The requester cannot approve their own request.");
        if (Approvals.Contains(admin.Id)) throw new DomainException("This administrator has already approved.");
        Approvals.Add(admin.Id);
    }

    public void Reject(User admin, string reason)
    {
        if (!IsOpen) throw new DomainException("This request is already closed.");
        if (!admin.IsAdmin) throw new DomainException("Only administrators can reject a reopen.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required to reject.");
        RejectedBy = admin.Id;
        RejectionReason = reason.Trim();
    }
}
