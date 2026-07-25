---
name: csharp-dotnet-best-practices
description: Best practices for modern C# and .NET development — async/await discipline, Span<T>/Memory<T> allocations, Dependency Injection lifetimes, LINQ optimization, record types, IAsyncEnumerable streaming, and resource disposal (IAsyncDisposable). Use when writing C# code, reviewing .NET applications, or building high-performance .NET services.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# C# & .NET Best Practices

Modern .NET (version 8+) is high performance, memory-efficient, and cross-platform. Writing idiomatic C# requires understanding async flow, memory lifetime management, and DI scopes.

## Async / Await Discipline

- Always pass `CancellationToken` through to asynchronous APIs.
- Avoid `.Result` or `.Wait()` which blocks threads and can deadlock:
  ```csharp
  // BAD: var data = GetDataAsync().Result;
  // GOOD: var data = await GetDataAsync(cancellationToken);
  ```
- Use `ConfigureAwait(false)` in reusable class libraries to avoid capturing synchronization context.

## Memory & Performance (`Span<T>` and `Memory<T>`)

Use `ReadOnlySpan<char>` for zero-allocation parsing of strings:

```csharp
public static bool TryParseYear(ReadOnlySpan<char> input, out int year)
{
    // Slicing ReadOnlySpan creates no string allocation on the heap
    ReadOnlySpan<char> yearSpan = input.Slice(0, 4);
    return int.TryParse(yearSpan, out year);
}
```

## Dependency Injection Lifetimes

- **Transient**: Created each time requested. Use for lightweight, stateless services.
- **Scoped**: Created once per request/scope. Use for `DbContext` or per-operation contexts. Never inject a Scoped service into a Singleton!
- **Singleton**: Created once per application lifetime. Must be thread-safe.

## Resource Disposal (`IAsyncDisposable`)

```csharp
public async ValueTask DisposeAsync()
{
    if (_stream != null)
    {
        await _stream.DisposeAsync();
        _stream = null;
    }
    GC.SuppressFinalize(this);
}
```

## Checklist

- [ ] Async methods accept and pass `CancellationToken`
- [ ] No sync-over-async (`.Result`, `.Wait()`) present
- [ ] Scoped services are not captured inside Singleton singletons
- [ ] High-frequency string parsing uses `Span<char>` / `ReadOnlySpan<char>`
- [ ] Disposable resources wrapped in `using` or `await using`
