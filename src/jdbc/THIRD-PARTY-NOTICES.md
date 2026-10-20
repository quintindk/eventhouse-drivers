# Third-party notices and inventory

Project-owned code: MIT, Copyright (c) 2026 Quintin De Kok. See root `LICENSE`.
Dependencies are not relicensed by the project licence.

## JNA

JNA 5.17.0 (`net.java.dev.jna:jna:5.17.0`) offers Apache-2.0 or LGPL-2.1-or-later.
The project elects **Apache-2.0** and preserves the exact JAR without modification,
its full alternative texts and native notices. Full Apache text, licence selector
and alternative LGPL text are vendored under `licences/restore-inputs/`.
`licences/jna-native-attribution.txt` and `licences/upstream/jna-libffi.txt`
preserve dispatch/libffi attribution. The tagged native source identifies libffi
3.4.4, but that is not an attested version for every prebuilt archive member.
Member hashes must be inventoried from the actual JAR during packaging. Bundled
platform members do not extend the supported Windows x64/ARM64 target matrix.

## .NET and authentication dependencies

The pinned restore graph includes Azure.Core 1.53.0, Azure.Identity 1.21.0,
Microsoft.Identity.Client and Extensions.Msal 4.83.1,
Microsoft.IdentityModel.Abstractions 8.14.0, System.ClientModel 1.10.0,
Microsoft.Bcl.AsyncInterfaces/System.Memory.Data and eight Extensions components
10.0.3, ProtectedData 4.5.0, and runtime/compiler/linker inputs 10.0.13.
Full pinned texts and exact mappings are in `infra/release/notice-materials.json`
at the source/package root.
Original package copyright notices and third-party aggregates are preserved.
ProtectedData's bundled full text establishes MIT despite its legacy licence URL.

Compiler/ILLink packages are build inputs. Downloaded ASP.NET/WindowsDesktop packs
are not established linked dependencies. NETCore/NativeAOT attribution inputs are
not an exact linker composition report. Native exports include runtime/Brotli
symbols in addition to the 16 product exports. Runtime aggregates include the
Brotli permission text and a separate pinned copy is retained. Tagged Brotli 1.1.0
is a source fact, not a verified linked binary version.

## Outstanding distribution gates

Full collected texts do not establish complete legal/security clearance. Exact
native binary attribution/advisory applicability, WindowsDesktop third-party
aggregation or reviewed non-linkage evidence, final package scans and independent
inventory review remain required. A failed retrieval is not included as a notice.
Do not represent a parent-package advisory lookup as native-component clearance.

No historical private package/report or deployment configuration is a public
build input. Every new package must derive its source, binary and notice hashes
from the exact public source/build. JDBC distribution is independent of ODBC MSI.
