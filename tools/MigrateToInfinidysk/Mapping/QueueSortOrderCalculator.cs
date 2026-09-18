namespace NzbWebDAV.MigrateToInfinidysk.Mapping;

/// <summary>
/// Backfills QueueItem.SortOrder using infinidysk's own formula from migration
/// 20260817160000_Add-QueueItem-SortOrder.cs:
/// SortOrder = ROW_NUMBER() OVER (PARTITION BY Priority ORDER BY CreatedAt, Id) * 1024
/// </summary>
public static class QueueSortOrderCalculator
{
    public readonly record struct Item(Guid Id, int Priority, DateTime CreatedAt);

    public static IReadOnlyDictionary<Guid, long> Backfill(IEnumerable<Item> items)
    {
        var result = new Dictionary<Guid, long>();

        var groups = items.GroupBy(i => i.Priority);
        foreach (var group in groups)
        {
            var ordered = group
                .OrderBy(i => i.CreatedAt)
                .ThenBy(i => i.Id);

            var rowNumber = 0L;
            foreach (var item in ordered)
            {
                rowNumber++;
                result[item.Id] = rowNumber * 1024;
            }
        }

        return result;
    }
}
