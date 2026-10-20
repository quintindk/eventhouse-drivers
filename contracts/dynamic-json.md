# RC dynamic JSON representation, product ABI 1.3

Representation rules retained by product ABI 0x00010003 with all 16 required
exports. The driver requires the exact version, without silent fallback.
The default spike profile is not a product package.

Native type10(dynamic): every non-null JSON value returns its exact raw JSON
lexical UTF8 bytes from the response, including quotes/escapes for scalar strings.
No parse/reformat through double, no key reordering or invented canonicalisation.
Native null returns0bytes,is_null1; JSON string "null" returns quotedbytes,is_null0.
Non-dynamic representations remain unchanged. GetString is used for non-dynamic
JSON strings only; other non-dynamic values retain raw lexical text.

JDBC type10 maps LONGVARCHAR/String with typeName dynamic. getString(index/name)
and getObject(index/name) return JSON text or SQLnull; wasNull agrees. Numeric,
boolean,timestamp/decimal conversions from dynamic reject22018, never coerce.
Metadata signedfalse, no inventedprecision/schema. Driver does not claim JSON
object materialisation, SQLXML or java.sql.Array. Service JSONnull and SQLnull are
indistinguishable on this wire: document that limitation, do not inventdifference.

Direct tests retain raw JSON, Unicode, null and conversion rejection coverage.
Unrestricted streamed results are specified in streaming-results.md. No total
process-memory or throughput guarantee is claimed.
