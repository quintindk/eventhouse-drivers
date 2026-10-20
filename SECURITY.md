# Security and qualification

Do not post credentials, configuration, tokens, query data, memory dumps or raw
service/identity-provider logs in public issues. Report a vulnerability through
the repository's private vulnerability reporting facility if enabled. If it is
unavailable, obtain a private contact channel from the maintainer before sending
sensitive details. Never create a public issue containing the evidence.

## Trust boundaries

- Only trusted applications/configuration and valid native caller storage are
  admitted. Pointer checks cannot make hostile unreadable native pointers safe.
- HTTPS Fabric DNS suffix validation is not a tenant/workspace allowlist. The
  application must choose its approved host/database. No TDS, URI credentials,
  arbitrary redirect or disabled certificate validation.
- Authentication uses a fixed public authority/resource policy. Metadata resource
  and token audience checks do not replace cryptographic token validation by the
  identity/service platform.
- Native libraries execute code before ABI checks. Use trusted version-specific
  files, private ACLs, canonical absolute native paths and a private JNA temporary
  directory. Retain the native library for the process lifetime. Stop the JVM
  before replacement, removal or rollback. Unloading/reloading is unsupported.
- Managed/Java strings and SDK caches cannot be securely erased. Dumps,
  instrumentation and application logs may contain credentials. Do not enable
  tracing, transcription, profiler hooks or raw HTTP diagnostics in live CI.
- A complete wire response is not independent proof that the service returned all
  stored rows. Present partial/truncation/failure markers must reject the result.

## CI policy

The sole workflow is manual, main-only, exact-SHA, owner-only including reruns,
using native Windows architectures and a protected environment. Only the live
step receives the five named configuration secrets. No untrusted fork/PR code,
cross-workflow artefacts, cache or user-supplied ref may enter a secret-bearing
job. Required environment approval and main-only branch policy must be configured
before execution. Qualification has read-only repository permissions.

Public workflow logs and artefacts are public content. Child output is captured
and reduced to closed diagnostic records. No endpoint, database, tenant/client ID,
token, connection string or row value may be emitted. Uploads and release creation
are deliberately absent from this preparation. A scanner result is not assurance
that arbitrary future output is safe.

## Retained release gates

All are required against the final source and exact package hashes:

1. Linux ARM64 product NativeAOT and Java 21/25 affected direct/live tests.
2. Native Windows x64 and ARM64 Java 21/25 extracted-package tests, including
   unrestricted results, known values, query rejection, cancellation, timeout,
   cleanup and invalid-secret/reconnect.
3. Clean Windows **without installed modern .NET**, package installation, restart,
   side-by-side upgrade and rollback. Bundled .NET Framework does not establish a
   modern .NET dependency. No destructive runtime uninstall is authorised.
4. Exact public-source and final-package secret/deployment-metadata scans,
   including nested archives, dependency/advisory checks, threat-model review,
   complete required notices, SBOM and honest provenance/native inventory.
5. Independent source/candidate validation and final release-owner approval.

Static PE imports, absent CLR header, controlled environment and application
module inventories are supporting dependency evidence only. A hosted runner with
modern .NET installed cannot pass the literal clean-environment gate. Hiding
`dotnet` or setting an unavailable `DOTNET_ROOT` does not prove absence or prevent
all runtime resolution. A narrower gate requires an explicit reviewed change,
never silent substitution. Unknown/skipped mandatory checks block release.

Unsigned experimental packaging is not authenticated publisher identity.
Checksums establish byte integrity only. No signing is claimed. No broad stable
support, resource-envelope or original-consumer compatibility is established.
