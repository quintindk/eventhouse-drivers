package com.microsoft.eventhouse.jdbc;

import com.sun.jna.Function;
import com.sun.jna.Memory;
import com.sun.jna.Native;
import com.sun.jna.NativeLibrary;
import com.sun.jna.ptr.ByteByReference;
import com.sun.jna.ptr.IntByReference;
import com.sun.jna.ptr.LongByReference;
import java.nio.file.Path;
import java.sql.SQLException;
import java.sql.SQLTimeoutException;
import java.util.Arrays;
import java.util.Map;
import static com.microsoft.eventhouse.jdbc.SpikeLibrary.*;

final class NativeEngine {
    // System-loader roots for JVM lifetime, including rejected libraries. Never dispose.
    private static SpikeLibrary retainedApi;
    private static NativeLibrary retained;
    private static Path loadedPath;
    private static NativeEngine instance;
    private final SpikeLibrary api;
    NativeEngine(SpikeLibrary api) { this.api = api; }

    static void requireSystemLoaders() throws SQLException {
        ClassLoader system = ClassLoader.getSystemClassLoader();
        // Class literals and getClassLoader do not initialise JNA or load JNI code.
        for (Class<?> type : new Class<?>[] {EventhouseDriver.class, NativeEngine.class, SpikeLibrary.class,
                Native.class, NativeLibrary.class, com.sun.jna.Library.class}) {
            if (type.getClassLoader() != system)
                throw new SQLException("Driver and JNA must be defined by the JVM system class loader. Plugin loading and hot replacement are unsupported.", "08001");
        }
    }

    static synchronized NativeEngine load(Path path) throws SQLException {
        requireSystemLoaders();
        try {
            Path canonical = path.toRealPath();
            if (loadedPath != null && !loadedPath.equals(canonical)) throw Configuration.invalid();
            if (retainedApi == null) {
                // One load/options identity. Root the proxy before any symbol/ABI validation.
                retainedApi = Native.load(canonical.toString(), SpikeLibrary.class,
                        Map.of(com.sun.jna.Library.OPTION_CALLING_CONVENTION, Function.C_CONVENTION,
                                com.sun.jna.Library.OPTION_CLASSLOADER, ClassLoader.getSystemClassLoader()));
                loadedPath = canonical;
                retained = Native.getNativeLibrary(retainedApi);
            }
            if (instance == null) {
                validateProduct(retainedApi, name -> retained.getFunction(name));
                instance = new NativeEngine(retainedApi);
            }
            return instance;
        } catch (SQLException e) { throw e; }
        catch (Exception | LinkageError ignored) {
            throw new SQLException("Native engine load failed. Supply an absolute nativeLibrary path matching JVM architecture and install JNA 5.17.0.", "08001");
        }
    }

    static void validateProduct(SpikeLibrary library, java.util.function.Consumer<String> resolve) throws SQLException {
        if (library.eh_abi_version_v1() != 0x00010003)
            throw new SQLException("Native ABI version mismatch. Require product ABI 0x00010003.", "08001");
        try {
            // All 16 symbols required before opening a handle, never legacy fallback.
            for (var method : SpikeLibrary.class.getDeclaredMethods()) resolve.accept(method.getName());
        } catch (LinkageError ignored) {
            throw new SQLException("Native product ABI exports are incomplete. Require all 16 product symbols.", "08001");
        }
    }

    long open(Configuration config) throws SQLException {
        byte[] uri = Utf8.encode("https://" + config.host(), 512, "08001");
        byte[] db = Utf8.encode(config.database(), 256, "08001");
        byte[] tenant = Utf8.encode(config.tenant(), 64, "08001"), client = Utf8.encode(config.client(), 64, "08001");
        byte[] secret = Utf8.encode(config.secret(), 4096, "08001");
        LongByReference handle = new LongByReference();
        try {
            check(api.eh_open_v1(uri, size(uri.length), db, size(db.length), tenant, size(tenant.length),
                    client, size(client.length), secret, size(secret.length), config.timeout(), handle));
            return nonzero(handle.getValue());
        } finally { Arrays.fill(secret, (byte) 0); }
    }
    long prepare(long connection, int timeoutMilliseconds) throws SQLException {
        LongByReference operation = new LongByReference();
        checkOperation(api.eh_operation_prepare_v1(connection, timeoutMilliseconds, operation));
        return nonzero(operation.getValue());
    }
    long executeOperation(long operation, String sql) throws SQLException {
        byte[] bytes = Utf8.encode(sql, 16384, "HY000");
        if (bytes.length == 0) throw new SQLException("Query is empty.", "HY000");
        LongByReference handle = new LongByReference();
        checkOperation(api.eh_operation_execute_v1(operation, bytes, size(bytes.length), handle));
        return nonzero(handle.getValue());
    }
    void cancelOperation(long operation) throws SQLException { checkOperation(api.eh_operation_cancel_v1(operation)); }
    void closeOperation(long operation) throws SQLException { checkOperation(api.eh_operation_close_v1(operation)); }
    long[] shape(long handle) throws SQLException {
        SizeRef columns = new SizeRef(), rows = new SizeRef();
        checkOperation(api.eh_result_shape_v1(handle, columns, rows));
        if (columns.value() < 0 || columns.value() > 4096)
            throw new SQLException("Native result dimensions exceed Java indexing.", "HY000");
        // SIZE_MAX is unknown, not a negative dimension or a Java row-count limit.
        long count = rows.value();
        return new long[] { columns.value(), count == (Native.SIZE_T_SIZE == 8 ? -1L : 0xffffffffL) ? -1L : count };
    }
    boolean next(long handle) throws SQLException {
        ByteByReference hasRow = new ByteByReference();
        checkOperation(api.eh_result_next_v1(handle, hasRow));
        if (hasRow.getValue() != 0 && hasRow.getValue() != 1)
            throw new SQLException("Invalid native row flag.", "HY000");
        return hasRow.getValue() == 1;
    }
    record Value(int type, boolean flag, String text) { }
    /** One result's serialised current-cell reader. Never owns row history. */
    final class CellReader implements AutoCloseable {
        private static final int MAXIMUM = 4 * 1024 * 1024;
        private Memory scratch;
        private boolean closed;
        private int cachedColumn = -1;
        private long cachedLength;
        private Value cached;
        private final IntByReference type = new IntByReference();
        private final ByteByReference flag = new ByteByReference();
        private final SizeRef required = new SizeRef();

        void clear() { cached = null; cachedColumn = -1; cachedLength = 0; }
        long capacity() { return scratch == null ? 0 : scratch.size(); }

        Value read(long handle, int column) throws SQLException {
            if (closed) throw new SQLException("Result cell reader is closed.", "HY010");
            try {
                if (cached != null && cachedColumn == column) {
                    int status = call(handle, column, null, 0);
                    long length = validate(status);
                    if (status != (length == 0 ? 0 : 2) || length != cachedLength || type.getValue() != cached.type()
                            || (flag.getValue() == 1) != cached.flag())
                        throw new SQLException("Native current cell changed.", "HY000");
                    return cached;
                }
                clear();
                int status = call(handle, column, scratch, capacity());
                long length = validate(status);
                if (status == 0 && length > capacity())
                    throw new SQLException("Native output exceeds buffer capacity.", "HY000");
                if (status == 2) {
                    if (length <= capacity()) throw new SQLException("Invalid native buffer requirement.", "HY000");
                    int expectedType = type.getValue();
                    byte expectedFlag = flag.getValue();
                    // Release the smaller allocation before growing. Aggregate native
                    // scratch capacity never exceeds the per-value maximum.
                    if (scratch != null) { scratch.close(); scratch = null; }
                    scratch = new Memory(length);
                    status = call(handle, column, scratch, capacity());
                    checkOperation(status);
                    if (validate(status) != length || type.getValue() != expectedType || flag.getValue() != expectedFlag)
                        throw new SQLException("Native current cell changed.", "HY000");
                }
                String text = length == 0 ? "" : Utf8.decode(scratch.getByteArray(0, (int) length));
                cached = new Value(type.getValue(), flag.getValue() == 1, text);
                cachedLength = length;
                cachedColumn = column;
                return cached;
            } catch (SQLException | RuntimeException | Error e) { clear(); throw e; }
        }
        private int call(long handle, int column, Memory buffer, long capacity) {
            return api.eh_result_current_cell_v1(handle, size(column), type, flag, buffer, size(capacity), required);
        }
        private long validate(int status) throws SQLException {
            if (status != 0 && status != 2) checkOperation(status);
            long length = required.value();
            if (length < 0 || length > MAXIMUM || (status == 2 && length == 0)
                    || (flag.getValue() != 0 && flag.getValue() != 1) || (flag.getValue() == 1 && length != 0))
                throw new SQLException("Invalid native cell representation.", "HY000");
            return length;
        }
        @Override public void close() {
            clear();
            if (closed) return;
            closed = true;
            if (scratch != null) { scratch.close(); scratch = null; }
        }
    }
    Value read(long handle, long row, int column) throws SQLException {
        IntByReference type = new IntByReference();
        ByteByReference flag = new ByteByReference();
        SizeRef required = new SizeRef();
        int status = row < 0
                ? api.eh_result_column_v1(handle, size(column), type, flag, null, size(0), required)
                : api.eh_result_current_cell_v1(handle, size(column), type, flag, null, size(0), required);
        if (status != 0 && status != 2) checkOperation(status);
        long length = required.value();
        // Per-value representation guard, not a total-response or process-memory limit.
        if (length < 0 || length > 4 * 1024 * 1024 || (status == 2 && length == 0))
            throw new SQLException("Invalid native output length.", "HY000");
        String text = "";
        if (length != 0) {
            try (Memory buffer = new Memory(length)) {
                checkOperation(row < 0
                        ? api.eh_result_column_v1(handle, size(column), type, flag, buffer, size(length), required)
                        : api.eh_result_current_cell_v1(handle, size(column), type, flag, buffer, size(length), required));
                if (required.value() != length) throw new SQLException("Native output length changed.", "HY000");
                text = Utf8.decode(buffer.getByteArray(0, (int) length));
            }
        }
        if (flag.getValue() != 0 && flag.getValue() != 1) throw new SQLException("Invalid native flag.", "HY000");
        return new Value(type.getValue(), flag.getValue() == 1, text);
    }
    void closeResult(long handle) throws SQLException { check(api.eh_result_close_v1(handle)); }
    void closeConnection(long handle) throws SQLException {
        // Native cancellation callbacks may outlive operation_close. The Java owner
        // has already drained execution/signalling callers and prevents new work.
        boolean interrupted = false;
        try {
            int status;
            while ((status = api.eh_connection_close_v1(handle)) == 12) {
                interrupted |= Thread.interrupted();
                java.util.concurrent.locks.LockSupport.parkNanos(1_000_000L);
            }
            check(status);
        } finally { if (interrupted) Thread.currentThread().interrupt(); }
    }
    static SizeT size(long value) { return new SizeT(value); }
    private static long nonzero(long handle) throws SQLException {
        if (handle == 0) throw new SQLException("Native engine returned an invalid handle.", "HY000");
        return handle;
    }
    static void check(int status) throws SQLException {
        if (status == 0) return;
        String state = switch (status) { case 1 -> "08001"; case 3 -> "HY010"; case 4 -> "28000";
            case 5 -> "08006"; case 8 -> "HYT00"; case 13 -> "42501"; case 14 -> "42000"; default -> "HY000"; };
        throw new SQLException("Native Eventhouse operation failed (status " + Integer.toUnsignedString(status) + ").", state, status);
    }
    static void checkOperation(int status) throws SQLException {
        if (status == 10) throw new SQLTimeoutException("Native Eventhouse operation timed out.", "HYT00", status);
        if (status == 8 || status == 11 || status == 12)
            throw new SQLException("Native Eventhouse operation failed (status " + status + ").",
                    status == 8 ? "HY008" : "HY010", status);
        check(status);
    }
    static SQLException drainTimeout() {
        return new SQLException("Native Eventhouse retirement drain budget exhausted.", "HY010", 12);
    }
}
