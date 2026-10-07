using VerbaFlow.Core.Domain;

namespace VerbaFlow.Tests;

public static class T
{
    public static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public static User U(string name, bool admin = false, Capability grants = Capability.None) =>
        new(Guid.NewGuid(), name, $"{name.ToLowerInvariant()}@example.test", admin, grants);

    public static Item Recorded(User owner, bool processed = true)
    {
        var i = Item.CreateRecorded(Guid.NewGuid(), owner.Id, "Board meeting", 60_000, "en", Now);
        if (processed) i.CompleteProcessing();
        return i;
    }

    public static Item Imported(User owner, bool processed = true)
    {
        var i = Item.CreateImported(Guid.NewGuid(), owner.Id, "Client call", 90_000, "en", Now, "call.mp4", "supplied by client");
        if (processed) i.CompleteProcessing();
        return i;
    }
}
