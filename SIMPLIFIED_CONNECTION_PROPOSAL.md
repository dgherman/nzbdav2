# Simplified Connection Pool Proposal

## Current Problems

1. **Inverted reservation logic** - `RequiredAvailable=30` means "keep 30 free" but operations requesting with this value can use UP TO 115 connections
2. **No per-operation limits** - HealthCheck configured for 60 but uses 80+
3. **Queue starvation** - Queue gets only 11 connections when it should get 30
4. **Complex semantics** - `ReservedPooledConnectionsContext` is confusing and error-prone

## Proposed Solution: Per-Operation Pool Limits

### Architecture
Replace the single global pool with reservation system with **separate logical pools per operation type**:

```
Total Pool: 145 connections
├── Queue Pool: 30 connections (Priority 0 - highest)
├── HealthCheck Pool: 60 connections (Priority 1)
├── Streaming Pool: 55 connections (Priority 2 - remaining)
```

### Implementation Approach

**Option 1: Connection Pool Wrapper (Recommended)**
- Keep existing `ConnectionPool<T>` as-is
- Create `OperationLimitedConnectionPool<T>` wrapper
- Wrapper tracks usage per `ConnectionUsageType`
- Enforces hard limits before delegating to underlying pool
- Simple, non-invasive change

**Option 2: Multi-Pool Architecture**
- Create separate physical pools for each operation type
- More isolated but requires more refactoring
- Connections can't be shared across operation types (less efficient)

### Benefits

1. **Clear semantics** - Each operation has a hard connection limit
2. **Priority enforcement** - Queue gets its 30 connections guaranteed
3. **No starvation** - HealthCheck can't consume beyond its 60 limit
4. **Simpler code** - Remove `ReservedPooledConnectionsContext` and `ExtendedSemaphoreSlim`
5. **Easy to reason about** - Usage never exceeds configured limits

### Configuration

```csharp
// In ConfigManager
public int GetMaxQueueConnections() => 30;
public int GetMaxRepairConnections() => 60;  // HealthCheck
public int GetConnectionsPerStream() => 5;   // Per individual stream
// Streaming pool = Total - Queue - HealthCheck
```

### Migration Path

1. Implement `OperationLimitedConnectionPool<T>` wrapper
2. Replace usage of `ReservedPooledConnectionsContext` with operation limits
3. Remove `ExtendedSemaphoreSlim` complexity
4. Use simple `SemaphoreSlim` per operation type
5. Test with Queue + HealthCheck + Streaming simultaneously

## Alternative: Keep Current System But Fix It

If we want to keep the reservation system:

1. **Invert the logic properly**:
   - Queue sets `RequiredAvailable=0` (highest priority)
   - HealthCheck sets `RequiredAvailable=85` (keep 85 free = max 60 for itself)
   - Streaming sets `RequiredAvailable=85` (keep 85 free = max 60 available)

2. **Add per-operation tracking in ConnectionPool**:
   - Track active connections per `ConnectionUsageType`
   - Reject requests that would exceed configured limit
   - This requires modifying `ConnectionPool<T>.GetConnectionLockAsync()`

3. **Problems with this approach**:
   - Still complex and error-prone
   - Reservation value calculation is unintuitive
   - Hard to ensure limits are respected

## Recommendation

**Go with Option 1: Connection Pool Wrapper**

This provides:
- Minimal code changes
- Clear, enforceable limits
- Better isolation between operation types
- Easy to test and verify
- Can be implemented incrementally
