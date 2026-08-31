using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Services;
using NzbWebDAV.Tools;
using NzbWebDAV.Websocket;
using Usenet.Nzb;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// Cross-review requirement for issue #28: <see cref="Issue28RarStreamMisalignmentTests"/> drives
/// <c>NzbFileStream</c>/<c>RarUtil</c> directly with a hand-picked <c>segmentSizes</c> argument --
/// it never touches <see cref="RarProcessor.GetFastNzbFileStream"/>, the method that actually
/// DECIDES what to pass. Reverting that decision back to the pre-fix
/// <c>UsenetStreamingClient.GetFastFileStream</c> (fabricated uniform table) left every test in that
/// file passing, because none of them exercise the selection itself.
///
/// This test does. It drives a REAL <see cref="RarProcessor"/> backed by a REAL
/// <see cref="UsenetStreamingClient"/> talking real NNTP to an in-process
/// <see cref="MockNntpServer"/> serving the genuine <c>-hp</c> RAR5 fixtures in
/// <c>Fixtures/issue28</c>, chunked at REAL non-uniform segment sizes (a fixed larger body plus a
/// short final segment -- the actual shape yEnc articles take, not the synthesized filler
/// <c>MockNzbGenerator</c> produces). This is the exact seam issue #28 broke:
/// <c>RarProcessor.ProcessAsync</c> -&gt; <c>GetFastNzbFileStream</c> -&gt; the real
/// <c>UsenetStreamingClient</c> stream-selection call.
///
/// Two volumes, one control and one target, so the assertion distinguishes "the fix broke
/// everything" from "the fix broke exactly the drifted volume":
/// - <c>hp.part1.rar</c> (control): registered as a SINGLE segment. A single segment's fabricated
///   "uniform" table (<c>fileSize / 1</c>) is trivially exact, so this volume parses under BOTH the
///   pre-fix and post-fix code -- it proves a passing volume doesn't mask a failing one.
/// - <c>hp.part2.rar</c> (target, the real continuation volume from the original repro): registered
///   at 7 full 131,072-byte segments plus a genuinely short final segment -- the shape that drifts
///   the fabricated uniform table hundreds of KB by the time RAR5 header parsing seeks to the
///   file/end-archive header near end-of-volume.
///
/// Pre-fix (<c>RarProcessor.cs</c> reverted to call <c>GetFastFileStream</c> when
/// <c>segmentSizes</c> is null): part2's header parse misaligns and <c>ProcessPartAsync</c> catches
/// the failure per-volume, so the OVERALL result silently loses part2's file -- 1 stored segment
/// instead of 2. Post-fix: both volumes parse, 2 stored segments, both declaring the fixture's real
/// uncompressed size.
/// </summary>
public class Issue28RarProcessorIntegrationTests : IAsyncLifetime
{
    private string? _configPath;
    private string? _previousConfigPath;
    private MockNntpServer? _server;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _server?.Dispose();
        if (_configPath != null)
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
            try { Directory.Delete(_configPath, recursive: true); } catch { /* best-effort cleanup */ }
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Slices <paramref name="volume"/> into segments matching <paramref name="segmentSizes"/> and
    /// registers them on <paramref name="server"/> under freshly-generated message ids, returning
    /// those ids in order (what an <c>NzbFile</c>'s segment list would carry).
    /// </summary>
    private static string[] RegisterVolume(
        MockNntpServer server, string volumeTag, byte[] volume, long[] segmentSizes)
    {
        var segments = new List<(string SegmentId, byte[] Payload, long PartOffset)>();
        var ids = new string[segmentSizes.Length];
        long offset = 0;
        for (var i = 0; i < segmentSizes.Length; i++)
        {
            var id = $"issue28-{volumeTag}-{i}-{Guid.NewGuid():N}@mock.server";
            var payload = new byte[segmentSizes[i]];
            Array.Copy(volume, offset, payload, 0, payload.Length);
            segments.Add((id, payload, offset));
            ids[i] = id;
            offset += segmentSizes[i];
        }
        Assert.Equal(volume.Length, offset);

        server.RegisterRawSegments($"hp.{volumeTag}.rar", volume.Length, segments);
        return ids;
    }

    private static string BuildNzbXml(IReadOnlyList<(string FileName, string[] SegmentIds)> files)
    {
        var date = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version='1.0' encoding='utf-8' ?>");
        sb.AppendLine("<!DOCTYPE nzb PUBLIC '-//newzBin//DTD NZB 1.1//EN' 'http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd'>");
        sb.AppendLine("<nzb xmlns='http://www.newzbin.com/DTD/2003/nzb'>");
        foreach (var (fileName, segmentIds) in files)
        {
            sb.Append($" <file poster='Mock' date='{date}' subject='{fileName}'>\n");
            sb.AppendLine("  <groups><group>mock.group</group></groups>");
            sb.AppendLine("  <segments>");
            for (var i = 0; i < segmentIds.Length; i++)
                sb.Append($"   <segment bytes='1' number='{i + 1}'>{segmentIds[i]}</segment>\n");
            sb.AppendLine("  </segments>");
            sb.AppendLine(" </file>");
        }
        sb.AppendLine("</nzb>");
        return sb.ToString();
    }

    /// <summary>
    /// Builds a real <see cref="UsenetStreamingClient"/> pointed at <paramref name="mockPort"/>.
    /// Requires a migrated <c>CONFIG_PATH</c> because <see cref="ConfigManager"/> and
    /// <see cref="UsenetStreamingClient"/> are concrete classes with no test seam of their own --
    /// this IS the production wiring (mirrors <c>Program.cs</c>'s <c>--test-full-nzb --mock-server</c>
    /// path). Isolated per test via a unique temp <c>CONFIG_PATH</c>; no other test in this suite
    /// touches that env var or constructs a default <c>DavDatabaseContext</c>, so mutating it here is
    /// safe even under xunit's cross-class parallelism.
    /// </summary>
    private async Task<UsenetStreamingClient> BuildRealClientAsync(int mockPort)
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        _configPath = Path.Combine(Path.GetTempPath(), $"issue28-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_configPath);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configPath);

        await using (var db = new DavDatabaseContext())
            await db.Database.MigrateAsync();

        var configManager = new ConfigManager();
        await configManager.LoadConfig();

        var providerConfig = new UsenetProviderConfig
        {
            Providers = new List<UsenetProviderConfig.ConnectionDetails>
            {
                new()
                {
                    Type = ProviderType.Pooled,
                    Host = "127.0.0.1",
                    Port = mockPort,
                    UseSsl = false,
                    User = "mock",
                    Pass = "mock",
                    MaxConnections = 10,
                },
            },
        };
        configManager.UpdateValues(new List<ConfigItem>
        {
            new() { ConfigName = "usenet.providers", ConfigValue = JsonSerializer.Serialize(providerConfig) },
        });

        var services = new ServiceCollection();
        services.AddSingleton(configManager);
        services.AddSingleton<WebsocketManager>();
        services.AddSingleton<BandwidthService>();
        services.AddSingleton<ProviderErrorService>();
        services.AddSingleton<NzbProviderAffinityService>();
        services.AddSingleton<UsenetStreamingClient>();
        services.AddScoped<DavDatabaseContext>(_ => new DavDatabaseContext());
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<UsenetStreamingClient>();
    }

    [Fact]
    public async Task RarProcessor_KeepsBothVolumesAligned_AgainstRealNntpServer()
    {
        var part1 = Issue28Fixtures.Volume("hp.part1.rar");
        var part2 = Issue28Fixtures.Volume("hp.part2.rar");

        var server = new MockNntpServer(port: 0, latencyMs: 0, jitterMs: 0, timeoutRate: 0);
        _server = server; // disposed in DisposeAsync
        server.Start();

        // Control: one segment, so a fabricated "uniform" table (fileSize/1) is exact by
        // construction. Its parse succeeding on BOTH pre-fix and post-fix code is what proves this
        // test isolates the drifted volume rather than failing wholesale.
        var part1Ids = RegisterVolume(server, "part1", part1, new[] { (long)part1.Length });

        // Target: real drifted layout -- 7 full 131,072-byte segments then a genuinely short tail.
        var part2Layout = Issue28Fixtures.NonUniformLayout(part2.Length, fullCount: 7, fullSize: 131_072);
        var part2Ids = RegisterVolume(server, "part2", part2, part2Layout);

        var nzbXml = BuildNzbXml(new (string, string[])[]
        {
            ("hp.part1.rar", part1Ids),
            ("hp.part2.rar", part2Ids),
        });
        await using var nzbStream = new MemoryStream(Encoding.UTF8.GetBytes(nzbXml));
        var nzb = await NzbDocument.LoadAsync(nzbStream);
        var nzbFilesByName = nzb.Files.ToDictionary(f => f.FileName);

        var client = await BuildRealClientAsync(server.Port);

        var fileInfos = new List<GetFileInfosStep.FileInfo>
        {
            new()
            {
                NzbFile = nzbFilesByName["hp.part1.rar"],
                FileName = "hp.part1.rar",
                ReleaseDate = DateTimeOffset.UtcNow,
                FileSize = part1.Length, // the "declared" size a Par2 descriptor would supply
                IsRar = true,
                MagicOffset = 0,
                MissingFirstSegment = false,
                SegmentSizes = null, // RAR volumes skip smart-analysis in production (see FetchFirstSegmentsStep)
            },
            new()
            {
                NzbFile = nzbFilesByName["hp.part2.rar"],
                FileName = "hp.part2.rar",
                ReleaseDate = DateTimeOffset.UtcNow,
                FileSize = part2.Length,
                IsRar = true,
                MagicOffset = 0,
                MissingFirstSegment = false,
                SegmentSizes = null,
            },
        };

        var processor = new RarProcessor(fileInfos, client, Issue28Fixtures.Password, CancellationToken.None);
        var result = await processor.ProcessAsync() as RarProcessor.Result;

        Assert.NotNull(result);
        // The whole point: BOTH volumes' File headers survive. Pre-fix (GetFastFileStream's
        // fabricated uniform table), part2's header parse misaligns near end-of-volume and
        // ProcessPartAsync swallows the per-volume failure -- this comes back as 1, not 2.
        Assert.Equal(2, result!.StoredFileSegments.Length);
        Assert.All(result.StoredFileSegments, s =>
        {
            Assert.Equal("payload.bin", s.PathWithinArchive);
            Assert.Equal(Issue28Fixtures.DeclaredUncompressedSize, s.FileUncompressedSize);
        });
        // RarProcessor prefers the volume number the RAR5 archive header itself declares (0-based,
        // set-relative) over filename parsing -- these fixtures are a genuine 3-volume set, so
        // hp.part1.rar/hp.part2.rar truthfully self-report as volumes 0 and 1.
        Assert.Equal(new[] { 0, 1 }, result.StoredFileSegments.Select(s => s.PartNumber).OrderBy(x => x));
    }
}
