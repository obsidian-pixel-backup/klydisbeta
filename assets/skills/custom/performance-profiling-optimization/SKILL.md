---
name: performance-profiling-optimization
description: Methodology for profiling and optimizing code performance — identifying CPU bottlenecks, memory allocation hot paths, garbage collection pressures, async/concurrency stalls, memory leaks, and algorithmic complexity improvements. Use when profiling applications, fixing slow response times, reducing memory usage, or optimizing hot loops.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Performance Profiling & Optimization

Premature optimization is the root of all evil — but unmeasured code is a guess. Always profile before optimizing, establish baseline benchmarks, and measure the impact of changes under realistic workloads.

## The Performance Workflow

1. **Measure First**: Use profilers (`dotnet-trace`, `pprof`, `Chrome DevTools`, `perf`, `BenchmarkDotNet`) to locate actual hot spots.
2. **Isolate**: Create isolated benchmark tests reproducing the hot path.
3. **Optimize**: Apply high-leverage optimizations (algorithmic improvements, data structure changes, zero-allocation buffers).
4. **Verify**: Run benchmarks to ensure speedup without regression.

## Common Optimization Categories

### 1. Algorithmic Complexity
- Upgrade from $O(N^2)$ nested loops to $O(N \log N)$ or $O(N)$ lookup dictionaries/hash maps.

### 2. Allocation & GC Pressure
- Avoid heap allocations inside high-frequency hot loops.
- Use reusable object pools (`ArrayPool<T>`, memory buffers, StringBuilder).
- Use stack allocation (`stackalloc`, `Span<T>`) where lifetime is strictly bound to the scope.

### 3. Database & I/O Stalls
- Batch network calls and database queries.
- Avoid sync-over-async blocking (`.Result`, `.Wait()`) which causes thread pool starvation.

## Rules for Micro-Optimization

- Don't sacrifice code readability for negligible 1% gains outside hot execution paths.
- Benchmark with representative data distributions — small test datasets mask algorithmic scaling issues.

## Checklist

- [ ] Optimization is backed by actual profiling metrics (CPU/Memory trace)
- [ ] Automated benchmark (e.g. BenchmarkDotNet) captures before & after results
- [ ] No sync-over-async thread blocking on I/O operations
- [ ] Allocations inside tight loops eliminated or pooled
