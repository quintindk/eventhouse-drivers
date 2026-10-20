package com.microsoft.eventhouse.jdbc;

import com.sun.jna.IntegerType;
import com.sun.jna.Library;
import com.sun.jna.Native;
import com.sun.jna.Pointer;
import com.sun.jna.ptr.ByteByReference;
import com.sun.jna.ptr.IntByReference;
import com.sun.jna.ptr.LongByReference;
import com.sun.jna.ptr.ByReference;

/** Product ABI 0x00010003, exactly 16 cdecl exports. Historical class name only.
 * uint64 handles are bit patterns, not pointers. */
public interface SpikeLibrary extends Library {
    final class SizeT extends IntegerType {
        private static final long serialVersionUID = 1L;
        public SizeT() { this(0); }
        public SizeT(long value) { super(Native.SIZE_T_SIZE, value, true); }
    }
    final class SizeRef extends ByReference {
        public SizeRef() { super(Native.SIZE_T_SIZE); getPointer().clear(Native.SIZE_T_SIZE); }
        public long value() {
            return Native.SIZE_T_SIZE == 8 ? getPointer().getLong(0)
                    : Integer.toUnsignedLong(getPointer().getInt(0));
        }
    }
    int eh_abi_version_v1();
    int eh_open_v1(byte[] uri, SizeT uriLength, byte[] database, SizeT databaseLength,
            byte[] tenant, SizeT tenantLength, byte[] client, SizeT clientLength,
            byte[] secret, SizeT secretLength, int timeoutMilliseconds, LongByReference handle);
    int eh_execute_v1(long connection, byte[] sql, SizeT sqlLength, LongByReference result);
    int eh_operation_prepare_v1(long connection, int timeoutMilliseconds, LongByReference operation);
    int eh_operation_execute_v1(long operation, byte[] sql, SizeT sqlLength, LongByReference result);
    int eh_operation_cancel_v1(long operation);
    int eh_operation_close_v1(long operation);
    int eh_result_shape_v1(long result, SizeRef columns, SizeRef rows);
    int eh_result_column_v1(long result, SizeT column, IntByReference type, ByteByReference nullable,
            Pointer buffer, SizeT capacity, SizeRef required);
    int eh_result_cell_v1(long result, SizeT row, SizeT column, IntByReference type, ByteByReference isNull,
            Pointer buffer, SizeT capacity, SizeRef required);
    int eh_result_next_v1(long result, ByteByReference hasRow);
    int eh_result_current_cell_v1(long result, SizeT column, IntByReference type, ByteByReference isNull,
            Pointer buffer, SizeT capacity, SizeRef required);
    int eh_connection_close_v1(long connection);
    int eh_result_close_v1(long result);
    int eh_error_code_v1();
    int eh_error_message_v1(Pointer buffer, SizeT capacity, SizeRef required);
}
