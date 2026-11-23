# Performance

Back to the [overview](../README.md#documentation).

[perf/BehaviorLock.LoadTest](../perf/BehaviorLock.LoadTest) measures what the guard costs. It starts the sample
API four ways on real Kestrel ports (without the package, and in `Off`, `Audit` and `Enforce` mode) and
sends the same load to each one.

```
dotnet run -c Release --project perf/BehaviorLock.LoadTest
dotnet run -c Release --project perf/BehaviorLock.LoadTest -- --filter preview --rounds 8
```

Results from one machine (16 logical cores, Windows 11, .NET 11, local SQLite file, 16 connections,
8 rounds of 4 s), with the caller check and the limits on. Throughput in `Enforce` mode, compared with the
same API without the package:

| Scenario | Requests/s without the package | Change in `Enforce` mode |
|---|---:|---|
| No resource access | 65,800 | About 4% lower |
| One EF Core query | 16,500 | No clear difference (the median is +1%, the rounds go from -20% to +5%) |
| One raw SQL query | 18,200 | No clear difference (the median is -3%, the rounds go from -38% to +47%) |
| EF Core query + `UPDATE`, one connection | 440 | About 3% lower (the rounds go from -6% to +4%) |
| One outbound HTTP call | 61,900 | About 4% lower |
| SQL injection, blocked with a 403 | 16,500 | No clear difference (the median is +5%, the rounds go from -2% to +8%) |

The latency percentiles (p50, p95, p99) differ by 0.3 ms or less in every scenario.

The guard keeps the shapes of the SQL command texts that passed. An app sends the same few texts again and
again, so most commands are not normalized:

| Work for one SQL command | Time | Allocated |
|---|---:|---:|
| A text that the guard knows: one lookup and one check | About 0.4 µs | 216 bytes |
| A new text: normalize it first | 0.3 to 0.6 µs more | About 770 bytes more |

- The row check at the end of a command uses the same kept shape.
- The cache keeps at most `SqlShapeCacheSize` texts (default 1000) and about 4 MB of text. When it is full, it
  starts again from empty. A text of more than 20,000 characters is never kept.
- In `Enforce` mode a blocked command is never kept, so the texts of an attacker do not fill the cache.
  In `Audit` mode they can fill it. That costs time, not safety: the texts of the app are normalized again.
- A known text is still checked for each request. The cache only saves the work of normalizing.

How to read these numbers:

- The machine did other work during this run, so the requests per second are low and the SQL rounds differ
  much. Read the relative change, not the absolute numbers.
- With a size limit, the response body passes through one more writer. With the caller check, each request
  makes one more lookup in the profile.
- A local SQLite file answers in about 0.2 ms. That makes the relative cost of the guard as high as it gets.
  A database on the network is slower, so the same cost is a smaller share of the request.
- In `Off` mode, and for code outside a request and outside a scope, the guard skips its work: the SQL is not normalized and
  the outbound call is not inspected. Only the call into the EF Core interceptor and the HTTP handler remains.
- The load test answers outbound calls with a stub handler, so it does not measure the check of the .NET
  handlers. In a separate test with a real local call (40 to 70 µs per call), the check made no
  difference that the test could measure.
- The load client runs in the same process as the servers, and the machine speed changes during a run.
  The test compares each variant with the baseline run of the same round to reduce that effect.
  Differences below about 1% are not reliable.
