# Benchmarks

What the pipeline costs over the same work done by hand.

```sh
dotnet run -c Release --project benchmarks/Levee.Benchmarks -- --filter '*'
```

It starts Postgres 17 in a container, installs Levee's schema and the sample app's table with its isolation policy, and
connects as the least-privilege role the grants describe. Each pair does the same work in one transaction, scoped to one
tenant by row-level security:

- **By hand:** EF on the app's context, `begin`, `set transaction read only` for the read, `set_config('levee.scope', …)`, the
  query or insert, `commit`.
- **Dispatched:** the operation through the `Dispatcher`: scope, the access policy, the unit of work (`begin`, read-only,
  both scope settings), admissions, the handler, the audit for the write, `commit`, the outcome.

## Results

Apple M5 (10 cores), macOS 26.5, .NET 10.0.15, Postgres 17 in Docker Desktop, BenchmarkDotNet 0.15.8, 10 warmups and 40
iterations.

| Benchmark | Mean | Error | Allocated |
|---|---:|---:|---:|
| read: EF in a transaction, by hand | 635.5 µs | ±5.1 µs | 88.8 KB |
| read: `notes.list`, dispatched | 649.5 µs | ±9.9 µs | 111.7 KB |
| write: EF in a transaction, by hand | 479.6 µs | ±5.9 µs | 72.4 KB |
| write: `notes.create`, dispatched and audited | 909.8 µs | ±12.8 µs | 100.5 KB |

## Reading them

- **A read costs about 2% more dispatched** (14 µs, 23 KB): authorization, the scope, a DI scope of its own and the outcome
  are in-process; the round trips are the same ones a careful hand-written read makes.
- **A write costs about 430 µs more**, nearly all of it Postgres round trips: the audit record is one more statement in
  the transaction (through `levee_audit_write`), and the sample handler's statements go through EF's change tracking where
  the by-hand insert is one raw statement. The pipeline's in-process share is about the read's 14 µs.
- **Round trips dominate.** Each costs about 100 µs here, through Docker Desktop's network on macOS; a database on the
  same network as the app pays less for each, and the pipeline's in-process share stays the same.
- **Not measured yet:** a subscription's recheck (a read-only unit of work per recheck, every 30 seconds per open
  stream) and jobs (Oxbow's own [benchmarks](https://github.com/rkingon/Oxbow/blob/main/docs/benchmarks.md) cover the
  queue).
