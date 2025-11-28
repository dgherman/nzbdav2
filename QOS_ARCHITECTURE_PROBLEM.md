# QoS Architecture Problem - Root Cause Analysis

## Understanding the Intended Architecture

### Correct Design (What You Described):
```
Total System: 145 pooled connections
├── Provider 1: 95 connections ────┐
└── Provider 2: 50 connections ────┼──> Shared Pool (145 total)
                                    │
                                    ↓
            OperationLimitedConnectionPool
            ├── Queue: 30 (guaranteed)
            ├── HealthCheck: 60 (guaranteed/max)
            └── Streaming: 55 (guaranteed)
```

**Key Points:**
- Providers share a **global semaphore** (`ExtendedSemaphoreSlim` with 145 slots)
- All operations compete for the same 145 connections
- Limits (30/60/55) apply **globally across all providers**
- MultiProviderNntpClient routes requests to providers with idle connections

## Current Incorrect Implementation

### What We Built:
```
Provider 1 (95 connections)
    ↓
OperationLimitedConnectionPool #1
├── Queue: 30
├── HealthCheck: 60
└── Streaming: 55

Provider 2 (50 connections)
    ↓
OperationLimitedConnectionPool #2
├── Queue: 30
├── HealthCheck: 60
└── Streaming: 55

Both wrapped by MultiProviderNntpClient
```

**The Bug:**
- Each provider gets its own OperationLimitedConnectionPool
- Limits (30/60/55) apply **per-provider** instead of globally
- With 2 providers: theoretical max = 60 Queue + 120 HealthCheck + 110 Streaming = 290!
- The global semaphore (145 slots) still exists and prevents creating 290 physical connections
- But we can queue up to 290 **operations** waiting for those 145 physical connections

## Code Analysis

### Where the Bug Happens

**File:** `UsenetStreamingClient.cs:167-188`

```csharp
private MultiProviderNntpClient CreateMultiProviderClient(UsenetProviderConfig providerConfig)
{
    var connectionPoolStats = new ConnectionPoolStats(providerConfig, _websocketManager);
    var totalPooledConnectionCount = providerConfig.TotalPooledConnections; // 145
    var pooledSemaphore = new ExtendedSemaphoreSlim(totalPooledConnectionCount, totalPooledConnectionCount); // Global!

    // Get operation limits from config
    var maxQueueConnections = _configManager.GetMaxQueueConnections(); // 30
    var maxHealthCheckConnections = _configManager.GetMaxRepairConnections(); // 60

    // BUG: Creates SEPARATE OperationLimitedPool for EACH provider
    var providerClients = providerConfig.Providers
        .Select((provider, index) => CreateProviderClient(
            provider,
            connectionPoolStats,
            index,
            pooledSemaphore, // ← Same global semaphore passed to all
            maxQueueConnections, // ← Same limits passed to all
            maxHealthCheckConnections,
            totalPooledConnectionCount
        ))
        .ToList();

    return new MultiProviderNntpClient(providerClients);
}
```

**File:** `UsenetStreamingClient.cs:190-214`

```csharp
private MultiConnectionNntpClient CreateProviderClient(
    UsenetProviderConfig.ConnectionDetails connectionDetails,
    ConnectionPoolStats connectionPoolStats,
    int providerIndex,
    ExtendedSemaphoreSlim pooledSemaphore,
    int maxQueueConnections,
    int maxHealthCheckConnections,
    int totalConnections
)
{
    // Creates OperationLimitedPool wrapper for THIS PROVIDER
    var connectionPool = CreateNewConnectionPool(
        maxConnections: connectionDetails.MaxConnections, // 95 for P1, 50 for P2
        pooledSemaphore: pooledSemaphore, // Global semaphore (145 slots)
        connectionFactory: ct => CreateNewConnection(connectionDetails, ct),
        onConnectionPoolChanged: connectionPoolStats.GetOnConnectionPoolChanged(providerIndex),
        connectionPoolStats: connectionPoolStats,
        providerIndex: providerIndex,
        maxQueueConnections: maxQueueConnections, // 30 PER PROVIDER ❌
        maxHealthCheckConnections: maxHealthCheckConnections, // 60 PER PROVIDER ❌
        totalConnections: totalConnections // 145
    );
    return new MultiConnectionNntpClient(connectionPool, connectionDetails.Type);
}
```

**File:** `UsenetStreamingClient.cs:138-165`

```csharp
private OperationLimitedConnectionPool<INntpClient> CreateNewConnectionPool(
    int maxConnections,
    ExtendedSemaphoreSlim pooledSemaphore,
    Func<CancellationToken, ValueTask<INntpClient>> connectionFactory,
    EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged,
    ConnectionPoolStats connectionPoolStats,
    int providerIndex,
    int maxQueueConnections,
    int maxHealthCheckConnections,
    int totalConnections
)
{
    // Creates inner pool for THIS PROVIDER with global semaphore
    var innerPool = new ConnectionPool<INntpClient>(maxConnections, pooledSemaphore, connectionFactory);
    innerPool.OnConnectionPoolChanged += onConnectionPoolChanged;
    connectionPoolStats.RegisterConnectionPool(providerIndex, innerPool);
    var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
    onConnectionPoolChanged(innerPool, args);

    // Wraps with OperationLimitedPool - SEPARATE FOR EACH PROVIDER ❌
    return new OperationLimitedConnectionPool<INntpClient>(
        innerPool,
        maxQueueConnections,     // 30 per provider
        maxHealthCheckConnections, // 60 per provider
        totalConnections         // 145 (same for all, but applied separately!)
    );
}
```

## How MultiProviderNntpClient Works

**File:** `MultiProviderNntpClient.cs:69-111`

```csharp
private async Task<T> RunFromPoolWithBackup<T>(
    Func<INntpClient, Task<T>> task,
    CancellationToken cancellationToken
)
{
    var orderedProviders = GetOrderedProviders(lastSuccessfulProvider);

    foreach (var provider in orderedProviders)
    {
        try
        {
            result = await task.Invoke(provider).ConfigureAwait(false);
            return result;
        }
        catch (Exception e)
        {
            // Try next provider
        }
    }
}

private IEnumerable<MultiConnectionNntpClient> GetOrderedProviders(...)
{
    return providers
        .Where(x => x.ProviderType != ProviderType.Disabled)
        .OrderBy(x => x.ProviderType)
        .ThenByDescending(x => x.IdleConnections)
        .ThenByDescending(x => x.RemainingSemaphoreSlots) // Routes to provider with most capacity
        .Prepend(preferredProvider)
        // ...
}
```

**Key Insight:**
- MultiProviderNntpClient picks a provider based on which has **idle connections**
- Each provider has its own OperationLimitedPool
- When operation requests connection:
  1. MultiProvider picks Provider 1 or 2 based on availability
  2. That provider's OperationLimitedPool checks its own semaphores (30/60/55)
  3. If semaphore available, requests from inner ConnectionPool
  4. Inner ConnectionPool uses **global semaphore** (145 slots)

## The Problem in Practice

### Example Scenario:

**Initial state:**
- Global semaphore: 145 slots
- Provider 1 OperationLimitedPool: Queue=30, HealthCheck=60, Streaming=55
- Provider 2 OperationLimitedPool: Queue=30, HealthCheck=60, Streaming=55

**HealthCheck starts (needs 60 connections):**
1. MultiProvider routes first 60 requests to Provider 1
2. Provider 1 OperationLimitedPool: HealthCheck semaphore allows all 60
3. Provider 1 ConnectionPool creates/reuses connections (uses global semaphore)
4. **Provider 1 HealthCheck: 60/60 slots used**

**More HealthCheck requests (needs 60 more):**
1. MultiProvider sees Provider 1 is busy, routes to Provider 2
2. Provider 2 OperationLimitedPool: HealthCheck semaphore allows all 60
3. Provider 2 ConnectionPool tries to create connections
4. **Global semaphore only has 85 slots left (145 - 60 = 85)**
5. Provider 2 can only get 85 connections, blocks waiting for more
6. **Total HealthCheck: 120 operations queued (60 + 60) but only 145 physical connections**

**Result:**
- OperationLimitedPool thinks: "I have 60 HealthCheck slots available"
- But global semaphore only has 85 total slots left
- HealthCheck "guaranteed" 60 connections per provider = 120 total
- This exceeds the global 145 limit!

## Why the Logs Show 149 Operations

**Log Evidence:**
```
Usage=Queue=25,HealthCheck=60,BufferedStreaming=64
Total: 149 operations tracked
```

This is **aggregate tracking** across both providers:
- Provider 1: ~75 operations (mix of Queue/HealthCheck/Streaming)
- Provider 2: ~74 operations (mix of Queue/HealthCheck/Streaming)
- Total: 149 operations being tracked by both OperationLimitedPools

But the physical connections:
- Live=60 (Provider 1) + Live=16 (Provider 2) = 76 physical connections
- Global semaphore: 145 - 76 = 69 slots remaining

The discrepancy (149 tracked operations vs. 76 physical) happens because:
1. OperationLimitedPool tracks semaphore acquisitions (logical operations)
2. ConnectionPool tracks physical connections
3. Multiple operations might share/reuse the same physical connection
4. Or operations are queued waiting for physical connections to become available

## The Fix Required

### Option 1: Single Global OperationLimitedPool (Correct Approach)

Wrap the **MultiProviderNntpClient** instead of each provider:

```csharp
private MultiProviderNntpClient CreateMultiProviderClient(UsenetProviderConfig providerConfig)
{
    var totalPooledConnectionCount = providerConfig.TotalPooledConnections; // 145
    var pooledSemaphore = new ExtendedSemaphoreSlim(totalPooledConnectionCount, totalPooledConnectionCount);

    // Create providers WITHOUT OperationLimitedPool wrapper
    var providerClients = providerConfig.Providers
        .Select((provider, index) => CreateProviderClientWithoutLimits(
            provider,
            connectionPoolStats,
            index,
            pooledSemaphore
        ))
        .ToList();

    // Create MultiProvider client
    var multiProviderClient = new MultiProviderNntpClient(providerClients);

    // Wrap the ENTIRE MultiProvider with ONE OperationLimitedPool
    return WrapWithOperationLimits(
        multiProviderClient,
        maxQueueConnections: 30,
        maxHealthCheckConnections: 60,
        totalConnections: 145
    );
}
```

**Challenge:** MultiProviderNntpClient is already a complete `INntpClient`. OperationLimitedConnectionPool expects `ConnectionPool<INntpClient>`, not `INntpClient`.

### Option 2: Distributed Limits with Coordination

Split the 30/60/55 limits across providers proportionally:

```
Provider 1 (95 connections = 65.5% of 145):
├── Queue: 20 (65.5% of 30)
├── HealthCheck: 39 (65.5% of 60)
└── Streaming: 36 (65.5% of 55)

Provider 2 (50 connections = 34.5% of 145):
├── Queue: 10 (34.5% of 30)
├── HealthCheck: 21 (34.5% of 60)
└── Streaming: 19 (34.5% of 55)
```

**Pros:** Simple math, keeps per-provider wrapping
**Cons:** Inflexible - if Provider 1 is idle, Queue can't use its 20+10=30 allocation

### Option 3: Global Semaphore at OperationLimitedPool Level

Create a **shared OperationLimitedPool instance** that all providers use:

```csharp
private MultiProviderNntpClient CreateMultiProviderClient(UsenetProviderConfig providerConfig)
{
    var totalPooledConnectionCount = providerConfig.TotalPooledConnections;
    var pooledSemaphore = new ExtendedSemaphoreSlim(totalPooledConnectionCount, totalPooledConnectionCount);

    // Create ONE shared OperationLimitedPool coordinator
    var sharedLimits = new OperationLimitCoordinator(
        maxQueueConnections: 30,
        maxHealthCheckConnections: 60,
        maxStreamingConnections: 55
    );

    var providerClients = providerConfig.Providers
        .Select((provider, index) => CreateProviderClientWithSharedLimits(
            provider,
            connectionPoolStats,
            index,
            pooledSemaphore,
            sharedLimits // ← Same instance for all providers
        ))
        .ToList();

    return new MultiProviderNntpClient(providerClients);
}
```

**Requires:** Refactoring OperationLimitedConnectionPool to be a coordinator that can wrap multiple ConnectionPools.

## Recommended Solution: Option 3 with Refactoring

Create a **global operation limiter** that sits above the provider routing:

### New Architecture:
```
GlobalOperationLimiter (Queue=30, HealthCheck=60, Streaming=55)
    ↓
    [Acquire global operation semaphore first]
    ↓
MultiProviderNntpClient (routes to provider)
    ↓
Provider 1: ConnectionPool(95) ──┐
Provider 2: ConnectionPool(50) ──┼──> Global Semaphore (145)
```

### Implementation Steps:

1. **Create `GlobalOperationLimiter`** - Not a pool wrapper, just a semaphore coordinator
2. **Inject into `GetConnectionLockAsync` context** - Before calling MultiProvider
3. **Acquire operation semaphore** - Before requesting from provider
4. **Release on operation completion** - After provider connection is returned

This way:
- Global limits enforced BEFORE provider selection
- Providers remain simple ConnectionPools with global semaphore
- QoS ballooning works across all providers naturally

## Summary

**Root Cause:**
- OperationLimitedConnectionPool is applied **per-provider** instead of **globally**
- With 2 providers, limits are effectively doubled (60+60=120 HealthCheck instead of 60 global)

**Why It Partially Works:**
- Global semaphore (145 slots) prevents creating too many physical connections
- But operations can queue up beyond intended limits

**Why Queue Appears Starved:**
- Queue only using 25 connections is likely correct (single-item processing)
- But if it needs to scale to 30, it competes with:
  - HealthCheck: 60 (correct limit, but per-provider = 120 potential)
  - BufferedStreaming: 64+ (ballooning correctly, but per-provider = 110+ potential)

**Fix Required:**
- Implement global operation limiting BEFORE provider routing
- Ensure 30/60/55 limits apply across ALL providers combined
- Maintain QoS ballooning behavior within those global limits
