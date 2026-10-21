# RC1 qualification assessment

## Disposition and immutable identities

Experimental JDBC `0.1.0-rc.1`, C ABI `0x00010003` (16 required exports).
Retained checks PASS within the human-approved minimal current-build runtime,
installation, restart and removal boundary. Release authority is confirmed.
**`release_ready: true`: independent final review and fresh publication scans
PASS.** The ready transition uses existing human publication authority. Final
coordinator identity verification precedes commit/publication. This assessment
does not publish the release or approve its own review.

- Product source: `73d757e9f73f8d65fc4dc37997313cc47a66a260`.
- Later runtime/install evidence source: `3181d29dc277390b8a5e5067b364172ed5d30570`.
- These are different source roles. Documentation reconstruction does not change
  the product identity or overwrite historical review/source-version references.

| Exact candidate file | SHA-256 |
| --- | --- |
| `eventhouse-jdbc-0.1.0-rc.1-win-x64.zip` | `392108ea823fa7f2e1abce783faf84c674b45f769f3716fedfbacada306a19a5` |
| `eventhouse-jdbc-0.1.0-rc.1-win-arm64.zip` | `fdbdf222a157f4aa19e6b7ebb0a8a8014d8642d97a25955bd7cc851124395e96` |
| Shared `eventhouse-jdbc-0.1.0.jar` | `3a6cce5cf7e41e879effbf9c3a4019b66edfaec2369465ef0b5f14564dd7416d` |
| x64 `eventhouse-engine.dll` | `212e1068860273ba1d7c01134e2295408eee7486cdd699cb8f73c6fe3c7ae29a` |
| ARM64 `eventhouse-engine.dll` | `fb0dfc356d7a63d8b03ddcfadf7dde4f64f663c86ba71ff1453ce43e5be232e7` |

No rebuild, repack, signing or product-byte changes. The five publication assets
are exactly the two ZIPs above, `CHECKSUMS.sha256`, `qualification.json` and
`release-notes.md`. The outer checksum file covers the other four assets and
excludes itself. SBOM, notices and build provenance remain inside each ZIP.

## Retained execution

Native Windows x64 and ARM64, Microsoft Java 21.0.11 and 25.0.3, Java compilation
with `--release 21`. Original product Build/Live/Verify and custody steps passed.
Original final gates failed under their then-retained rules and are not relabelled.

| Evidence scope | Result |
| --- | --- |
| Managed/native contract per architecture | 44 managed, 1 native PASS |
| Product Java checks per architecture/JVM | 1701 direct, 1706 native-inclusive PASS |
| Unrestricted query per architecture/JVM | 321771 rows, 28 columns, 200 nulls, 197008507 returned UTF-8 bytes, valid physical EOF PASS |
| Retained live checks per architecture/JVM | 45 base, 7 cancellation, 7 idle deadline, 6 invalid-secret/reconnect PASS |
| Later evidence-source offline checks per architecture/JVM | 1832 direct, 1837 native-inclusive PASS, not a replacement product build |
| Current-build runtime per architecture/JVM | 65 assertions PASS |
| Current-build restart per architecture/JVM | 45 assertions PASS |
| Current-build removal | All four architecture/JVM combinations PASS |
| Recovery integrity | 850 static verification checks PASS, no new execution campaign |

### Approved runtime proof, not literal runtime absence

Human approval replaced the literal clean-Windows-without-installed-modern-.NET
criterion with checkpoint proof for this minimal RC. Static inspection found no
external runtime imports and an absent CLR directory in both product DLLs.
Eight named checkpoints on each of four architecture/JVM combinations, 32 total,
found zero external modern .NET modules. Each checkpoint used two enumeration
passes. The target DLL was absent before loading and present thereafter.

Modern .NET was installed on both runners (`true`). Transient load absence is
not proven (`false`). This is not a clean runtime-free OS claim or continuous
module monitoring. Current-build install/restart/remove passed. Side-by-side
upgrade and rollback remain **DEFERRED / NOT QUALIFIED**.

Runtime child exit 2 denotes the separately deferred overcell case in the reviewed
closed diagnostic protocol, not a failed retained runtime check. Restart children
exited 0. Deferred tests are not counted as passes.

## Independent security and distribution assessment

The recovered independent candidate review records:

- 2266 inventory checks PASS, zero failures. Each ZIP has 139 payload files,
  138 checksum entries and 137 provenance hash entries.
- MIT project licence, 72 notice materials and 20 licence mappings, 57 SBOM
  components, 58 dependency records and 28 JNA member hashes accepted within
  conservative complete-text and restore/build/runtime inventory scope.
- Four pinned Gitleaks 8.30.1 scans, exit 0 and zero findings, independently
  accepted for the exact payload/custody. Public deployment metadata absent.
- Threat-model review, candidate/evidence review and point-in-time dependency
  gate PASS. Thirty coordinate queries succeeded with zero matches. Reviewed
  Brotli advisories were NOT APPLICABLE, not waived. Prebuilt JNA native versions
  remain unknown. No zero-vulnerability or exact linked upstream-binary claim.

Original temporary scanner output is lost. Retained security evidence is the
recovered independent review summary plus successful original content/custody
receipts, not restored scanner reports. A fresh scan of reconstructed publication
content now PASSes Gitleaks 8.30.1 scans of all-ref public Git history, the complete
source snapshot and all five assets including nested archives. Each scan returned
exit 0 and zero findings, with default rules, no suppression and 100% redaction.
The official Linux ARM64 archive SHA-256 is
`e4a487ee7ccd7d3a7f7ec08657610aa3606637dab924210b3aee62570fb4b080`,
verified against official checksums and the GitHub asset digest. Scanner binary
SHA-256 is `00e91bbe655bd7c47753e8cfe61cb76ea1a5d7e7702fe161ee40102b46b3823b`.
Additional assets-only destination/editorial checks passed, including nested
members. The exact public authentication resource and accepted assembly label
are not private destination or editorial data. Default scanner rules are not
suppressed. Final scan receipts and exact file hashes are external to avoid
self-referential qualification/checksum hashes.

## Historical records and supersession

Original ZIP README/security/build documents and provenance retain build-time
blocked or pending states. Original final-gate failures and review references
remain intact. This external hash-bound assessment supersedes the specific
notice/security/inventory pending conclusions using independent review, and the
literal runtime/install requirement using explicit human-approved checkpoint
proof and later install evidence. It does not rewrite history or assert that
the old automated final gate passed. Independent final publication review PASS
was conditional on fresh scans and the now-completed ready transition.

The DLL assembly label `eventhouse-connectivity-wp003` is accepted non-secret
build metadata, not user data. It does not justify changing the tested binaries.

## Exclusions and operation

Unsigned experimental JDBC only. No ODBC DLL/MSI is shipped. No stable support,
original-consumer compatibility, remote query abort, transparent token refresh,
total-process memory envelope, soak or leak-freedom claim. Live oversized-cell,
forced-expiry and bespoke negative-TLS campaigns remain deferred, not passed.
Project source is MIT, Copyright (c) 2026 Quintin De Kok. Dependencies retain
their licences. See [installation and removal](BUILD-INSTALL.md) and
[security](../SECURITY.md). Stop the JVM before replacement or removal.
