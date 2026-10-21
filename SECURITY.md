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
token, connection string or row value may be emitted. The workflow uploads only
projected public evidence, not candidate ZIPs. Release creation is absent. A
scanner result is not assurance that arbitrary future output is safe.

## RC1 acceptance boundary

The external [RC1 qualification assessment](docs/RC1-QUALIFICATION.md) binds the
unchanged candidate hashes to the retained evidence. Human approval replaced the
literal clean-Windows-without-installed-modern-.NET criterion with bounded
checkpoint proof for current-build runtime, installation, restart and removal.
It did not establish runtime absence or qualify rollback.

1. Linux ARM64 product NativeAOT and Java 21/25 affected direct/live tests.
2. Native Windows x64 and ARM64 Java 21/25 extracted-package tests, including
   unrestricted results, known values, query rejection, cancellation, timeout,
   cleanup and invalid-secret/reconnect.
3. Current-build installation, restart and removal on both architectures/JVMs,
   static import inspection and 32 runtime checkpoints with zero external modern
   .NET modules. Modern .NET was installed on both runners. Transient load absence
   between checkpoints is not proven. Upgrade and rollback are NOT QUALIFIED.
4. Exact public-source and final-package secret/deployment-metadata scans,
   including nested archives, dependency/advisory checks, threat-model review,
   complete required notices, SBOM and honest provenance/native inventory.
5. Independent candidate validation and final review of reconstructed public
   documentation and the five-asset publication set PASS. Fresh publication scans
   PASS. Existing human release authority permits the ready transition, not an
   automatic commit or publication. Coordinator verification remains required.

The recovered independent review accepted licence/notices, runtime SBOM coverage,
secret scans, threat model and point-in-time dependency applicability for these
exact candidates. It records four Gitleaks 8.30.1 scans with exit 0 and zero
findings. Original temporary scanner outputs are lost. The recovered review
summary and successful content/custody receipts are the retained evidence, not
recreated scan reports. Fresh Gitleaks 8.30.1 publication scans now PASS, separately
from those historical records. The official Linux ARM64 archive was verified
against its official checksum and GitHub asset digest. Default rules were extended
without suppression, redaction was 100%, and nested archives were scanned.
Public Git history, the complete final source snapshot and all five publication
assets each returned exit 0 with zero findings. Additional assets-only private
destination and editorial checks passed. External receipts bind the final hashes
without embedding a self-referential qualification or checksum hash.

Historical ZIP documentation, provenance and failed final-gate records are
preserved, not edited into passes. The external assessment supersedes their
specified build-time pending conclusions only. Prebuilt JNA native versions
remain unknown. Dependency review is not a zero-vulnerability guarantee.

Unsigned experimental packaging is not authenticated publisher identity.
Checksums establish byte integrity only. No signing is claimed. No broad stable
support, resource-envelope or original-consumer compatibility is established.
