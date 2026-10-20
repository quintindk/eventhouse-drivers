package com.microsoft.eventhouse.jdbc;

import com.sun.jna.Memory;
import com.sun.jna.Native;
import com.sun.jna.Pointer;
import com.sun.jna.ptr.IntByReference;
import com.sun.jna.win32.StdCallLibrary;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.math.BigDecimal;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.SQLFeatureNotSupportedException;
import java.sql.SQLTimeoutException;
import java.sql.Statement;
import java.sql.Timestamp;
import java.sql.Types;
import java.time.Instant;
import java.util.Objects;
import java.util.Properties;

/** Direct bounded SQL caller. No credentials, row values or exception text in output. */
public final class RcSmoke {
    private static int assertions;
    private static CaseDiagnostic diagnostic;
    private RcSmoke() { }

    @SuppressWarnings("try") // Repeated connection close is intentional.
    public static void main(String[] args) {
        String stage = "configuration";
        boolean verifyRuntime = false;
        RuntimeInspection inspection = null;
        try {
            boolean failures = false;
            boolean overcell = false;
            for (String arg : args) {
                if ("--live-failures".equals(arg)) failures = true;
                else if ("--overcell".equals(arg)) overcell = true;
                else if ("--verify-runtime".equals(arg)) verifyRuntime = true;
                else throw new IllegalArgumentException();
            }
            if (overcell && !failures) throw new IllegalArgumentException();
            if (verifyRuntime) {
                if (!failures) throw new RuntimeRejected(RuntimeReason.arguments);
                if (!System.getProperty("os.name", "").startsWith("Windows")
                        || java.io.File.separatorChar != '\\') {
                    System.out.println("WP006R_RUNTIME outcome=blocked reason=platform checkpoints=0 expected=8");
                    System.exit(2);
                    return;
                }
            }
            String host = required("EVENTHOUSE_QUERY_HOST");
            Properties p = new Properties();
            for (String[] pair : new String[][] {{"tenantId", "AZURE_TENANT_ID"},
                    {"clientId", "AZURE_SPN_CLIENT_ID"}, {"clientSecret", "AZURE_SPN_SECRET"},
                    {"nativeLibrary", "EVENTHOUSE_NATIVE_LIBRARY"}}) {
                p.setProperty(pair[0], required(pair[1]));
            }
            p.setProperty("timeoutMillis", "30000");
            String database = URLEncoder.encode(required("EVENTHOUSE_DB_NAME"), StandardCharsets.UTF_8)
                    .replace("+", "%20");
            if (verifyRuntime) {
                inspection = new RuntimeInspection(Path.of(p.getProperty("nativeLibrary")));
                inspection.checkpoint("pre_load");
                NativeEngine.load(inspection.target);
                inspection.checkpoint("post_load");
            }
            stage = "open";
            try (Connection c = DriverManager.getConnection("jdbc:eventhouse://" + host + "/" + database, p)) {
                stage = "select-one";
                one(c);
                stage = "known-values";
                // Service SQL support is verified by execution, never substituted with KQL or a mock.
                String sql = "SELECT CAST(1 AS bit) AS b, CAST(1234567890.125 AS decimal(20,3)) AS d, "
                        + "CAST(NULL AS decimal(20,3)) AS n, N'Grüße 東京 😀' AS u, "
                        + "CAST(-2147483648 AS int) AS i, CAST(9223372036854775807 AS bigint) AS l, "
                        + "CAST('2026-10-16T12:34:56.123' AS datetime) AS t";
                try (Statement s = c.createStatement(); ResultSet r = s.executeQuery(sql)) {
                    check(r.getMetaData().getColumnCount() == 7);
                    int[] types = {Types.BOOLEAN, Types.DECIMAL, Types.DECIMAL, Types.VARCHAR,
                            Types.INTEGER, Types.BIGINT, Types.TIMESTAMP};
                    for (int i = 0; i < types.length; i++) check(r.getMetaData().getColumnType(i + 1) == types[i]);
                    check(r.next());
                    check(r.getBoolean("b") && !r.wasNull());
                    check(Objects.equals(r.getObject("b"), Boolean.TRUE));
                    check(r.getBigDecimal("d").compareTo(new BigDecimal("1234567890.125")) == 0 && !r.wasNull());
                    check(r.getObject("d") instanceof BigDecimal);
                    check(r.getBigDecimal("n") == null && r.wasNull());
                    check(r.getObject("n") == null && r.wasNull());
                    check("Grüße 東京 😀".equals(r.getString("u")) && !r.wasNull());
                    check(r.getInt("i") == Integer.MIN_VALUE && !r.wasNull());
                    check(r.getLong("l") == Long.MAX_VALUE && !r.wasNull());
                    Timestamp timestamp = r.getTimestamp("t");
                    check(timestamp != null && !r.wasNull());
                    check(timestamp.toInstant().equals(Instant.parse("2026-10-16T12:34:56.123Z")));
                    check(!r.next());
                }
                stage = "query-rejection";
                try (Statement s = c.createStatement()) {
                    try (ResultSet unexpected = s.executeQuery("SELECT FROM")) {
                        check(unexpected == null); // A successful result is never accepted below.
                        throw new SQLException("Expected query rejection", "HY000");
                    } catch (SQLFeatureNotSupportedException wrongBoundary) {
                        throw new SQLException("Not a service query rejection", "HY000");
                    } catch (SQLException rejected) {
                        check("42000".equals(rejected.getSQLState()) && rejected.getErrorCode() == 14);
                    }
                }
                stage = "recovery";
                one(c);
                if (inspection != null) inspection.checkpoint("query_recovery");
                stage = "unsupported";
                try {
                    c.prepareStatement("SELECT 1");
                    throw new SQLException("Prepared statement unexpectedly supported", "HY000");
                } catch (SQLFeatureNotSupportedException expected) {
                    check("0A000".equals(expected.getSQLState()));
                }
                try {
                    c.setAutoCommit(false);
                    throw new SQLException("Transaction unexpectedly supported", "HY000");
                } catch (SQLFeatureNotSupportedException expected) {
                    check("0A000".equals(expected.getSQLState()));
                }
                stage = "repeat-close";
                for (int i = 0; i < 3; i++) one(c);
                c.close();
                c.close();
                check(c.isClosed());
            }
            if (inspection != null) inspection.checkpoint("base_closed");
            System.out.println("PASS WP005K_RC assertions=" + assertions
                    + " known_values=pass query_rejection=pass recovery=pass unsupported=pass repeat_close=pass"
                    + " live_inflight_cancel=blocked live_inflight_timeout=blocked");
            if (failures) {
                stage = "live-failures";
                String url = "jdbc:eventhouse://" + host + "/" + database;
                int failed = 0;
                int blocked = 0;
                for (String name : new String[] {"cursor_cancel", "idle_deadline", "invalid_secret", "overcell"}) {
                    int before = assertions;
                    diagnostic = new CaseDiagnostic();
                    String outcome;
                    try {
                        outcome = switch (name) {
                            case "cursor_cancel" -> cursorFailure(url, p, false);
                            case "idle_deadline" -> cursorFailure(url, p, true);
                            case "invalid_secret" -> invalidSecret(url, p);
                            case "overcell" -> overcell ? overcell(url, p) : "blocked_not_requested";
                            default -> throw new IllegalStateException();
                        };
                    } catch (Throwable failure) {
                        outcome = "fail"; // Never print service, credential or exception text.
                        diagnostic.capture(failure);
                        diagnostic.print(name, failure);
                    }
                    if ("fail".equals(outcome)) failed++;
                    else if (outcome.startsWith("blocked_")) blocked++;
                    System.out.println("WP006D case=" + name + " outcome=" + outcome
                            + " assertions=" + (assertions - before));
                    if (inspection != null && !"overcell".equals(name)) {
                        // A failed/blocked recovery must never acquire a successful checkpoint.
                        inspection.checkpoint(RuntimePolicy.recoveryCheckpoint(name, outcome));
                    }
                }
                System.out.println("WP006D cases=4 pass=" + (4 - failed - blocked)
                        + " fail=" + failed + " blocked=" + blocked + " assertions=" + (assertions - 45));
                if (inspection != null) {
                    if (failed != 0) throw new RuntimeRejected(RuntimeReason.workload);
                    inspection.checkpoint("final");
                    System.out.println("WP006R_RUNTIME outcome=pass checkpoints=8 expected=8");
                }
                if (failed != 0 || blocked != 0) System.exit(failed != 0 ? 1 : 2);
            }
        } catch (Throwable failure) {
            if (verifyRuntime) {
                RuntimeReason reason = failure instanceof RuntimeRejected r ? r.reason : RuntimeReason.workload;
                System.out.println("WP006R_RUNTIME outcome=fail reason=" + reason
                        + " checkpoints=" + (inspection == null ? 0 : inspection.completed) + " expected=8");
            }
            System.out.println("FAIL WP005K_RC stage=" + stage);
            System.exit(1);
        }
    }

    enum RuntimeReason { arguments, platform, binding, enumeration, bounds, truncation, unstable,
        identity, external_runtime, sequence, workload }

    static final class RuntimeRejected extends RuntimeException {
        private static final long serialVersionUID = 1L;
        final RuntimeReason reason;
        RuntimeRejected(RuntimeReason reason) { super(reason.name()); this.reason = reason; }
    }

    /** Pure validation shared with DriverTest. No Windows API or library initialisation. */
    static final class RuntimePolicy {
        static final int INITIAL = 256, MAXIMUM = 4096, PASSES = 4, NAME_CAPACITY = 32768;
        static final String[] CHECKPOINTS = {"pre_load", "post_load", "query_recovery", "base_closed",
            "cancel_recovery", "deadline_recovery", "auth_reconnect", "final"};
        private static final Set<String> FORBIDDEN = Set.of("coreclr.dll", "clrjit.dll", "hostfxr.dll",
                "hostpolicy.dll", "nethost.dll", "mscoree.dll", "clr.dll", "mscorjit.dll",
                "mscorwks.dll", "mscorsvr.dll", "sos.dll");

        static boolean forbidden(String basename) { return FORBIDDEN.contains(basename.toLowerCase(Locale.ROOT)); }

        static int moduleCount(boolean success, int needed, int pointerSize) {
            if (!success) throw new RuntimeRejected(RuntimeReason.enumeration);
            long bytes = Integer.toUnsignedLong(needed);
            if (pointerSize != 8 || bytes == 0 || bytes % pointerSize != 0
                    || bytes / pointerSize > MAXIMUM) throw new RuntimeRejected(RuntimeReason.bounds);
            return (int) (bytes / pointerSize);
        }

        static boolean complete(int count, int capacity, int pass) {
            if (count < 1 || count > MAXIMUM || capacity < INITIAL || capacity > MAXIMUM
                    || pass < 1 || pass > PASSES) throw new RuntimeRejected(RuntimeReason.bounds);
            if (count <= capacity) return true;
            if (pass == PASSES) throw new RuntimeRejected(RuntimeReason.truncation);
            return false;
        }

        static String filename(int length, char[] buffer) {
            if (length <= 0 || length >= buffer.length || buffer[length] != 0)
                throw new RuntimeRejected(RuntimeReason.truncation);
            for (int i = 0; i < length; i++) {
                if (buffer[i] == 0) throw new RuntimeRejected(RuntimeReason.truncation);
            }
            return new String(buffer, 0, length);
        }

        static void sequence(int completed, String checkpoint) {
            if (completed < 0 || completed >= CHECKPOINTS.length || !CHECKPOINTS[completed].equals(checkpoint))
                throw new RuntimeRejected(RuntimeReason.sequence);
        }

        static String recoveryCheckpoint(String name, String outcome) {
            if (!"pass".equals(outcome)) throw new RuntimeRejected(RuntimeReason.workload);
            return switch (name) {
                case "cursor_cancel" -> "cancel_recovery";
                case "idle_deadline" -> "deadline_recovery";
                case "invalid_secret" -> "auth_reconnect";
                default -> throw new RuntimeRejected(RuntimeReason.sequence);
            };
        }

        static void module(Map<Long, Path> modules, long handle, Path path) {
            if (handle == 0 || path == null || !path.isAbsolute() || path.getFileName() == null
                    || modules.putIfAbsent(handle, path) != null)
                throw new RuntimeRejected(RuntimeReason.identity);
            if (forbidden(path.getFileName().toString())) throw new RuntimeRejected(RuntimeReason.external_runtime);
        }

        static int targetCount(Map<Long, Path> modules, Path target, boolean loaded) {
            int count = 0;
            for (Path path : modules.values()) {
                if (target.equals(path)) count++;
                else if (target.getFileName().toString().equalsIgnoreCase(path.getFileName().toString()))
                    throw new RuntimeRejected(RuntimeReason.identity);
            }
            if (count != (loaded ? 1 : 0)) throw new RuntimeRejected(RuntimeReason.identity);
            return count;
        }

        static boolean stable(Map<Long, Path> previous, Map<Long, Path> current, int pass) {
            if (pass < 1 || pass > PASSES) throw new RuntimeRejected(RuntimeReason.bounds);
            if (previous != null && previous.equals(current)) return true;
            if (pass == PASSES) throw new RuntimeRejected(RuntimeReason.unstable);
            return false;
        }
    }

    public interface RuntimeKernel32 extends StdCallLibrary {
        Pointer GetCurrentProcess();
        int GetModuleFileNameW(Pointer module, char[] filename, int capacity);
        int GetSystemDirectoryW(char[] directory, int capacity);
    }

    public interface RuntimePsapi extends StdCallLibrary {
        int EnumProcessModulesEx(Pointer process, Pointer modules, int bytes, IntByReference needed, int filter);
    }

    /** Own JVM only. Never closes module handles, unloads libraries or prints module paths. */
    private static final class RuntimeInspection {
        final Path target;
        final RuntimeKernel32 kernel;
        final RuntimePsapi psapi;
        final Pointer process;
        int completed;

        RuntimeInspection(Path configured) {
            try {
                if (!configured.isAbsolute() || Native.POINTER_SIZE != 8)
                    throw new RuntimeRejected(RuntimeReason.identity);
                target = configured.toRealPath();
                kernel = Native.load("kernel32", RuntimeKernel32.class);
                char[] directory = new char[RuntimePolicy.NAME_CAPACITY];
                String system = RuntimePolicy.filename(kernel.GetSystemDirectoryW(directory, directory.length), directory);
                psapi = Native.load(Path.of(system).resolve("psapi.dll").toString(), RuntimePsapi.class);
                process = kernel.GetCurrentProcess();
                if (process == null || Pointer.nativeValue(process) == 0)
                    throw new RuntimeRejected(RuntimeReason.binding);
            } catch (RuntimeRejected rejected) { throw rejected; }
            catch (Exception | LinkageError failure) { throw new RuntimeRejected(RuntimeReason.binding); }
        }

        void checkpoint(String checkpoint) {
            RuntimePolicy.sequence(completed, checkpoint);
            try {
                Map<Long, Path> previous = null;
                int capacity = RuntimePolicy.INITIAL;
                char[] name = new char[RuntimePolicy.NAME_CAPACITY];
                for (int pass = 1; pass <= RuntimePolicy.PASSES; pass++) {
                    Map<Long, Path> current = new HashMap<>();
                    try (Memory handles = new Memory(Math.multiplyExact((long) capacity, Native.POINTER_SIZE))) {
                        IntByReference needed = new IntByReference();
                        int count = RuntimePolicy.moduleCount(psapi.EnumProcessModulesEx(process, handles,
                                Math.toIntExact(handles.size()), needed, 3) != 0, needed.getValue(), Native.POINTER_SIZE);
                        if (!RuntimePolicy.complete(count, capacity, pass)) {
                            capacity = count;
                            previous = null; // An incomplete observation never establishes stability.
                            continue;
                        }
                        for (int i = 0; i < count; i++) {
                            Pointer handle = handles.getPointer((long) i * Native.POINTER_SIZE);
                            if (handle == null) throw new RuntimeRejected(RuntimeReason.identity);
                            java.util.Arrays.fill(name, '\0');
                            String filename = RuntimePolicy.filename(kernel.GetModuleFileNameW(handle, name, name.length), name);
                            Path path = Path.of(filename);
                            if (!path.isAbsolute()) throw new RuntimeRejected(RuntimeReason.identity);
                            RuntimePolicy.module(current, Pointer.nativeValue(handle), path.toRealPath());
                        }
                    }
                    int targets = RuntimePolicy.targetCount(current, target, completed != 0);
                    if (RuntimePolicy.stable(previous, current, pass)) {
                        completed++;
                        System.out.println("WP006R_MODULE checkpoint=" + checkpoint + " outcome=pass modules="
                                + current.size() + " passes=" + pass + " target=" + targets + " external=0");
                        return;
                    }
                    previous = current;
                }
                throw new RuntimeRejected(RuntimeReason.unstable);
            } catch (RuntimeRejected rejected) { throw rejected; }
            catch (Exception | LinkageError failure) { throw new RuntimeRejected(RuntimeReason.enumeration); }
        }
    }

    private static String cursorFailure(String url, Properties p, boolean deadline) throws Exception {
        diagnostic.phase = 1;
        try (Connection c = DriverManager.getConnection(url, p)) {
            String outcome = "pass";
            diagnostic.phase = 2;
            Statement s = c.createStatement();
            Throwable statementFailure = null;
            try {
                if (deadline) s.setQueryTimeout(10);
                boolean firstRowSeen = false;
                ResultSet r = null;
                Throwable resultFailure = null;
                try {
                    diagnostic.phase = 3;
                    r = s.executeQuery("SELECT * FROM transactions");
                    diagnostic.phase = 4;
                    firstRowSeen = r.next();
                    if (!firstRowSeen) {
                        outcome = "blocked_no_first_row";
                    } else {
                        check(true); // Actual first row, not a scripted or already-drained cursor.
                        diagnostic.phase = 5;
                        if (deadline) Thread.sleep(10200); // Contract includes idle consumer time, no polling loop.
                        else s.cancel();
                        diagnostic.phase = 6;
                        try {
                            r.next();
                            throw new IllegalStateException(); // A row or false EOF is not a terminal failure.
                        } catch (SQLException expected) {
                            diagnostic.terminal = CaseDiagnostic.sql(expected);
                            diagnostic.phase = 7;
                            check(expected.getErrorCode() == (deadline ? 10 : 8)
                                    && (deadline ? "HYT00" : "HY008").equals(expected.getSQLState()));
                            diagnostic.phase = 8;
                            check((expected instanceof SQLTimeoutException) == deadline);
                        }
                    }
                } catch (SQLTimeoutException beforeFirstRow) {
                    // A deadline before the first row does not prove idle-cursor termination.
                    if (!deadline || firstRowSeen) {
                        resultFailure = beforeFirstRow;
                        diagnostic.capture(beforeFirstRow);
                        throw beforeFirstRow;
                    }
                    diagnostic.terminal = CaseDiagnostic.sql(beforeFirstRow);
                    try {
                        check(beforeFirstRow.getErrorCode() == 10 && "HYT00".equals(beforeFirstRow.getSQLState()));
                    } catch (SQLException failure) {
                        resultFailure = failure;
                        diagnostic.capture(failure);
                        throw failure;
                    }
                    outcome = "blocked_deadline_before_first_row";
                } catch (Exception | Error failure) {
                    resultFailure = failure;
                    diagnostic.capture(failure);
                    throw failure;
                } finally {
                    closeCase(r, resultFailure, 9);
                }
            } catch (Exception | Error failure) {
                statementFailure = failure;
                diagnostic.capture(failure);
                throw failure;
            } finally {
                closeCase(s, statementFailure, 10);
            }
            diagnostic.phase = 11;
            one(c); // Same physical connection, fresh statement, complete EOF.
            diagnostic.phase = 14;
            return outcome;
        }
    }

    private static void closeCase(AutoCloseable resource, Throwable primary, int phase) throws Exception {
        diagnostic.phase = phase;
        if (resource == null) return;
        try { resource.close(); }
        catch (Exception | Error cleanup) {
            diagnostic.capture(cleanup);
            if (primary == null) throw cleanup;
            primary.addSuppressed(cleanup);
        }
    }

    /** Closed labels only. Never render arbitrary class names, states or messages. */
    private static final class CaseDiagnostic {
        int phase;
        int failedPhase;
        String failure;
        String terminal = "none";
        void capture(Throwable value) {
            if (failure == null) { failedPhase = phase; failure = sql(value); }
        }
        void print(String name, Throwable value) {
            Throwable[] suppressed = value.getSuppressed();
            System.out.println("WP006D diagnostic=" + name + " phase=" + failedPhase
                    + " failure=" + failure + " terminal=" + terminal
                    + " suppressed=" + (suppressed.length == 0 ? "none" : sql(suppressed[0])));
        }
        static String sql(Throwable value) {
            String kind = value.getClass() == SQLTimeoutException.class ? "SQLTimeoutException"
                    : value.getClass() == SQLException.class ? "SQLException"
                    : value.getClass() == IllegalStateException.class ? "IllegalStateException"
                    : value.getClass() == InterruptedException.class ? "InterruptedException" : "other";
            if (!(value instanceof SQLException e)) return kind + ":none:none";
            String state = switch (Objects.toString(e.getSQLState(), "")) {
                case "HY008", "HYT00", "HY000", "HY010", "08003", "08006", "28000", "42000" -> e.getSQLState();
                default -> "other";
            };
            int code = e.getErrorCode();
            return kind + ":" + state + ":" + (code >= 0 && code <= 14 ? Integer.toString(code) : "other");
        }
    }

    private static String invalidSecret(String url, Properties approved) throws SQLException {
        diagnostic.phase = 15;
        Properties invalid = new Properties();
        invalid.putAll(approved);
        invalid.setProperty("clientSecret", "WP006D-deliberately-invalid-secret-not-a-credential");
        try {
            try (Connection c = DriverManager.getConnection(url, invalid); Statement s = c.createStatement()) {
                try (ResultSet unexpected = s.executeQuery("SELECT 1")) {
                    unexpected.getMetaData();
                    throw new IllegalStateException();
                } catch (SQLException expected) {
                    diagnostic.terminal = CaseDiagnostic.sql(expected);
                    check(expected.getErrorCode() == 4);
                    check("28000".equals(expected.getSQLState()));
                }
            }
        } finally { invalid.clear(); }
        // Do not mutate the approved properties or retry the rejected identity.
        try (Connection c = DriverManager.getConnection(url, approved)) { one(c); }
        return "pass";
    }

    private static String overcell(String url, Properties p) throws SQLException {
        diagnostic.phase = 16;
        String outcome = "blocked_expression_not_overlimit";
        try (Connection c = DriverManager.getConnection(url, p)) {
            try (Statement s = c.createStatement()) {
                // Candidate only: Fabric SQL support and REPLICATE return width are NOT established.
                try (ResultSet r = s.executeQuery("SELECT REPLICATE('x',4194305) AS oversized")) {
                    if (r.next()) {
                        String value = r.getString(1);
                        if (value != null && value.length() > 4194304) throw new IllegalStateException();
                    }
                } catch (SQLException expected) {
                    diagnostic.terminal = CaseDiagnostic.sql(expected);
                    if (expected.getErrorCode() == 14 && "42000".equals(expected.getSQLState())) {
                        outcome = "blocked_service_sql_unsupported";
                    } else {
                        check(expected.getErrorCode() == 7);
                        check("HY000".equals(expected.getSQLState()));
                        // Generic protocol failure cannot independently identify the cell-limit reason.
                        outcome = "blocked_protocol_reason_unverified";
                    }
                }
            }
            one(c);
        }
        return outcome;
    }

    @SuppressWarnings("try") // Explicit repeated close is the behaviour under test.
    private static void one(Connection c) throws SQLException {
        try (Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT 1")) {
            if (diagnostic != null) diagnostic.phase = 12;
            check(r.next());
            check(r.getInt(1) == 1 && !r.wasNull());
            check(!r.next());
            if (diagnostic != null) diagnostic.phase = 13;
            r.close();
            r.close();
            s.close();
            s.close();
            check(r.isClosed() && s.isClosed());
        }
    }

    private static void check(boolean condition) throws SQLException {
        assertions++;
        if (!condition) throw new SQLException("RC assertion failed", "HY000");
    }

    private static String required(String name) {
        String value = System.getenv(name);
        if (value == null || value.isBlank()) throw new IllegalStateException("Missing configuration");
        return value;
    }
}
