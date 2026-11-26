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
- **The same statements and audit, by hand** (writes only): what the dispatched write sends, both scope settings, the
  `levee_audit_write` call and a raw insert, so what's left between it and the dispatched write is in-process work and
  EF's tracked insert.
- **Dispatched:** the operation through the `Dispatcher`: scope, the access policy, the unit of work (`begin`, read-only,
  both scope settings), admissions, the handler, the audit for the write, `commit`, the outcome.

The host logs nothing (EF logs every command at Information, and writing that to the console would be measured too), and
Postgres runs with `fsync=off`, as Oxbow's benchmarks do: a disk's flush time differs by machine more than anything here.

## Results

Apple M5 (10 cores), macOS 26.5, .NET 10.0.15, Postgres 17 in Docker Desktop, BenchmarkDotNet 0.15.8, 10 warmups and 40
iterations.

| Benchmark | Mean | Error | Allocated |
|---|---:|---:|---:|
| read: EF in a transaction, by hand | 550.7 µs | ±14.8 µs | 85.2 KB |
| read: `notes.list`, dispatched | 548.5 µs | ±7.7 µs | 106.7 KB |
| write: EF in a transaction, by hand | 423.5 µs | ±14.1 µs | 68.5 KB |
| write: the same statements and audit, by hand | 547.3 µs | ±5.4 µs | 76.1 KB |
| write: `notes.create`, dispatched and audited | 780.2 µs | ±13.1 µs | 92.7 KB |

## Reading them

- **A read costs the same dispatched**, within the error (and 21 KB more): authorization, the scope, a DI scope of its
  own and the outcome are in-process, and the round trips are the ones a careful hand-written read makes.
- **A write costs about 360 µs more than the bare insert.** About 125 µs of it is the audit and the second scope setting:
  the statements alone, sent by hand. The other 233 µs is in-process work and EF's tracked insert (`SaveChanges` where the
  by-hand write sends one raw statement); those two aren't measured apart yet.
- **Round trips dominate.** Each costs about 100–125 µs here (inferred, not measured alone: the one statement the
  audit adds costs about 124 µs), through Docker Desktop's network on macOS; a database on the
  same network as the app pays less for each.
- **Not measured yet:** a subscription's recheck (a read-only unit of work per recheck, every 30 seconds per open
  stream) and jobs (Oxbow's own [benchmarks](https://github.com/rkingon/Oxbow/blob/main/docs/benchmarks.md) cover the
  queue).
