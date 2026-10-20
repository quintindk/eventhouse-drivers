# RC bounded cursor contract, candidate ABI 0x00010003

Experimental product contract. One HTTP query response,
incremental parse and one current row; no paging token, repeated SQL, wholebody
buffer, diskspool or prefetchqueue. All16exports required, exactversion. Existing14
signatures retained plus next/currentcell:

Complete-result request amendment the current RC: product requests use documented
Options.notruncation=true alongside SQL/read-only options, without SQL rewriting
or conflicting truncation options. Local memory guards/deadlines remain. Service
errors still fail, no completeness assumed merely from requesting notruncation.

```
uint32_t eh_result_next_v1(uint64_t result, uint8_t *has_row);
uint32_t eh_result_current_cell_v1(uint64_t result, size_t column,
 uint32_t *type, uint8_t *is_null, uint8_t *value, size_t capacity, size_t *required);
```

## Fetch and completion

Execute returns cursor when Table_0 schema validated and Rows starts, before first
row. Shape has knowncolumncount, rowcountSIZE_MAX until successfulEOF then exact
deliveredcount. Legacy indexedcell rejectsINVALID_STATE for cursor, no rowhistory.
Column metadata noI/O. Next invalidates oldrow and reads exactlyone complete row,
successhas_row1. Currentcell repeated/sizing calls copycallerbuffers withoutI/O.
Beforefirst/afterEOF currentcell rejectsINVALID_STATE. Afterterminalfailure current
cell returns the same latched failure status as next, including8cancel/10timeout;
it never advances or performsHTTP. This refines the candidate10003contract to
preserve authoritative terminalerrors through getters. Nextsuccesshas_row0
requires validation of ALL trailing tables, JSONclosures and physicalHTTP EOF.
RepeatednextafterEOF returnsOK0; latchedfailure rethrowssamestatusfixedreason,
never falseEOF. Type10rawJSON policy retained. No rowdata logs.

Require tableName and completeColumns beforeRows; eithername/columnsorderallowed.
Rejectrowsbeforeheader fixedreason. Tablefields exactexistingname/columns/rows,
duplicate/unknown/root/widthchecks retained. Auxiliarytables consumedincrementally,
recognisedschemas only, validates existingQueryStatus rows (severity>2,status0),
failed/empty status fails even after deliveredrows. Preserve existing primary-only
response acceptance for now: absenceQueryStatus is not newly classifiedasfailure.
SuccessfulEOF means valid completewire plusallpresentservicestatussuccessful,
not independent proofserviceprovidedallstoredrows. Explicit partial/truncation
indicators or errorobjects mustfail; no known truncation marker may be ignored.
Actualservicepayloadshape compatibility remains required in localqueries.

## Memory and bounds (chosen product safeguards, not measured guarantees)

Inputwindow128KiB, boundedtoken spill for largerJSONtokens, maxcell4MiB bothrawJSON
andreturnedUTF8, maxcurrentrow8MiB aggregate raw and returnedcellbytes, metadata
256KiB(primaryheader plusallauxiliarytables), max4096columns, nesting64.
Alllimits enforce BEFOREunboundedallocation. Onexcessstatus7 closedreasoncell_limit,
row_limit,metadata_limit,column_limit,depth_limit. Fixedcapacities boundedlookahead
accounted for. No totalresponsebytecap onqueryresponse, authmetadatacap retained.
Currentrow/token/cellscratch/schema boundedindependentlyoftotalrows. No128KiBtotal
processmemory/RSS/leakfreedomclaim. Java4MiBvalueguard retained. Countoverflowfails.
Utf8JsonReaderstate reused acrosschunks; dynamic raw spansaccumulatedwithinbounds
includingwhitespace/escapes/keyorder; neverholdreader/spanacrossawait. No responseDOM.

## Ownership and cancellation

OperationPrepared->Executing->Streaming->Finished(status). Cursorpublication not
terminalsuccess. Deadline startsatexecute and continues throughfetch includingidle
consumer time; no pernextreset. Terminalarbitration unchanged monotonic checks.
Beforeeachrow/EOFcommit checkcancel/deadline. AftervalidatedEOFcancelnoop; beforeEOF
cancel mayinvalidate currentrow and stopsfetch, copiedcallerbytescannotberevoked.
Cursor ownsresponse/stream/parser plusoperationlease. Publicoperationtoken owned
bycaller. Resultcloseabandonsearlyresponse, cancelsunfinishedop, releasesafter
admittedcallsdrain, doesnotclosepublicoptoken. Operationclose invalidates/cancels,
cursorremainsfailureobservable; connectionbusy untilopclosedandallleasesdrain.
Idlecancel/timeoutdetach/disposeHTTPresources withoutrequiringnextfetch. Onecursor
datacallatatime BUSYonconcurrentfetch/getter; close/cancelindependentlyadmissible.
Registrytokenremoval atomicagainstleases, noI/O/dispose/callbacksunderlocks.
eh_execute wrapsprivateop usingconnectiontimeout, resultownsprivateopclose.

On terminalEOF orerror, detachHTTP/parser/currentrow resources and releasecursor
operationlease EXACTLYONCE afteradmittedcallleasesdrain, WITHOUTrequiringresult
close. Publicoperationstillneedsclosebeforeconnectionreservationcanrelease.
Terminalresultretains boundedimmutablemetadata, deliveredcount andterminalstatus
only: metadata/shape/repeatedEOF orlatchedfailure remaincallable; currentcellfails
withterminalfailurestatus, orINVALID_STATE forsuccessfulEOF.
Earlyclose uses sameonce-onlydetach path. EOFresultleftopen mustnotkeepconnection
busyafteroperationretirement. Sameafterfailedfetch. ReserveSIZE_MAX sentinel:
deliveredcountoverflowintoSIZE_MAX fails ratherthanambiguouslyreportingunknown.

JDBCtransferoperationownershiptoRows; keepactiveoperation/connectionreservation
untilEOF/error/close. Nextnativefetch OUTSIDE sessionmonitor likeexecute; getters
currentcellnotindexedhistory. OnEOF/failureclose retireexactop drainsignals;
parentclose marksclosing, cancels, abandonsresult, waitsoutside locks thenretires.
Do NOTwaitforactiveopbeforeclosingresult: retainedstreamwoulddeadlock. Onlateerror
throwSQLexception notreturnfalse. Existingtimeout/cancel/statusmaps unchanged.

## Required verification

ExistingnativeandJava directtests: splitUTF8/token/escapes/dynamic, generated>4MiB
stream withoutwholebodytestallocation, bound/overbound,cursoronecurrentrow,
latequerystatus/invalidtrailer/transportcut/errorobjects, earlyclose/cancelfetch/
idledeadline/closefetch races/recovery. ActualLinuxARM64Java21/25unrestricted
SELECT*transactions throughEOF mandatory; noTOP substitute. Then native Windows ARM64 and x64 qualification. No new apparatus/framework or resourcecampaign.
