package com.microsoft.eventhouse.jdbc;

import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.sql.*;
import java.util.ArrayList;
import java.util.List;

/** Small explicit method whitelist. Every other JDBC method, including defaults, rejects. */
final class JdbcObjects {
    private JdbcObjects() { }
    static SQLFeatureNotSupportedException unsupported() {
        return new SQLFeatureNotSupportedException("Operation not supported by the Eventhouse JDBC driver.", "0A000");
    }
    static Connection connection(NativeEngine engine, long handle) {
        return new Session(engine, handle).proxy(Connection.class);
    }

    abstract static class ObjectHandler implements InvocationHandler {
        Object self;
        <T> T proxy(Class<T> type) {
            self = Proxy.newProxyInstance(JdbcObjects.class.getClassLoader(), new Class<?>[] {type}, this);
            return type.cast(self);
        }
        @Override public final Object invoke(Object proxy, Method method, Object[] arguments) throws Throwable {
            Object[] args = arguments == null ? new Object[0] : arguments;
            if (method.getDeclaringClass() == Object.class) return switch (method.getName()) {
                case "toString" -> "Eventhouse JDBC " + method.getDeclaringClass().getSimpleName();
                case "hashCode" -> System.identityHashCode(proxy);
                case "equals" -> proxy == args[0];
                default -> throw unsupported();
            };
            // These methods own short state locks internally. In particular, never
            // retain the session monitor across execute, cancellation or close waits.
            if (method.getName().equals("close") && args.length == 0) { close(); return null; }
            if (this instanceof Query query) {
                if (method.getName().equals("executeQuery")) return query.execute(args);
                if (method.getName().equals("cancel")) { query.cancel(); return null; }
            }
            if (this instanceof Rows rows && method.getName().equals("next")) return rows.next();
            if (this instanceof Rows rows) {
                try {
                    NativeEngine.Value snapshot;
                    synchronized (lock()) {
                        if (method.getName().equals("isClosed")) return closed();
                        checkOpen();
                        if (method.getName().equals("isWrapperFor")) return ((Class<?>) args[0]).isInstance(self);
                        if (method.getName().equals("unwrap")) {
                            if (((Class<?>) args[0]).isInstance(self)) return self;
                            throw unsupported();
                        }
                        if (!rows.isValueGetter(method.getName())) return call(method, args);
                        if (args.length != 1) throw unsupported();
                        snapshot = method.getName().equals("getString") || method.getName().equals("getObject")
                                ? rows.value(args[0]) : rows.conversionValue(args[0]);
                    }
                    // Immutable Java text only. No native cursor/cache/state access
                    // after admission, and no session monitor during conversion.
                    return Rows.convert(method.getName(), snapshot);
                } catch (SQLException e) {
                    // Native getter failure is terminal, conversion/index errors are not.
                    boolean terminal;
                    synchronized (lock()) { terminal = rows.terminalFailure == e; }
                    if (terminal) {
                        try { rows.operation.close(); } catch (SQLException cleanup) { e.addSuppressed(cleanup); }
                    }
                    throw e;
                }
            }
            synchronized (lock()) {
                if (method.getName().equals("isClosed") && args.length == 0) return closed();
                if (method.getName().equals("setClientInfo")) {
                    try { checkOpen(); }
                    catch (SQLException e) {
                        throw new SQLClientInfoException("Connection is closed.", e.getSQLState(), java.util.Map.of());
                    }
                    throw new SQLClientInfoException("Client info is unsupported.", "0A000", java.util.Map.of());
                }
                checkOpen();
                if (method.getName().equals("isWrapperFor")) return ((Class<?>) args[0]).isInstance(self);
                if (method.getName().equals("unwrap")) {
                    if (((Class<?>) args[0]).isInstance(self)) return self;
                    throw unsupported();
                }
                return call(method, args);
            }
        }
        abstract Object lock();
        abstract boolean closed();
        abstract void checkOpen() throws SQLException;
        abstract void close() throws SQLException;
        abstract Object call(Method method, Object[] args) throws SQLException;
    }

    static final class Session extends ObjectHandler {
        final NativeEngine engine;
        final long handle;
        final List<Query> statements = new ArrayList<>();
        boolean closed, closeComplete;
        Query executing;
        Query.Operation draining;
        Session(NativeEngine engine, long handle) { this.engine = engine; this.handle = handle; }
        Object lock() { return this; }
        boolean closed() { return closed; }
        void checkOpen() throws SQLException { if (closed) throw new SQLException("Connection is closed.", "08003"); }
        void close() throws SQLException {
            List<Query> children;
            synchronized (this) {
                if (closed) { await(this, () -> closeComplete); return; }
                closed = true;
                notifyAll();
                children = List.copyOf(statements);
            }
            SQLException failure = null;
            try {
                for (Query statement : children) {
                    try { statement.close(); } catch (SQLException e) { failure = collect(failure, e); }
                }
                try { engine.closeConnection(handle); } catch (SQLException e) { failure = collect(failure, e); }
            } finally {
                synchronized (this) { closeComplete = true; notifyAll(); }
            }
            if (failure != null) throw failure;
        }
        Object call(Method method, Object[] args) throws SQLException {
            return switch (method.getName()) {
                case "createStatement" -> {
                    if (args.length != 0 && !(args.length >= 2 && (int) args[0] == ResultSet.TYPE_FORWARD_ONLY
                            && (int) args[1] == ResultSet.CONCUR_READ_ONLY
                            && (args.length == 2 || (int) args[2] == ResultSet.CLOSE_CURSORS_AT_COMMIT))) throw unsupported();
                    Query query = new Query(this);
                    Statement statement = query.proxy(Statement.class);
                    statements.add(query);
                    yield statement;
                }
                case "isReadOnly" -> true;
                case "getAutoCommit" -> true;
                case "getTransactionIsolation" -> Connection.TRANSACTION_NONE;
                case "getHoldability" -> ResultSet.CLOSE_CURSORS_AT_COMMIT;
                default -> throw unsupported();
            };
        }
    }

    static final class Query extends ObjectHandler {
        final Session session;
        Rows result;
        boolean closed, closeComplete;
        int timeoutSeconds;
        Operation active;
        Admission admission;
        boolean executingCall;
        static final long DRAIN_NANOS = java.util.concurrent.TimeUnit.SECONDS.toNanos(5);
        final class Admission {
            final long started = System.nanoTime();
            final Operation drain = session.draining;
            boolean cancelled;
        }
        Query(Session session) { this.session = session; }
        Object lock() { return session; }
        boolean closed() { return closed || session.closed; }
        void checkOpen() throws SQLException { if (closed()) throw new SQLException("Statement is closed.", "HY010"); }
        void close() throws SQLException {
            Operation signal;
            Rows retainedResult;
            synchronized (session) {
                if (closed) { await(session, () -> closeComplete); return; }
                closed = true;
                if (admission != null) admission.cancelled = true;
                session.notifyAll();
                signal = reserveSignal();
                retainedResult = result;
            }
            SQLException failure = null;
            try {
                try { signal(signal); } catch (SQLException e) { failure = e; }
                try { if (retainedResult != null) retainedResult.close(); } catch (SQLException e) { failure = collect(failure, e); }
                synchronized (session) { await(session, () -> active == null && !executingCall); }
            } finally {
                synchronized (session) {
                    session.statements.remove(this);
                    closeComplete = true;
                    session.notifyAll();
                }
            }
            if (failure != null) throw failure;
        }
        // All Operation state is protected by session. Its identity never changes.
        final class Operation implements AutoCloseable {
            final long handle;
            int signals;
            boolean retiring, retired;
            Operation(long handle) { this.handle = handle; }
            @Override public void close() throws SQLException {
                synchronized (session) {
                    if (retiring) { await(session, () -> retired); return; }
                    retiring = true;
                    session.notifyAll();
                    await(session, () -> signals == 0);
                }
                boolean success = false;
                try { session.engine.closeOperation(handle); success = true; }
                finally {
                    synchronized (session) {
                        if (success) session.draining = this;
                        retired = true;
                        if (active == this) active = null;
                        if (!executingCall && session.executing == Query.this) session.executing = null;
                        session.notifyAll();
                    }
                }
            }
        }
        Operation reserveSignal() {
            if (active == null || active.retiring) return null;
            active.signals++;
            return active;
        }
        void signal(Operation operation) throws SQLException {
            if (operation == null) return;
            try { session.engine.cancelOperation(operation.handle); }
            catch (SQLException e) {
                synchronized (session) {
                    // Only an exactly identified retired operation can make a stale
                    // token harmless. Never swallow a wrong-kind/live-handle error.
                    if (e.getErrorCode() != 3 || !operation.retired) throw e;
                }
            } finally {
                synchronized (session) { operation.signals--; session.notifyAll(); }
            }
        }
        void cancel() throws SQLException {
            Operation operation;
            synchronized (session) {
                checkOpen();
                if (admission != null) { admission.cancelled = true; session.notifyAll(); }
                operation = reserveSignal();
            }
            signal(operation);
        }
        // Called only under session. Identity checks prevent one admission from
        // publishing or clearing another execution's state. No native call here.
        void checkAdmission(Admission expected) throws SQLException {
            checkOpen();
            if (admission != expected || session.executing != this)
                throw new SQLException("Query admission is no longer current.", "HY010");
            if (expected.cancelled) throw new SQLException("Query admission was cancelled.", "HY008", 8);
            if (expected.drain != null && System.nanoTime() - expected.started >= DRAIN_NANOS)
                throw NativeEngine.drainTimeout();
        }
        Operation prepare(Admission expected, int timeout) throws SQLException {
            boolean interrupted = false;
            try {
                while (true) {
                    synchronized (session) { checkAdmission(expected); }
                    long handle;
                    try { handle = session.engine.prepare(session.handle, timeout); }
                    catch (SQLException failure) {
                        synchronized (session) {
                            checkAdmission(expected);
                            if (failure.getErrorCode() != 12 || expected.drain == null) throw failure;
                        }
                        // Native prepare has not dispatched SQL. Only our own known
                        // retired reservation grants retry permission. Never hold the
                        // session monitor while waiting or entering native code.
                        interrupted |= Thread.interrupted();
                        java.util.concurrent.locks.LockSupport.parkNanos(1_000_000L);
                        continue;
                    }
                    Operation candidate = new Operation(handle);
                    try {
                        synchronized (session) {
                            checkAdmission(expected); // Includes late success after drain deadline.
                            if (session.draining == expected.drain) session.draining = null;
                            active = candidate;
                            admission = null;
                            return candidate;
                        }
                    } catch (SQLException failure) {
                        try { candidate.close(); } catch (SQLException cleanup) { failure.addSuppressed(cleanup); }
                        throw failure;
                    }
                }
            } finally { if (interrupted) Thread.currentThread().interrupt(); }
        }
        Object execute(Object[] args) throws SQLException {
            Operation operation = null;
            Admission reserved;
            int timeout;
            Rows previous;
            synchronized (session) {
                checkOpen();
                if (executingCall) throw new SQLException("Connection is executing a query.", "HY010");
                previous = result;
            }
            // Closing a retained stream can drain native calls. Never hold session here.
            if (previous != null) previous.close();
            synchronized (session) {
                checkOpen();
                if (args.length != 1 || !(args[0] instanceof String)) throw new SQLException("Invalid SQL input.", "HY000");
                if (executingCall || session.executing != null) throw new SQLException("Connection is executing a query.", "HY010");
                result = null;
                reserved = new Admission();
                admission = reserved;
                session.executing = this;
                executingCall = true;
                timeout = timeoutSeconds * 1000;
            }
            Rows candidate = null;
            Throwable failure = null;
            try {
                operation = prepare(reserved, timeout);
                {
                    long handle = session.engine.executeOperation(operation.handle, (String) args[0]);
                    try {
                        candidate = new Rows(this, handle, operation);
                        candidate.proxy(ResultSet.class);
                    } catch (SQLException | RuntimeException | Error e) {
                        candidate = null;
                        try { session.engine.closeResult(handle); } catch (SQLException cleanup) { e.addSuppressed(cleanup); }
                        throw e;
                    }
                }
                synchronized (session) {
                    checkOpen();
                    result = candidate;
                    candidate = null;
                    return result.self;
                }
            } catch (SQLException | RuntimeException | Error e) { failure = e; throw e; }
            finally {
                try {
                    if (candidate != null) {
                        try { candidate.close(); }
                        catch (SQLException cleanup) { if (failure != null) failure.addSuppressed(cleanup); else throw cleanup; }
                    }
                } finally {
                    try {
                        // Successful publication transfers ownership, never closes here.
                        if (failure != null && operation != null) {
                            try { operation.close(); }
                            catch (SQLException cleanup) { failure.addSuppressed(cleanup); }
                        }
                    } finally {
                        synchronized (session) {
                            if (admission == reserved) admission = null;
                            if (active == null && session.executing == this) session.executing = null;
                            executingCall = false;
                            session.notifyAll();
                        }
                    }
                }
            }
        }
        Object call(Method method, Object[] args) throws SQLException {
            return switch (method.getName()) {
                case "setQueryTimeout" -> {
                    int seconds = (int) args[0];
                    if (seconds < 0 || seconds > 600) throw new SQLException("Invalid query timeout.", "HY092");
                    timeoutSeconds = seconds;
                    yield null;
                }
                case "getQueryTimeout" -> timeoutSeconds;
                case "getResultSet" -> result == null || result.closed ? null : result.self;
                case "getConnection" -> session.self;
                case "getResultSetType" -> ResultSet.TYPE_FORWARD_ONLY;
                case "getResultSetConcurrency" -> ResultSet.CONCUR_READ_ONLY;
                case "getResultSetHoldability" -> ResultSet.CLOSE_CURSORS_AT_COMMIT;
                default -> throw unsupported();
            };
        }
    }

    record Column(String name, int nativeType, boolean nullable) {
        int sqlType() throws SQLException {
            return switch (nativeType) { case 1 -> Types.BOOLEAN; case 5 -> Types.DECIMAL;
                case 2 -> Types.INTEGER; case 3 -> Types.BIGINT; case 4 -> Types.DOUBLE;
                case 6 -> Types.TIMESTAMP; case 9 -> Types.VARCHAR; case 10 -> Types.LONGVARCHAR;
                default -> throw unsupported(); };
        }
        String typeName() throws SQLException {
            return switch (nativeType) { case 1 -> "bool"; case 5 -> "decimal";
                case 2 -> "int"; case 3 -> "long"; case 4 -> "double";
                case 6 -> "datetime"; case 9 -> "string"; case 10 -> "dynamic"; default -> throw unsupported(); };
        }
    }

    static final class Rows extends ObjectHandler {
        final Query query;
        final long handle;
        final Column[] columns;
        final Query.Operation operation;
        final NativeEngine.CellReader cells;
        boolean onRow, eof, fetching, closeComplete;
        SQLException terminalFailure;
        boolean closed, wasNull, read;
        ResultSetMetaData metadata;
        Rows(Query query, long handle, Query.Operation operation) throws SQLException {
            this.query = query;
            this.handle = handle;
            this.operation = operation;
            cells = query.session.engine.new CellReader();
            long[] shape = query.session.engine.shape(handle);
            columns = new Column[(int) shape[0]];
            for (int i = 0; i < columns.length; i++) {
                NativeEngine.Value value = query.session.engine.read(handle, -1, i);
                columns[i] = new Column(value.text(), value.type(), value.flag());
                columns[i].sqlType(); // No implicit support for deferred service types.
            }
        }
        Object lock() { return query.session; }
        boolean closed() { return closed || query.closed(); }
        void checkOpen() throws SQLException { if (closed()) throw new SQLException("ResultSet is closed.", "HY010"); }
        void close() throws SQLException {
            synchronized (query.session) {
                if (closed) { await(query.session, () -> closeComplete); return; }
                closed = true;
                onRow = false;
                read = false;
                cells.close();
                query.session.notifyAll();
            }
            SQLException failure = null;
            try {
                // Abandon first, then wait. Waiting before native close retains the stream.
                try { query.session.engine.closeResult(handle); } catch (SQLException e) { failure = e; }
                synchronized (query.session) { await(query.session, () -> !fetching); }
                try { operation.close(); } catch (SQLException e) { failure = collect(failure, e); }
            } finally {
                synchronized (query.session) { closeComplete = true; query.session.notifyAll(); }
            }
            if (failure != null) throw failure;
        }
        boolean next() throws SQLException {
            synchronized (query.session) {
                checkOpen();
                if (fetching) throw new SQLException("Result fetch is active.", "HY010");
                read = false;
                onRow = false;
                cells.clear();
                if (terminalFailure != null) throw terminalFailure;
                if (eof) return false;
                fetching = true;
            }
            SQLException failure = null;
            boolean hasRow = false;
            try {
                hasRow = query.session.engine.next(handle);
            } catch (SQLException e) { failure = e; }
            catch (RuntimeException | Error e) {
                try { closeAfterFetchFailure(); } catch (SQLException cleanup) { e.addSuppressed(cleanup); }
                throw e;
            }
            try {
                if (!hasRow || failure != null) {
                    try { operation.close(); }
                    catch (SQLException cleanup) { failure = collect(failure, cleanup); }
                }
                synchronized (query.session) {
                    terminalFailure = failure;
                    if (failure != null || !hasRow) cells.close();
                    eof = !hasRow && failure == null;
                    checkOpen();
                    if (failure != null) throw failure;
                    onRow = hasRow;
                    return hasRow;
                }
            } finally {
                synchronized (query.session) { fetching = false; query.session.notifyAll(); }
            }
        }
        private void closeAfterFetchFailure() throws SQLException {
            synchronized (query.session) { fetching = false; query.session.notifyAll(); }
            close();
        }
        int index(Object argument) throws SQLException {
            if (argument instanceof Integer n && n >= 1 && n <= columns.length) return n - 1;
            if (argument instanceof String name) {
                for (int i = 0; i < columns.length; i++) if (columns[i].name().equalsIgnoreCase(name)) return i;
            }
            throw new SQLException("Invalid result column.", "07009");
        }
        NativeEngine.Value value(Object argument) throws SQLException {
            int column = index(argument);
            if (terminalFailure != null) throw terminalFailure;
            if (fetching || !onRow) throw new SQLException("ResultSet is not on a row.", "HY010");
            NativeEngine.Value value;
            try { value = cells.read(handle, column); }
            catch (SQLException e) {
                cells.close();
                terminalFailure = e; onRow = false; read = false;
                throw e;
            }
            if (value.type() != columns[column].nativeType()) throw new SQLException("Native cell type mismatch.", "HY000");
            wasNull = value.flag();
            read = true;
            return value;
        }
        NativeEngine.Value conversionValue(Object argument) throws SQLException {
            NativeEngine.Value value = value(argument);
            // Dynamic is JSON text, not a scalar conversion source, even for null.
            if (value.type() == 10) throw Values.malformed();
            return value;
        }
        boolean isValueGetter(String name) {
            return switch (name) {
                case "getString", "getBoolean", "getBigDecimal", "getInt", "getLong", "getDouble", "getTimestamp", "getObject" -> true;
                default -> false;
            };
        }
        static Object convert(String name, NativeEngine.Value v) throws SQLException {
            return switch (name) {
                case "getString" -> {
                    yield v.flag() ? null : v.type() == 6 ? Values.datetimeText(v.text()) : v.text();
                }
                case "getBoolean" -> {
                    if (v.flag()) yield false;
                    if (v.type() != 1 && v.type() != 9) throw Values.malformed();
                    yield Values.bool(v.text(), v.type() == 9);
                }
                case "getBigDecimal" -> {
                    if (v.flag()) yield null;
                    if (v.type() != 5 && v.type() != 2 && v.type() != 3 && v.type() != 9) throw Values.malformed();
                    yield Values.decimal(v.text());
                }
                case "getInt" -> {
                    if (v.flag()) yield 0;
                    if (v.type() != 2 && v.type() != 3 && v.type() != 9) throw Values.malformed();
                    yield integer(v.text());
                }
                case "getLong" -> {
                    if (v.flag()) yield 0L;
                    if (v.type() != 2 && v.type() != 3 && v.type() != 9) throw Values.malformed();
                    yield Values.longInteger(v.text());
                }
                case "getDouble" -> {
                    if (v.flag()) yield 0.0;
                    if (v.type() != 4 && v.type() != 9) throw Values.malformed();
                    yield Values.real(v.text());
                }
                case "getTimestamp" -> {
                    if (v.flag()) yield null;
                    if (v.type() != 6) throw Values.malformed();
                    yield Values.timestamp(v.text());
                }
                case "getObject" -> {
                    if (v.flag()) yield null;
                    yield switch (v.type()) {
                        case 1 -> Values.bool(v.text(), false);
                        case 5 -> Values.decimal(v.text());
                        case 2 -> integer(v.text());
                        case 3 -> Values.longInteger(v.text());
                        case 4 -> Values.real(v.text());
                        case 6 -> Values.timestamp(v.text());
                        case 9, 10 -> v.text();
                        default -> throw unsupported();
                    };
                }
                default -> throw unsupported();
            };
        }
        Object call(Method method, Object[] args) throws SQLException {
            return switch (method.getName()) {
                case "findColumn" -> index(args[0]) + 1;
                case "wasNull" -> {
                    if (!read) throw new SQLException("No current getter value.", "HY010");
                    yield wasNull;
                }
                case "getMetaData" -> {
                    if (metadata == null) metadata = new Metadata(this).proxy(ResultSetMetaData.class);
                    yield metadata;
                }
                case "getStatement" -> query.self;
                case "getType" -> ResultSet.TYPE_FORWARD_ONLY;
                case "getConcurrency" -> ResultSet.CONCUR_READ_ONLY;
                case "getHoldability" -> ResultSet.CLOSE_CURSORS_AT_COMMIT;
                default -> throw unsupported();
            };
        }
    }

    static int integer(String text) throws SQLException {
        return Values.integer(text);
    }

    static final class Metadata extends ObjectHandler {
        final Rows rows;
        Metadata(Rows rows) { this.rows = rows; }
        Object lock() { return rows.lock(); }
        boolean closed() { return rows.closed(); }
        void checkOpen() throws SQLException { rows.checkOpen(); }
        void close() throws SQLException { throw unsupported(); }
        Object call(Method method, Object[] args) throws SQLException {
            if (method.getName().equals("getColumnCount")) return rows.columns.length;
            // Only column-indexed metadata methods reach this point.
            if (args.length != 1 || !(args[0] instanceof Integer)) throw unsupported();
            Column column = rows.columns[rows.index(args[0])];
            return switch (method.getName()) {
                case "getColumnName", "getColumnLabel" -> column.name();
                case "getColumnType" -> column.sqlType();
                case "getColumnTypeName" -> column.typeName();
                case "getColumnClassName" -> switch (column.nativeType()) {
                    case 1 -> Boolean.class.getName(); case 5 -> java.math.BigDecimal.class.getName();
                    case 2 -> Integer.class.getName(); case 3 -> Long.class.getName(); case 4 -> Double.class.getName();
                    case 6 -> Timestamp.class.getName(); case 9, 10 -> String.class.getName(); default -> throw unsupported(); };
                case "isNullable" -> column.nullable() ? ResultSetMetaData.columnNullable : ResultSetMetaData.columnNoNulls;
                case "isReadOnly" -> true;
                case "isWritable", "isDefinitelyWritable", "isAutoIncrement" -> false;
                case "isSigned" -> column.nativeType() == 2 || column.nativeType() == 3 || column.nativeType() == 4 || column.nativeType() == 5;
                default -> throw unsupported();
            };
        }
    }

    private static SQLException collect(SQLException first, SQLException next) {
        if (first == null) return next;
        first.addSuppressed(next);
        return first;
    }
    // Called with monitor held. wait releases it. Preserve interruption without
    // abandoning ownership cleanup or turning an interrupted wait into a spin.
    private static void await(Object monitor, java.util.function.BooleanSupplier done) {
        boolean interrupted = false;
        while (!done.getAsBoolean()) {
            try { monitor.wait(); }
            catch (InterruptedException e) { interrupted = true; }
        }
        if (interrupted) Thread.currentThread().interrupt();
    }
}
