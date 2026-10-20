# RC operation control

Java recovery admission amendment the current RC: nonblocking native operation close is
not a cleanup-drain acknowledgement. Java may wait up to5s outside session lock
for prepare/BUSY ONLY after its own retired operation, with no SQL dispatch and
no blanketretry. Concurrent execution still rejects; parentclose/cancel stops
admission. The admission, deadline and cleanup requirements follow below.
For this path prepare runs OUTSIDE monitor after reservingdistinctJavaadmission,
superseding older prepare-under-lock wording below. Publishonlysameuncancelled
admission/parentsopen; otherwisecloseunpublishedhandlewithoutSQL. Drain5sdeadline
startsatreservation, neverreset/checkagainbeforepublish; latehandleclosedHY010/12.

ABI0x00010003 override: streaming-results.md supersedes all older statements below
makingexecute terminalsuccess/protectingpublishedcursorfromcancel/closingopinside
executefinally. Cursorpublication is NONTERMINAL; deadline/cancelcontinuesfetch,
RowsownsoperationuntilEOF/error/close. Nonstreaminghistoricalrulesremainhistorical.

Endpoint/error refinement for the current RC: metadata non200/malformed/wrong-resource
remains status4, token authentication failure4, transport5. Query responses on
both legacy and operation execute classify401 as4,403 as13(permission denied),
400 as14(query rejected), other non200 as6. Existing numeric assignments remain
unchanged; legacy HTTP behaviour is intentionally refined, not byte-for-byte
preserved. Java maps13=42501,14=42000,4=28000 with fixed messages. No raw bodies.

Operation rules. Streaming ABI 0x00010003 overrides historical buffered rules below.
The product requires all 16 exports in eventhouse-engine.h/product-abi.md.
Dynamic JSON representation is in dynamic-json.md. No structs/callbacks.

- Prepare validates output storage and zeros it; reserves one connection execution
  slot atomically with connection close and legacy execute. No networking.
  Busy=12. Timeout 0 inherits existing connection timeout; 1..600000 overrides.
  These are existing configuration safeguards, not measured throughput promises.
- Operation is single-use Prepared -> Executing -> Finished. Timeout starts at
  execute admission, covering metadata, token, HTTP, response and parse/commit.
  Invalid SQL consumes the attempt; duplicate execute=11. Failures leave result 0.
- Cancel latches against that operation only. Prepared cancellation prevents any
  network dispatch. Cancel finished/already cancelled live token succeeds, no-op.
  Explicit cancel=8/HY008; deadline=10/HYT00. Legacy execute remains unchanged.
- Cancel, deadline and completion have one immutable terminal decision. Use a
  monotonic deadline, check it at cancel and commit even if timer callback is late.
  Earlier cancellation wins; after deadline choose timeout unless already terminal.
  No cancellation may invalidate a result already transferred on success.
- Operation close invalidates token and requests cancellation, without waiting for
  I/O. Previously admitted calls retain leases; later calls=3. Close before success
  commit suppresses and disposes unpublished result; success before close leaves
  result caller-owned. Native repeated close=3, JDBC repeated close remains OK.
- CTS/timer disposal and reservation release happen only after operation closed
  and all execution/cancel signalling leases drain. Connection close returns busy
  until then; never dispose HttpClient beneath an active call. Apply equivalent
  reservation safety to legacy execute. No lock across I/O, cancellation callbacks,
  disposal or waits. Token exhaustion fails instead of recycling stale tokens.
- Cancellation is cooperative local termination, not proof of remote query abort.
  Never replay a dispatched query. Check token around synchronous parsing and
  before commit, but do not claim parsing is pre-emptively interruptible.
- SQL bytes borrowed only during execute, copied before async use. Results and
  UTF-8 buffers retain existing ownership rules. Arbitrary unreadable pointers
  remain outside the safe-host boundary. Exceptions never escape C boundary.
  Only fixed sanitised diagnostics, never request/service/credential text.
- JDBC: short session lock prepares and publishes active handle before releasing
  lock for execute. cancel snapshots exact handle under short lock, calls native
  outside it. Finish clears only matching handle and publishes result only if
  parents remain open, otherwise closes result. Always close operation in finally.
  No active operation means cancel no-op, never sticky cancellation of next query.
  Java retains a caller-side signalling reservation from snapshot through native
  cancel return. Execution retirement/operation close waits for these Java signal
  reservations outside locks. If a captured token was nevertheless retired by
  concurrent close, status 3 is a JDBC no-op only after checking retirement of that
  exact operation; never suppress unrelated handle errors or retarget to B.
  Reserve connection during execute; reject concurrent execute rather than queue.
- JDBC setQueryTimeout accepts 0 (no statement override, connection ceiling still
  applies), 1..600 seconds; negative/greater rejects HY092. getQueryTimeout returns
  configured seconds. Timeout uses SQLTimeoutException/HYT00, cancel HY008.
  Statement/connection close requests active cancellation then waits outside locks
  for execution cleanup before native connection close. Do not block cancel behind
  execute. Same-thread close with no execution is immediate/idempotent.

Verification belongs alongside actual engine/driver tests, using existing injected
HTTP/token providers and bounded barriers. Required cases: cancel before dispatch,
during metadata/token/read; cancel/timeout/success orderings; operation close and
result ownership; stale/wrong-kind/duplicate calls; connection busy and recovery;
delayed cancel A cannot affect B; Java cancel reaches blocked native execute.
Include the precise Java gap: pause after snapshot A before native admission,
finish A and attempt B, resume cancel; no spurious invalid-handle exception,
no cancellation of B and no leaked signalling/execution reservation.
Windows published-DLL and Java21/25 execution remain required, not Linux substitutes.
