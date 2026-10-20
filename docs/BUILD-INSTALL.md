# Build, installation and rollback

## Prerequisites and status

No release binaries ship with this source. Do not run live checks before the
exact source is reviewed and affected Linux ARM64 Java 21/25 checks pass. Windows
qualification remains separate and must use native x64 and ARM64 OS/process/JDK
architectures. No emulation or Windows x86 substitution.

Pinned inputs:

- .NET SDK 10.0.113, `global.json` disables roll-forward. NativeAOT release warnings
  are errors. Runtime/compiler packs resolve through RID locks, currently 10.0.13.
- Native Visual Studio MSVC host/target tools, Windows SDK, `vswhere`, DevShell,
  `cl.exe`, `dumpbin.exe`. Actual tool versions are recorded, not asserted as a
  byte-identical native rebuild guarantee.
- Microsoft JDK 21.0.11 and 25.0.3 on Windows. Compile with Java 21 `--release 21
  -Xlint:all -Werror`. Run the same tests on both JVMs.
- JNA 5.17.0 from Maven Central, SHA-256
  `b3a9408e7c51e08ef0e3bfcc08f443f6ec0f6191ba8cd7c18d53d2b22e5bdbc0`.
- Access to pinned package sources. Locks record dependencies, not an offline
  package cache. No credential is required for the build.

## Windows qualification entry point

The protected manual `.github/workflows/jdbc-windows.yml` selects both native
architectures by default, supplies JDK homes and invokes:

```powershell
./src/jdbc/Run-Windows.ps1 -Phase Build -Runtime win-x64 -Architecture x64
./src/jdbc/Run-Windows.ps1 -Phase Live -Runtime win-x64 -Architecture x64
```

For ARM64 use `win-arm64` and `arm64` on the native ARM64 runner. The script
requires a clean exact `GITHUB_SHA`, `RUNNER_TEMP` outside the checkout and explicit
`EVENTHOUSE_JDK_21`/`EVENTHOUSE_JDK_25` homes. Build and Live use the same staging
directory and verify archive/payload/source hashes before and after live checks.
It is not a general local installer or release-publishing command.

The workflow has no upload or promotion step. Its final `Gate` phase reads the
exact-source live report and blocks on missing or non-pass retained evidence.
Clean-environment, inventory/scans and independent validation remain unimplemented.
Individual Build/Live success describes those routes only,
never overall release readiness. Do not change an expected test count merely to
make CI green. Current direct expectations are managed 44, published native 1,
Java 1701 direct / 1706 native-inclusive on each JVM, including extracted files.
Live retained expectations: base 45, cancellation 7, idle deadline 7,
invalid-secret/reconnect 6. The caller's separate deferred overcell case exits 2;
the strict parser distinguishes that from an unfulfilled retained test.

## Linux ARM64 product lock identity

The default engine profile is a spike. Its managed lock has project identity
`eventhouse.engine.spike` and must not be used for a product restore. The explicit
`tests/contract/wp003/managed/packages.product.linux-arm64.lock.json` has project
identity `eventhouse-engine`. Its origin/hash is recorded in the export manifest.
Do not regenerate a lock silently or pass a managed project lock into the engine.

From the repository root, use an external empty staging directory. Commands below
are a repeatable verification recipe, not a claim that this export has been built:

```bash
root="$PWD"
stage="${TMPDIR:-/tmp}/eventhouse-local-product"
mkdir -p "$stage"
engine="$root/src/engine/Eventhouse.Engine.Spike.csproj"
managed="$root/tests/contract/wp003/managed/Eventhouse.Engine.Spike.ContractTests.csproj"
engine_lock="$root/src/engine/packages.linux-arm64.lock.json"
managed_lock="$root/tests/contract/wp003/managed/packages.product.linux-arm64.lock.json"
dotnet restore "$engine" -r linux-arm64 --locked-mode \
  -p:EventhouseProduct=true -p:NuGetLockFilePath="$engine_lock" \
  -p:UseArtifactsOutput=true -p:ArtifactsPath="$stage/managed"
dotnet restore "$managed" -r linux-arm64 --locked-mode --no-dependencies \
  -p:EventhouseProduct=true -p:NuGetLockFilePath="$managed_lock" \
  -p:UseArtifactsOutput=true -p:ArtifactsPath="$stage/managed"
dotnet run --project "$managed" -c Release -r linux-arm64 --no-restore \
  -p:EventhouseProduct=true -p:NuGetLockFilePath="$managed_lock" \
  -p:UseArtifactsOutput=true -p:ArtifactsPath="$stage/managed" -- --streaming
dotnet publish "$engine" -c Release -r linux-arm64 --self-contained true \
  -p:EventhouseProduct=true -p:RestoreLockedMode=true \
  -p:NuGetLockFilePath="$engine_lock" \
  -p:BaseIntermediateOutputPath="$stage/native-obj/" \
  -p:BaseOutputPath="$stage/native-bin/" -o "$stage/native" \
  -p:TreatWarningsAsErrors=true -p:ILLinkTreatWarningsAsErrors=true \
  -p:IlcTreatWarningsAsErrors=true
dotnet run --project "$managed" -c Release -r linux-arm64 --no-build --no-restore \
  -p:EventhouseProduct=true -p:NuGetLockFilePath="$managed_lock" \
  -p:UseArtifactsOutput=true -p:ArtifactsPath="$stage/managed" \
  -- --native-streaming "$stage/native/eventhouse-engine.so"
```

Build Java from the seven main sources plus the service-provider resource, then
the three test callers. Use the same Java-21-compiled files on both JVMs:

```bash
mkdir -p "$stage/classes" "$stage/test-classes"
curl --fail --silent --show-error --location \
  https://repo.maven.apache.org/maven2/net/java/dev/jna/jna/5.17.0/jna-5.17.0.jar \
  --output "$stage/jna-5.17.0.jar"
printf '%s  %s\n' b3a9408e7c51e08ef0e3bfcc08f443f6ec0f6191ba8cd7c18d53d2b22e5bdbc0 \
  "$stage/jna-5.17.0.jar" | sha256sum --check --status
"$EVENTHOUSE_JDK_21/bin/javac" --release 21 -Xlint:all -Werror \
  -cp "$stage/jna-5.17.0.jar" -d "$stage/classes" \
  src/jdbc/src/main/java/com/microsoft/eventhouse/jdbc/*.java
cp -R src/jdbc/src/main/resources/. "$stage/classes/"
"$EVENTHOUSE_JDK_21/bin/jar" --create --date=2026-10-16T00:00:00Z \
  --file "$stage/eventhouse-jdbc-0.1.0.jar" -C "$stage/classes" .
classpath="$stage/eventhouse-jdbc-0.1.0.jar:$stage/jna-5.17.0.jar"
"$EVENTHOUSE_JDK_21/bin/javac" --release 21 -Xlint:all -Werror \
  -cp "$classpath" -d "$stage/test-classes" \
  src/jdbc/src/test/java/com/microsoft/eventhouse/jdbc/*.java
for jdk in "$EVENTHOUSE_JDK_21" "$EVENTHOUSE_JDK_25"; do
  "$jdk/bin/java" --enable-native-access=ALL-UNNAMED \
    -cp "$stage/test-classes:$classpath" com.microsoft.eventhouse.jdbc.DriverTest \
    "$stage/native/eventhouse-engine.so"
done
```

Run live callers only through a reviewed capturing parent that reduces output to
closed diagnostics and enforces timeouts. Do not use shell tracing or print the
environment. Supply the five private values through inherited configuration:
`EVENTHOUSE_QUERY_HOST`, `EVENTHOUSE_DB_NAME`, `AZURE_TENANT_ID`,
`AZURE_SPN_CLIENT_ID`, `AZURE_SPN_SECRET`. Set `EVENTHOUSE_NATIVE_LIBRARY` to the
absolute tested library path. Test `TransactionsSmoke` without `--top10` and
`RcSmoke --live-failures`. Retain validated EOF and >4194304 returned UTF-8 bytes,
not row values. Their historical fixed protocol markers are retained for parser
compatibility, not exported coordination records.

## Package inventory and provenance

The Windows build includes the driver JAR, exact JNA JAR, architecture-correct
NativeAOT DLL, project MIT licence, notices, these instructions, checksums,
CycloneDX 1.5 inventory and build provenance. No credentials, test classes,
prebuilt historical DLLs or raw logs belong in a package.

The SBOM distinguishes product file hashes, NuGet restore/build inputs and JNA
archive members. It does not invent product-to-every-runtime-pack link edges or
native versions. libffi/Brotli binary attribution and WindowsDesktop aggregation
remain unresolved. The native runtime is compiled into NativeAOT, but exact linked
composition still needs evidence. Fixed-date JAR/ZIP entry timestamps do not prove
byte-identical NativeAOT rebuilds. Provenance is unsigned build metadata, not an
authenticated attestation. Final live results remain separate from build-time
provenance. `checksums.sha256` excludes itself; provenance excludes its own hash
and the checksum manifest. External reports bind the complete ZIP identity.

## Installation, upgrade, rollback and removal

1. Obtain a qualified package and trusted external ZIP SHA-256. Verify the ZIP
   with `Get-FileHash -Algorithm SHA256`, then verify every checksum entry after
   safe extraction. Unsigned hashes do not prove publisher identity.
2. Extract into a private version-specific directory. Select matching native
   Java 21 or 25. Put the driver and `lib/jna-5.17.0.jar` on the classpath. Enable
   native access with `--enable-native-access=ALL-UNNAMED`. Use a private writable
   `jna.tmpdir` and canonical absolute `native/win-<architecture>/eventhouse-engine.dll`.
3. Construct `jdbc:eventhouse://<approved-host>/<encoded-database>` from secure
   configuration. Supply `tenantId`, `clientId`, `clientSecret`, `nativeLibrary`
   and optional `timeoutMillis` as properties, never secret command arguments.
   Default timeout is 30000 ms; accepted configured range is 1..600000 ms.
4. No modern .NET runtime is intended as an application prerequisite. The literal
   runtime-free Windows proof remains outstanding. Build-tool .NET requirements
   are distinct from application-runtime requirements. Do not promise a qualified
   runtime-free installation until that retained gate passes.
5. Upgrade side by side. Stop the JVM, verify the new package, update JAR/JNA/native
   paths together, restart and run approved acceptance queries. Never hot-replace
   a loaded native library.
6. On failure stop the JVM, restore all paths to the prior reviewed version,
   verify its hashes and restart. Verify query and recovery behaviour again.
7. Remove only the owned version directory and JNA cache after JVM exit, plus its
   application classpath/configuration entries. No schema or Driver Manager change.
