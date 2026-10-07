namespace VerbaFlow.Core.Domain;

public enum ItemMode { Dictate, Speech, Meeting }

public enum SourceKind { Recorded, Imported, PhoneCall }

/// <summary>Internal status codes. Labels vary per mode (see StatusLabels).</summary>
public enum ItemStatus
{
    Draft,
    WithAuthor,        // in-app recordings awaiting author review/approval
    WithImporter,      // shown as "Imported" in Meeting mode
    AwaitingAssignee,
    WithAssignee,
    ConversionFailed,
    Completed,
    Purged
}

public enum ProcessingState { Idle, Running }

public enum Priority { Low, Normal, High, Urgent }

public enum MarkerType { ConsentNotice, Objection, Pause, Resume }

public enum ItemRole { None, Owner, Assignee }

[Flags]
public enum Capability
{
    None = 0,
    Read = 1,
    Write = 2,
    Reassign = 4,
    Export = 8,
    ExportAudio = 16,
    ViewPhoneNumbers = 32,
    Approve = 64,
    EditLockedContent = 128
}

public sealed class DomainException(string message) : Exception(message);

public sealed class ForbiddenException(string message) : Exception(message);

public sealed class NotFoundException(string message) : Exception(message);
