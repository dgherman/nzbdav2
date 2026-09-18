using NzbWebDAV.MigrateToInfinidysk.Mapping;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class QueueSortOrderCalculatorTests
{
    // Mirrors infinidysk migration 20260817160000_Add-QueueItem-SortOrder.cs:
    // SortOrder = ROW_NUMBER() OVER (PARTITION BY Priority ORDER BY CreatedAt, Id) * 1024

    [Fact]
    public void Backfill_SinglePriorityGroup_AssignsSequentialMultiplesOf1024OrderedByCreatedAtThenId()
    {
        var idLow = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var idHigh = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var items = new[]
        {
            new QueueSortOrderCalculator.Item(idHigh, Priority: 0, CreatedAt: new DateTime(2026, 1, 1)),
            new QueueSortOrderCalculator.Item(idLow, Priority: 0, CreatedAt: new DateTime(2026, 1, 1)),
        };

        var result = QueueSortOrderCalculator.Backfill(items);

        // same CreatedAt -> tiebreak by Id ascending
        Assert.Equal(1024, result[idLow]);
        Assert.Equal(2048, result[idHigh]);
    }

    [Fact]
    public void Backfill_MultiplePriorityGroups_RestartsRowNumberPerPartition()
    {
        var a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var b = Guid.Parse("00000000-0000-0000-0000-00000000000b");
        var c = Guid.Parse("00000000-0000-0000-0000-00000000000c");
        var items = new[]
        {
            new QueueSortOrderCalculator.Item(a, Priority: 1, CreatedAt: new DateTime(2026, 1, 1)),
            new QueueSortOrderCalculator.Item(b, Priority: 1, CreatedAt: new DateTime(2026, 1, 2)),
            new QueueSortOrderCalculator.Item(c, Priority: 0, CreatedAt: new DateTime(2026, 1, 1)),
        };

        var result = QueueSortOrderCalculator.Backfill(items);

        Assert.Equal(1024, result[a]); // priority 1, row 1
        Assert.Equal(2048, result[b]); // priority 1, row 2
        Assert.Equal(1024, result[c]); // priority 0, row 1 (separate partition)
    }

    [Fact]
    public void Backfill_OrdersByCreatedAtBeforeId()
    {
        var later = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var earlier = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var items = new[]
        {
            new QueueSortOrderCalculator.Item(later, Priority: 0, CreatedAt: new DateTime(2026, 1, 2)),
            new QueueSortOrderCalculator.Item(earlier, Priority: 0, CreatedAt: new DateTime(2026, 1, 1)),
        };

        var result = QueueSortOrderCalculator.Backfill(items);

        Assert.Equal(1024, result[earlier]);
        Assert.Equal(2048, result[later]);
    }
}
