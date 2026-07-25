---
name: distributed-caching
description: Strategies and best practices for distributed caching — Cache-Aside, Write-Through, Write-Behind, TTL policies, Cache Stampede prevention (Singleflight/Mutex), Redis data structures (Hashes, Sorted Sets, HyperLogLog), and eviction policies (LRU, LFU). Use when designing caching layers, tuning Redis/Memcached performance, or debugging cache inconsistencies.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Distributed Caching Strategies

Caching improves application throughput and decreases database read pressure. Incorrect caching strategies, however, lead to stale data, race conditions, and thundering herd failures.

## Common Caching Patterns

### 1. Cache-Aside (Lazy Loading) - Default Pattern
Application manages cache explicitly:

```
[ Client ] ──► [ Application ] ──1. Check Cache──► [ Redis Cache ]
                     │                                   │ (Miss)
                     ├──────────2. Query Database───────►│
                     │                                   ▼
                     └──────────3. Populate Cache──────► [ Database ]
```

### 2. Write-Through
Application writes data to Cache first, and the Cache immediately synchronously writes to Database. Ensures low read latency and strong consistency.

### 3. Write-Behind (Write-Back)
Application writes to Cache; Cache asynchronously batches updates to Database. Provides high write throughput, but risks data loss if the cache node crashes before flushing.

## Preventing Cache Stampede (Thundering Herd)

When a hot cache key expires, thousands of concurrent requests miss the cache simultaneously and overwhelm the database.

- **Distributed Mutex / Singleflight**: Ensure only ONE request fetches from the database to populate the cache while others wait:
  ```csharp
  public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl)
  {
      var cached = await _redis.GetAsync<T>(key);
      if (cached != null) return cached;

      using (await _semaphore.WaitAsync(key))
      {
          // Double-check cache inside lock
          cached = await _redis.GetAsync<T>(key);
          if (cached != null) return cached;

          var value = await factory();
          await _redis.SetAsync(key, value, ttl);
          return value;
      }
  }
  ```

## Key Eviction & TTL Policies

- Always assign a **Time-To-Live (TTL)** to every cached key to prevent unbounded memory growth.
- Add random jitter to TTLs ($\pm 10\%$) so large batches of keys don't expire at the exact same second.

## Checklist

- [ ] All cached keys have an explicit TTL with randomized jitter
- [ ] Hot cache keys use Singleflight / Mutex locking to prevent cache stampedes
- [ ] Redis data types chosen optimally (Hashes for objects, Sorted Sets for leaderboards)
- [ ] Invalidation logic triggers immediately when entities update
