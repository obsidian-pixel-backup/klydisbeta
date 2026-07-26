---
name: async-concurrency-patterns
description: Mastering asynchronous programming, thread pools, event loops, semaphores, race condition prevention, and non-blocking I/O in Python, Node.js, and Go.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# Async & Concurrency Patterns

Concurrently executing tasks requires managing event loops, thread safety, synchronization primitives, and non-blocking I/O to maximize throughput without memory corruption or deadlocks.

## Core Concepts

1. **Event Loop vs Worker Threads**: Node.js/Python asyncio use single-threaded event loops for I/O; CPU-bound tasks require worker threads/processes.
2. **Race Conditions**: Concurrent mutation of shared state leads to non-deterministic bugs $\rightarrow$ Use locks, semaphores, or immutable state.
3. **Deadlocks**: Two threads waiting indefinitely for locks held by each other $\rightarrow$ Enforce strict lock ordering rules.

---

## Concurrency Blueprints across Languages

### 1. Python Asyncio Semaphore (Rate Limiting)
```python
import asyncio

async def fetch_url(url: str, semaphore: asyncio.Semaphore):
    async with semaphore:
        print(f"Fetching {url}")
        await asyncio.sleep(1) # Simulate HTTP I/O
        return f"Content of {url}"

async def main():
    semaphore = asyncio.Semaphore(3) # Max 3 concurrent requests
    urls = [f"https://api.example.com/item/{i}" for i in range(10)]
    tasks = [fetch_url(url, semaphore) for url in urls]
    results = await asyncio.gather(*tasks)
    print(f"Downloaded {len(results)} pages.")

asyncio.run(main())
```

### 2. Go Worker Pool Pattern
```go
package main

import "fmt"

func worker(id int, jobs <-chan int, results chan<- int) {
    for j := range jobs {
        fmt.Printf("Worker %d started job %d\n", id, j)
        results <- j * 2
    }
}

func main() {
    jobs := make(chan int, 100)
    results := make(chan int, 100)

    for w := 1; w <= 3; w++ {
        go worker(w, jobs, results)
    }

    for j := 1; j <= 5; j++ { jobs <- j }
    close(jobs)

    for a := 1; a <= 5; a++ { <-results }
}
```

---

## Verification Checklist

- [ ] All async promises/futures handle exceptions (`try/catch` or `.catch()`).
- [ ] Shared state mutations are protected by mutexes, channels, or atomic primitives.
- [ ] Background workers support graceful shutdown signals (`SIGINT`, `SIGTERM`).
- [ ] No unhandled floating promises exist in Node.js/TypeScript code.
