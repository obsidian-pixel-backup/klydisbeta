---
name: system-design-scalability
description: Architectural standards for building high-scale, distributed systems — horizontal scaling, load balancing, caching hierarchies, database sharding/partitioning, stateless server design, read/write segregation, rate limiting, and CAP theorem trade-offs. Use when designing scalable systems, preparing for high traffic, or conducting system design interviews/reviews.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# System Design & Scalability

Scalability is a system's ability to handle growing amounts of work by adding resources without redesigning the core architecture.

## Scale-Up vs Scale-Out

- **Vertical Scaling (Scale-Up)**: Adding CPU/RAM to a single server. Limited by hardware ceiling and single point of failure.
- **Horizontal Scaling (Scale-Out)**: Adding more commodity nodes behind a load balancer. Requires stateless application servers.

## Key Scalability Patterns

### 1. Stateless Application Tier
- Store session state in external fast data stores (Redis) or use stateless JWTs.
- Any application node can process any incoming client request.

### 2. Database Read/Write Segregation
- Route mutating traffic (`INSERT`, `UPDATE`, `DELETE`) to Primary DB node.
- Route read traffic (`SELECT`) to Read Replicas. Handle replication lag explicitly in UI/workflows.

### 3. Database Sharding & Partitioning
- **Horizontal Sharding**: Partition large tables across multiple database instances using a hash of `shard_key` (e.g. `user_id % num_shards`).
- Avoid cross-shard joins; denormalize or join at application layer.

### 4. Caching Hierarchy
- **Edge / CDN**: Static assets and public API payloads.
- **Application Cache (Redis / Memcached)**: Hot query results and user sessions.
- **Database Buffer Pool**: In-memory database page cache.

## CAP Theorem & PACELC

- **CAP**: In a network partition ($P$), a system must choose between Consistency ($C$) or Availability ($A$).
- **PACELC**: If there is a partition ($P$), trade off $A$ and $C$; Else ($E$), trade off Latency ($L$) and Consistency ($C$).

## Checklist

- [ ] Web application servers 100% stateless
- [ ] Database read queries routed to read replicas for heavy read workloads
- [ ] Sharding key chosen to evenly distribute reads/writes
- [ ] Rate limiters protect upstream services from traffic spikes
- [ ] System handles single-node instance failures without downtime
