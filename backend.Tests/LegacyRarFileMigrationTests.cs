using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests;

public class LegacyRarFileMigrationTests
{
    private static DavDatabaseContext NewInMemoryCtx(SqliteConnection conn)
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(conn).Options;
        var ctx = new DavDatabaseContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public async Task ConvertsRarFileRowToMultipartFile_AndFlipsType_AndIsIdempotent()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var itemId = Guid.NewGuid();
        using (var ctx = NewInMemoryCtx(conn))
        {
            // Root-level item (ParentId null) avoids the self-referential ParentId FK.
            var item = new DavItem
            {
                Id = itemId,
                IdPrefix = itemId.GetFiveLengthPrefix(),
                CreatedAt = DateTime.Now,
                ParentId = null,
                Name = "movie.mkv",
                FileSize = 1000,
                Type = DavItem.ItemType.RarFile,
                Path = "/movie.mkv",
            };
            ctx.Items.Add(item);
            ctx.RarFiles.Add(new DavRarFile
            {
                Id = itemId,
                RarParts = new[]
                {
                    new DavRarFile.RarPart
                    {
                        SegmentIds = new[] { "a@x", "b@x" },
                        PartSize = 1000, Offset = 0, ByteCount = 1000, ObfuscationKey = null
                    }
                }
            });
            await ctx.SaveChangesAsync();
        }

        using (var ctx = NewInMemoryCtx(conn))
        {
            Assert.Equal(1, await LegacyRarFileMigration.RunAsync(ctx));
        }

        using (var ctx = NewInMemoryCtx(conn))
        {
            Assert.False(await ctx.RarFiles.AnyAsync());
            var mp = await ctx.MultipartFiles.FirstOrDefaultAsync(x => x.Id == itemId);
            Assert.NotNull(mp);
            Assert.Equal(new[] { "a@x", "b@x" }, mp!.Metadata.FileParts[0].SegmentIds);
            var item = await ctx.Items.FirstAsync(x => x.Id == itemId);
            Assert.Equal(DavItem.ItemType.MultipartFile, item.Type);
        }

        // Idempotent: second run converts nothing.
        using (var ctx = NewInMemoryCtx(conn))
        {
            Assert.Equal(0, await LegacyRarFileMigration.RunAsync(ctx));
        }
    }
}
