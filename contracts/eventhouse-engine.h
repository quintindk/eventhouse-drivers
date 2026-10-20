#ifndef EVENTHOUSE_ENGINE_H
#define EVENTHOUSE_ENGINE_H
#include <stddef.h>
#include <stdint.h>
#ifdef _WIN32
#define EH_CALL __cdecl
#else
#define EH_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif
/* Experimental product ABI. All 16 symbols required, exact version before open.
 * uint64_t values are opaque tokens, not pointers. All calls are cdecl.
 * State/ownership: operation-control.md; manifest: product-abi.md. */
#define EH_ABI_VERSION UINT32_C(0x00010003)
#define EH_MAX_QUERY_BYTES ((size_t)16384)
/* Query total response has no body cap; bounded cursor guards in streaming-results.md.
 * Authentication metadata retains independent 4MiB cap. */
#define EH_MAX_CELL_BYTES ((size_t)4194304)
typedef uint64_t eh_connection;
typedef uint64_t eh_operation;
typedef uint64_t eh_result;
typedef uint32_t eh_status;
enum eh_status_code {
    EH_OK=0, EH_INVALID_ARGUMENT=1, EH_BUFFER_TOO_SMALL=2, EH_INVALID_HANDLE=3,
    EH_AUTHENTICATION_FAILED=4, EH_TRANSPORT_FAILED=5, EH_HTTP_FAILED=6,
    EH_PROTOCOL_FAILED=7, EH_CANCELLED=8, EH_INTERNAL_ERROR=9, EH_TIMEOUT=10,
    EH_INVALID_STATE=11, EH_BUSY=12, EH_PERMISSION_DENIED=13, EH_QUERY_REJECTED=14
};
enum eh_cell_type {
    EH_TYPE_UNKNOWN=0, EH_TYPE_BOOLEAN=1, EH_TYPE_INT32=2, EH_TYPE_INT64=3,
    EH_TYPE_DOUBLE=4, EH_TYPE_DECIMAL=5, EH_TYPE_DATETIME=6,
    EH_TYPE_TIMESPAN=7, EH_TYPE_GUID=8, EH_TYPE_STRING=9, EH_TYPE_DYNAMIC=10
};
/* Borrowed UTF8 byte/length inputs, no NUL required. Config copied on open.
 * size_t is native pointer width; output scalar storage must be valid/aligned.
 * Zero handles invalid; exactly one native close per successful creation.
 * Native repeat close returns INVALID_HANDLE. Results close separately from
 * connections. Operation reservations protect active execution/fetch; see state rules.
 * All column/row indices zero-based. Shape returns columns then rows.
 * Shape rows=SIZE_MAX before validatedEOF, exactdeliveredcount afterwards.
 * Indexed result_cell rejects INVALID_STATE for cursorresults; use current_cell.
 * Column/cell/error buffers caller-owned. Sizing returns BUFFER_TOO_SMALL and
 * exact required bytes; NULL,0 succeeds for zero bytes. Outputs not NUL terminated.
 * SQL null and empty text both length0, is_null distinguishes them. Type flags
 * nullable/is_null are uint8 values0/1. Null returns0bytes,is_null=1. Every JSON
 * string in non-dynamic columns returns decoded UTF8 text; other non-null
 * values retain JSON lexical text. Dynamic always retains JSON lexical text,
 * including string quotes/escapes. See dynamic-json.md. Deferred
 * timespan/guid/dynamic are enumerated, not supported JDBC conversion promises.
 * Thread-local last errors fixed/sanitised. Version/error getters preserve them;
 * ordinary calls clear then set on failure. Buffer sizing need not set error.
 * Never unload NativeAOT library: retain for process lifetime. No arbitrary
 * unreadable-pointer safety guarantee. Never replay dispatched requests. */
uint32_t EH_CALL eh_abi_version_v1(void);
eh_status EH_CALL eh_open_v1(const uint8_t *query_uri, size_t query_uri_bytes,
    const uint8_t *database, size_t database_bytes,
    const uint8_t *tenant, size_t tenant_bytes,
    const uint8_t *client, size_t client_bytes,
    const uint8_t *secret, size_t secret_bytes,
    uint32_t timeout_ms, eh_connection *out_connection);
eh_status EH_CALL eh_execute_v1(eh_connection connection,
    const uint8_t *sql, size_t sql_bytes, eh_result *out_result);
eh_status EH_CALL eh_result_shape_v1(eh_result result, size_t *columns, size_t *rows);
eh_status EH_CALL eh_result_column_v1(eh_result result, size_t column,
    uint32_t *type, uint8_t *nullable, uint8_t *name, size_t capacity, size_t *required);
eh_status EH_CALL eh_result_cell_v1(eh_result result, size_t row, size_t column,
    uint32_t *type, uint8_t *is_null, uint8_t *value, size_t capacity, size_t *required);
eh_status EH_CALL eh_connection_close_v1(eh_connection connection);
eh_status EH_CALL eh_result_close_v1(eh_result result);
uint32_t EH_CALL eh_error_code_v1(void);
eh_status EH_CALL eh_error_message_v1(uint8_t *message, size_t capacity, size_t *required);
/* Existing statuses 0..9 unchanged. New calls: explicit cancel=8, timeout=10,
 * invalid state=11, busy=12. Legacy execute retains combined cancel/timeout=8.
 * Query HTTP403=13(permission denied), HTTP400=14(query rejected), HTTP401=4.
 * Metadata HTTP rejection remains4. See operation-control.md for stage scope. */
eh_status EH_CALL eh_operation_prepare_v1(eh_connection connection,
    uint32_t timeout_ms, eh_operation *out_operation);
eh_status EH_CALL eh_operation_execute_v1(eh_operation operation,
    const uint8_t *sql_utf8, size_t sql_bytes, eh_result *out_result);
eh_status EH_CALL eh_operation_cancel_v1(eh_operation operation);
eh_status EH_CALL eh_operation_close_v1(eh_operation operation);
eh_status EH_CALL eh_result_next_v1(eh_result result, uint8_t *has_row);
eh_status EH_CALL eh_result_current_cell_v1(eh_result result, size_t column,
    uint32_t *type, uint8_t *is_null, uint8_t *value, size_t capacity, size_t *required);
#ifdef __cplusplus
}
#endif
#endif
