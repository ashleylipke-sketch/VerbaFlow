namespace VerbaFlow.Core.Domain;

public enum ItemMode { Dictate, Speech, Meeting }

/// <summary>OnlineMeeting: recorded by Meet's note-taker, which joined an online meeting (Zoom, Google Meet, Teams, Webex).</summary>
public enum SourceKind { Recorded, Imported, PhoneCall, OnlineMeeting }

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

/// <summary>Attendee: a colleague who also asked Meet's note-taker to record the same online meeting. They can read it, and export it once approved.</summary>
public enum ItemRole { None, Owner, Assignee, Attendee }

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
