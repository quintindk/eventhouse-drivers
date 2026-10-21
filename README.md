# Eventhouse JDBC driver

Experimental read-only JDBC connectivity to Microsoft Fabric Eventhouse through
its **HTTPS Query URI**, never TDS. Intended RC version: `0.1.0-rc.1`.

**Experimental RC1 candidate: final review and publication scans PASS.** The exact
Windows x64 and ARM64 packages passed the retained Java 21/25 workload and the
human-approved bounded runtime, installation, restart and removal checks.
`release_ready` is `true` under existing human publication authority. The
coordinator must verify the final identities before commit or publication.
No release has been published by this preparation.
See [RC1 qualification](docs/RC1-QUALIFICATION.md) for exact hashes and scope.
Rollback is deferred and **NOT QUALIFIED**.

## Architecture and scope

Java application -> JDBC JAR -> JNA -> .NET 10 NativeAOT native library -> HTTPS.
Java is compiled with `--release 21`. Product C ABI `0x00010003` has 16 required
`eh_*` exports. Additional runtime/native exports are inventoried, not public API.
Default engine builds are the historical spike profile with test hooks. **Always
pass `-p:EventhouseProduct=true` for product builds** and isolate build outputs.

The original source paths and internal class names are retained to minimise build
changes. They are not additional supported products. ODBC and its MSI installer
are separate future work and do not block JDBC packaging.

## Supported experimental behaviour

- Explicit caller-supplied Fabric host/database and service-principal credentials.
  There is no deployment host default. `EVENTHOUSE_QUERY_HOST` is mandatory for
  live test callers. Synthetic hosts in offline tests are not live destinations.
- JDBC SPI discovery, read-only forward-only results, indexed/named getters and
  result metadata, deterministic unsupported-feature errors.
- Boolean, signed integers, double, decimal, UTC datetime, strings, nulls and
  dynamic JSON. Dynamic values return raw JSON text. JSON string `"null"` remains
  different from wire null. Wire JSON null and SQL null cannot be distinguished.
- Incremental consumption of unrestricted results through validated physical EOF
  and all present service status tables. No automatic SQL replay, TOP rewrite,
  invented continuation token, whole-result buffer or disk spool.
- Cancellation and deadlines terminate local cursor work with cleanup/recovery.
  Cancellation does not establish remote query abort. Deadlines include idle
  consumer time, not a new allowance on every fetch.

## Limits and exclusions

Chosen guards: 16 KiB query, 128 KiB input window, 4 MiB cell, 8 MiB current row,
256 KiB metadata, 4096 columns and nesting depth 64. Authentication metadata has
its own 4 MiB cap. Query results have no total-body cap. These are **not** total
process-memory, RSS, concurrency, throughput, soak or leak-freedom guarantees.

Prepared statements, transactions, updates/ingestion, batch execution, catalogue
discovery and hot native reload are unsupported. No compatibility claim is made
for an unavailable third-party application. Windows x86 and emulated architecture
qualification are out of scope. Linux ARM64 is a local verification route, not a
supported release package or a substitute for Windows testing.

Connection creation is lazy. Query token acquisition uses
`Azure.Identity.ClientSecretCredential`; caching/renewal are SDK-owned. No
qualified transparent-refresh or long-running-session guarantee is claimed.
Invalid-secret/reconnect tests are required. Forced expiry, bespoke negative TLS
and live oversized-expression campaigns are deferred, not passed. Direct cell
bound/cleanup/recovery and TLS/no-redirect configuration tests remain required.

## Build, installation and security

- [Build, installation and removal](docs/BUILD-INSTALL.md)
- [RC1 qualification and limitations](docs/RC1-QUALIFICATION.md)
- [Security and release gates](SECURITY.md)
- [Product ABI](contracts/product-abi.md)
- [Streaming and lifetime rules](contracts/streaming-results.md)

Project-owned source: MIT, Copyright (c) 2026 Quintin De Kok. JNA's Apache-2.0
alternative is elected. Dependencies retain their own licences. Full collected
texts and pinned mappings are in `licences/` and
`infra/release/notice-materials.json`. Independent candidate review accepted the
conservative notice scope and runtime SBOM coverage. This is not exact upstream
native-binary attestation or a guarantee of no vulnerabilities. No stable support
commitment or Microsoft product support is claimed.
