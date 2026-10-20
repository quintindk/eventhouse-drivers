# Experimental product ABI 1.3

Experimental product contract. See README.md for qualification status.
Product ABI version 0x00010003, cdecl, exact required exports below. Cursor semantics
and two added signatures in streaming-results.md supersede buffered shape/cell and
execute completion. Dynamic JSON
representation refinement is in dynamic-json.md. These are the
existing reviewed signatures with product names, not new behaviour or structs.
Scalar widths, buffers, handle ownership, operation deadlines and diagnostics
remain as defined by operation-control.md and the corresponding reviewed source.

| Legacy/internal name | Product export |
| --- | --- |
| eh_spike_abi_version_v1 | eh_abi_version_v1 |
| eh_spike_open_v1 | eh_open_v1 |
| eh_spike_execute_v1 | eh_execute_v1 |
| eh_spike_result_shape_v1 | eh_result_shape_v1 |
| eh_spike_result_column_v1 | eh_result_column_v1 |
| eh_spike_result_cell_v1 | eh_result_cell_v1 |
| eh_spike_connection_close_v1 | eh_connection_close_v1 |
| eh_spike_result_close_v1 | eh_result_close_v1 |
| eh_spike_error_code_v1 | eh_error_code_v1 |
| eh_spike_error_message_v1 | eh_error_message_v1 |
| eh_operation_prepare_v1 | eh_operation_prepare_v1 |
| eh_operation_execute_v1 | eh_operation_execute_v1 |
| eh_operation_cancel_v1 | eh_operation_cancel_v1 |
| eh_operation_close_v1 | eh_operation_close_v1 |
| new cursor | eh_result_next_v1 |
| new current row | eh_result_current_cell_v1 |

16 required product exports. Additional runtime/native exports are not product API.
Product build returns0x00010003,
driver rejects any other version before open, resolves all16 symbols. Product
assembly/output name eventhouse-engine, native win-x64 eventhouse-engine.dll.
Test hook exports and eh_spike_* absent from product. SPI class/package unchanged.
No native unload. Product build selected by -p:EventhouseProduct=true; historical
managed feasibility tests may retain default spike build/testhooks. Packaging
must always select product build. Version/property affects compile constants,
assembly/output identity and export naming only, not silently alternate behaviour.
Use isolated build outputs when switching profiles to avoid stale products.

The accompanying header includes every signature above with fixed width and
size_t as existing authorities, statuses0..14, version constant and documented
no-NUL UTF8 length/query/result conventions. No future optional exports promised.
