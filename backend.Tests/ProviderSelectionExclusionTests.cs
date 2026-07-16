using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using Usenet.Nzb;
using UsenetSharp.Models;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// Provider exclusion is a preference, not a verdict. The straggler race excludes a slow provider
/// so the retry hedges the segment somewhere else — but if excluding empties the candidate list,
/// the segment must still be attempted rather than failing outright. A single-provider setup hits
/// that on every straggler: the only provider gets excluded, selection returns nothing, and the
/// caller is told "There are no usenet providers configured" while a working provider sits idle.
/// See issue #16.
/// </summary>
public class ProviderSelectionExclusionTests
{
    private sealed class StubNntpClient : INntpClient
    {
        public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken ct) => Task.FromResult(true);
        public Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<NzbWebDAV.Clients.Usenet.Models.UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetDateResponse> DateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task WaitForReady(CancellationToken ct) => Task.CompletedTask;
        public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private static readonly List<ConnectionPool<INntpClient>> s_pools = new();

    private static MultiConnectionNntpClient BuildProvider(int index, ProviderType type = ProviderType.Pooled)
    {
        var pool = new ConnectionPool<INntpClient>(
            10,
            new ExtendedSemaphoreSlim(10, 10),
            _ => ValueTask.FromResult<INntpClient>(new StubNntpClient()),
            poolName: $"provider-{index}",
            idleTimeout: TimeSpan.FromMinutes(15));
        s_pools.Add(pool);
        return new MultiConnectionNntpClient(pool, type, providerIndex: index, host: $"provider-{index}");
    }

    /// <summary>
    /// Builds the token a straggler retry carries: a usage context whose details exclude the
    /// providers already found wanting for this segment.
    /// </summary>
    private static IDisposable ExcludeProviders(CancellationToken token, params int[] indices)
    {
        var details = new ConnectionUsageDetails { ExcludedProviderIndices = new HashSet<int>(indices) };
        return token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.BufferedStreaming, details));
    }

    [Fact]
    public void SingleProvider_ExcludedByStragglerRace_IsStillOffered()
    {
        var provider = BuildProvider(0);
        var client = new MultiProviderNntpClient(new List<MultiConnectionNntpClient> { provider });

        using var cts = new CancellationTokenSource();
        using var scope = ExcludeProviders(cts.Token, 0);

        var selected = client.GetBalancedProviders(cts.Token).ToList();

        // Before the fix this was empty, and the caller threw "There are no usenet providers
        // configured" — with the only configured provider sitting right there, healthy.
        var only = Assert.Single(selected);
        Assert.Equal(0, only.ProviderIndex);
    }

    [Fact]
    public void MultipleProviders_ExcludedOneIsRankedLast_NotDropped()
    {
        var excludedProvider = BuildProvider(0);
        var healthy = BuildProvider(1);
        var client = new MultiProviderNntpClient(new List<MultiConnectionNntpClient> { excludedProvider, healthy });

        using var cts = new CancellationTokenSource();
        using var scope = ExcludeProviders(cts.Token, 0);

        var selected = client.GetBalancedProviders(cts.Token).ToList();

        // The point of the race is preserved: the non-excluded provider is tried first, so the
        // excluded one is only reached if the preferred provider actually errors.
        Assert.Equal(2, selected.Count);
        Assert.Equal(1, selected[0].ProviderIndex);
        Assert.Equal(0, selected[1].ProviderIndex);
    }

    [Fact]
    public void AllProvidersExcluded_AllAreStillOffered()
    {
        var a = BuildProvider(0);
        var b = BuildProvider(1);
        var client = new MultiProviderNntpClient(new List<MultiConnectionNntpClient> { a, b });

        using var cts = new CancellationTokenSource();
        using var scope = ExcludeProviders(cts.Token, 0, 1);

        var selected = client.GetBalancedProviders(cts.Token).ToList();

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void DisabledProvider_StaysExcluded_EvenAsLastResort()
    {
        var disabled = BuildProvider(0, ProviderType.Disabled);
        var healthy = BuildProvider(1);
        var client = new MultiProviderNntpClient(new List<MultiConnectionNntpClient> { disabled, healthy });

        using var cts = new CancellationTokenSource();
        using var scope = ExcludeProviders(cts.Token, 0, 1);

        var selected = client.GetBalancedProviders(cts.Token).ToList();

        // "Disabled" is a verdict from the user, unlike exclusion — the last-resort tier must not
        // resurrect a provider they turned off.
        var only = Assert.Single(selected);
        Assert.Equal(1, only.ProviderIndex);
    }

    [Fact]
    public void NoExclusions_SelectionIsUnchanged()
    {
        var a = BuildProvider(0);
        var b = BuildProvider(1);
        var client = new MultiProviderNntpClient(new List<MultiConnectionNntpClient> { a, b });

        using var cts = new CancellationTokenSource();
        using var scope = cts.Token.SetScopedContext(
            new ConnectionUsageContext(ConnectionUsageType.BufferedStreaming, new ConnectionUsageDetails()));

        var selected = client.GetBalancedProviders(cts.Token).ToList();

        Assert.Equal(2, selected.Count);
        // No provider appears twice via the new last-resort tier.
        Assert.Equal(selected.Count, selected.Select(x => x.ProviderIndex).Distinct().Count());
    }
}
