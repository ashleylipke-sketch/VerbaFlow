namespace VerbaFlow.Core.Domain;

public static class StatusLabels
{
    /// <summary>The label a mode shows for an internal status code.</summary>
    public static string For(ItemMode mode, ItemStatus status) => (mode, status) switch
    {
        (ItemMode.Speech, ItemStatus.WithAuthor) => "Speech to Text",
        (_, ItemStatus.WithAuthor) => "With Author",
        (_, ItemStatus.WithImporter) => "Imported",
        (_, ItemStatus.AwaitingAssignee) => "Awaiting Assignee",
        (_, ItemStatus.WithAssignee) => "With Assignee",
        (_, ItemStatus.ConversionFailed) => "Conversion Failed",
        (_, ItemStatus.Completed) => "Completed",
        (_, ItemStatus.Purged) => "Purged",
        (_, ItemStatus.Draft) => "Draft",
        _ => status.ToString()
    };

    /// <summary>The views a mode's dashboard offers, in display order.</summary>
    public static IReadOnlyList<ItemStatus> ViewsFor(ItemMode mode) => mode switch
    {
        ItemMode.Meeting => [ItemStatus.WithAuthor, ItemStatus.WithImporter, ItemStatus.AwaitingAssignee,
            ItemStatus.WithAssignee, ItemStatus.Completed, ItemStatus.ConversionFailed, ItemStatus.Purged],
        ItemMode.Speech => [ItemStatus.Draft, ItemStatus.WithAuthor, ItemStatus.AwaitingAssignee,
            ItemStatus.WithAssignee, ItemStatus.Completed, ItemStatus.ConversionFailed],
        _ => [ItemStatus.Draft, ItemStatus.WithAuthor, ItemStatus.AwaitingAssignee, ItemStatus.WithAssignee,
            ItemStatus.Completed, ItemStatus.ConversionFailed]
    };
}
