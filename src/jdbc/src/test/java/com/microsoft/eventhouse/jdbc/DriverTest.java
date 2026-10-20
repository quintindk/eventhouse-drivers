package com.microsoft.eventhouse.jdbc;

import com.sun.jna.Native;
import com.sun.jna.Pointer;
import com.sun.jna.ptr.ByteByReference;
import com.sun.jna.ptr.IntByReference;
import com.sun.jna.ptr.LongByReference;
import java.lang.reflect.Proxy;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.net.URLClassLoader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.charset.StandardCharsets;
import java.sql.*;
import java.util.Properties;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.FutureTask;
import java.util.concurrent.TimeUnit;

/** Direct offline tests only. Scripted native replies never enter the product JAR. */
public final class DriverTest {
    private static int passed;
    private static final String HOST = "synthetic-test.kusto.fabric.microsoft.com";
    private static final String URL = Configuration.PREFIX + "//" + HOST + "/test";
    private DriverTest() { }
    public static void main(String[] args) throws Exception {
        Driver driver = DriverManager.getDriver(URL);
        test(driver instanceof EventhouseDriver);
        test(driver.acceptsURL(URL));
        test(!driver.acceptsURL(null));
        test(driver.connect("jdbc:other:test", new Properties()) == null);
        for (String bad : new String[] {"?secret=x", "#fragment", "/extra", "/", "\\x"})
            test(!driver.acceptsURL(URL + bad));
        test(!driver.acceptsURL(URL.replace("//", "//user@")));
        test(!driver.acceptsURL(URL.replace(HOST, HOST + ":443")));
        test(!driver.acceptsURL(URL.replace("/test", "/..")));
        String base = Configuration.PREFIX + "//" + HOST + "/";
        for (String[] pair : new String[][] {{"my%20database", "my database"}, {"a+b", "a+b"},
                {"a%2Bb", "a+b"}, {"caf%C3%A9", "café"}, {"%E4%B8%96%E7%95%8C", "世界"},
                {"café", "café"}, {"%252F", "%2F"}, {"%25", "%"}, {"a%3Fb%23c", "a?b#c"}}) {
            test(driver.acceptsURL(base + pair[0]));
            test(Configuration.database(base + pair[0]).equals(pair[1]));
        }
        for (String bad : new String[] {"", "my database", "%", "%2", "%GG", "%FF", "%C0%AF",
                "%E2%82", "%ED%A0%80", "%2f", "a%2Fb", "%5c", "%00", "%01", "%09", "%0A",
                "%7F", "%C2%85", "%20", "%20%20", ".", "..", "%2e", ".%2E", "%2e%2e",
                "a/b", "%F4%90%80%80"}) {
            test(!driver.acceptsURL(base + bad));
            state("08001", () -> Configuration.database(base + bad));
        }
        test(Configuration.database(base + "%C3%A9".repeat(128)).equals("é".repeat(128)));
        test(Configuration.database(base + "a".repeat(256)).length() == 256);
        state("08001", () -> Configuration.database(base + "%C3%A9".repeat(129)));
        state("08001", () -> Configuration.database(base + "a".repeat(257)));
        Properties p = properties();
        test(Configuration.parse(URL, p).timeout() == 30000);
        test(!Configuration.parse(URL, p).toString().contains("dummy-secret"));
        for (String timeout : new String[] {"0", "-1", "600001", "abc", "999999999999", "+1"}) {
            p.setProperty("timeoutMillis", timeout);
            state("08001", () -> Configuration.parse(URL, p));
        }
        p.remove("timeoutMillis");
        p.setProperty("password", "never-echo");
        state("08001", () -> Configuration.parse(URL, p));
        p.remove("password");
        p.setProperty("nativeLibrary", "relative.dll");
        state("08001", () -> Configuration.parse(URL, p));
        p.setProperty("nativeLibrary", missingLibrary());
        state("08001", () -> driver.connect(URL, p));
        NativeEngine.requireSystemLoaders();
        test(EventhouseDriver.class.getClassLoader() == ClassLoader.getSystemClassLoader());
        test(Native.class.getClassLoader() == ClassLoader.getSystemClassLoader());
        rejectedLoader(false);
        rejectedLoader(true);
        for (String root : new String[] {"retainedApi", "retained", "loadedPath", "instance"}) {
            var field = NativeEngine.class.getDeclaredField(root);
            field.setAccessible(true);
            test(field.get(null) == null); // Policy and missing-file failures must not load or root native code.
        }
        state("0A000", driver::getParentLogger);
        test(Native.getNativeSize(SpikeLibrary.SizeT.class) == Native.SIZE_T_SIZE);
        test(new SpikeLibrary.SizeRef().value() == 0);
        test(SpikeLibrary.class.getDeclaredMethod("eh_open_v1", byte[].class, SpikeLibrary.SizeT.class,
                byte[].class, SpikeLibrary.SizeT.class, byte[].class, SpikeLibrary.SizeT.class,
                byte[].class, SpikeLibrary.SizeT.class, byte[].class, SpikeLibrary.SizeT.class,
                int.class, LongByReference.class).getReturnType() == int.class);
        test(JdbcObjects.integer("2147483647") == Integer.MAX_VALUE);
        test(JdbcObjects.integer("-2147483648") == Integer.MIN_VALUE);
        state("22003", () -> JdbcObjects.integer("2147483648"));
        state("22018", () -> JdbcObjects.integer("1.5"));
        state("22018", () -> JdbcObjects.integer("not-a-number"));
        state("08001", () -> Utf8.encode("\ud800", 20, "08001"));
        state("HY000", () -> Utf8.decode(new byte[] {(byte) 0xff}));
        for (int status = 1; status <= 9; status++) {
            final int s = status;
            try { NativeEngine.check(s); throw new AssertionError(); }
            catch (SQLException e) { test(e.getErrorCode() == s && !e.getMessage().contains("never-echo")); }
        }
        Script script = new Script();
        Connection connection = JdbcObjects.connection(new NativeEngine(script.api()), 1);
        test(connection.isReadOnly() && connection.getAutoCommit());
        test(connection.getTransactionIsolation() == Connection.TRANSACTION_NONE);
        state("0A000", () -> connection.prepareStatement("SELECT ?"));
        state("0A000", connection::getMetaData);
        state("0A000", connection::commit);
        state("0A000", () -> connection.setAutoCommit(false));
        state("0A000", () -> connection.setClientInfo("name", "value"));
        Statement statement = connection.createStatement();
        statement.cancel(); test(statement.getQueryTimeout() == 0);
        statement.setQueryTimeout(1); test(statement.getQueryTimeout() == 1);
        state("0A000", () -> statement.execute("SELECT 1"));
        ResultSet rs = statement.executeQuery("SELECT 1");
        state("HY010", () -> rs.getInt(1));
        ResultSetMetaData metadata = rs.getMetaData();
        test(metadata.getColumnCount() == 3);
        test(metadata.getColumnType(1) == Types.INTEGER);
        test(metadata.getColumnName(1).equals("integer"));
        test(metadata.getColumnType(2) == Types.VARCHAR);
        state("07009", () -> metadata.getColumnName(0));
        test(rs.next());
        test(rs.getInt(1) == 1 && !rs.wasNull());
        test(rs.getInt("INTEGER") == 1);
        test(rs.getString("integer").equals("1"));
        test(rs.getString(2) == null && rs.wasNull());
        test(rs.getInt(2) == 0 && rs.wasNull());
        test(rs.getString(3).isEmpty() && !rs.wasNull());
        script.text = "héllo 世界";
        clearScriptCell(rs);
        test(rs.getString(3).equals("héllo 世界"));
        script.text = "2147483648";
        clearScriptCell(rs);
        state("22003", () -> rs.getInt(3));
        script.text = "1.5";
        clearScriptCell(rs);
        state("22018", () -> rs.getInt(3));
        script.text = "";
        state("07009", () -> rs.getString("missing"));
        test(rs.getLong(1) == 1L); // Integer columns support exact long conversion.
        ResultSet replacement = statement.executeQuery("SELECT 1");
        test(rs.isClosed() && script.resultCloses == 1);
        state("HY010", rs::next);
        test(replacement.next() && !replacement.next() && !replacement.next());
        state("HY010", () -> replacement.getString(1));
        connection.close(); connection.close(); statement.close(); replacement.close();
        test(script.resultCloses == 2 && script.connectionCloses == 1);
        test(connection.isClosed() && statement.isClosed() && replacement.isClosed());
        state("08003", connection::createStatement);
        state("08003", () -> connection.setClientInfo("name", "value"));
        state("HY010", () -> statement.executeQuery("SELECT 1"));
        state("HY010", metadata::getColumnCount);
        Script failureScript = new Script();
        Connection failureConnection = JdbcObjects.connection(new NativeEngine(failureScript.api()), 1);
        Statement failureStatement = failureConnection.createStatement();
        ResultSet old = failureStatement.executeQuery("SELECT 1");
        failureScript.executeStatus = 6;
        state("HY000", () -> failureStatement.executeQuery("SELECT 1"));
        test(old.isClosed() && failureScript.resultCloses == 1 && failureStatement.getResultSet() == null);
        failureScript.executeStatus = 0;
        failureScript.type = 7; // Timespan remains unsupported. Dynamic is now supported.
        state("0A000", () -> failureStatement.executeQuery("SELECT 1"));
        test(failureScript.resultCloses == 2);
        failureConnection.close();
        Script duplicates = new Script();
        duplicates.names = new String[] {"Name", "name", "NAME"};
        duplicates.text = "99";
        try (Connection c = JdbcObjects.connection(new NativeEngine(duplicates.api()), 1);
             Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT 1")) {
            test(r.next());
            for (String name : new String[] {"Name", "name", "NAME", "nAmE"}) {
                test(r.findColumn(name) == 1);
                test(r.getString(name).equals("1"));
                test(r.getInt(name) == 1 && !r.wasNull());
            }
        }
        test(passed == 184); // Keep the full preceding driver regression coverage.
        typedValues();
        test(passed == 274); // All prior regression assertion positions retained.
        operations();
        if (passed != 360) throw new AssertionError("Prior regression count changed");
        booleanDecimal();
        if (passed != 547) throw new AssertionError("Prior Boolean/decimal regression count changed");
        fabricEndpoints(driver);
        if (passed != 702) throw new AssertionError("Prior endpoint regression count changed");
        productBindings();
        if (passed != 740) throw new AssertionError("Cursor product-binding regression count changed");
        dynamicValues();
        streaming();
        cellCache();
        numericBounds();
        numericAdmission();
        retirementRecovery();
        admissionRaces();
        for (String url : new String[] {"jdbc:eventhouse:", "jdbc:eventhouse:/test",
                "jdbc:eventhouse:///test", "jdbc:eventhouse:// /test"}) {
            test(!driver.acceptsURL(url));
            state("08001", () -> driver.connect(url, properties()));
            try {
                Configuration.parse(url, properties());
                throw new AssertionError("Missing host admitted");
            } catch (SQLException expected) {
                test(expected.getMessage().equals("Invalid Eventhouse JDBC configuration."));
            }
        }
        System.out.println("DriverTest PASS assertions=" + passed + " failures=0 (offline, scripted native boundary)");
        if (args.length == 1) {
            Properties nativeProperties = properties();
            nativeProperties.setProperty("nativeLibrary", Path.of(args[0]).toAbsolutePath().toString());
            try (Connection c = driver.connect(base + "synthetic%20database", nativeProperties)) {
                test(!c.isClosed());
            }
            for (String host : new String[] {"a.kusto.fabric.microsoft.com", "second-region.cluster-2.kusto.fabric.microsoft.com"}) {
                String nativeUrl = Configuration.PREFIX + "//" + host + "/synthetic%20database";
                try (Connection c = driver.connect(nativeUrl, nativeProperties)) { test(!c.isClosed()); }
            }
            NativeEngine engine = NativeEngine.load(Path.of(args[0]).toAbsolutePath());
            state("HY010", () -> engine.next(0));
            state("HY010", () -> engine.read(0, 0, 0));
            long connectionHandle = engine.open(Configuration.parse(base + "synthetic%20database", nativeProperties));
            long operation = engine.prepare(connectionHandle, 0);
            try { engine.cancelOperation(operation); }
            finally { engine.closeOperation(operation); engine.closeConnection(connectionHandle); }
            System.out.println("NativeSmoke PASS product ABI0x00010003/all16symbols, configured-host open/close, invalid-handle next/current-cell and prepare/cancel/close. Dummy credentials, no query or network. assertions=" + passed);
        }
    }
    static Properties properties() {
        Properties p = new Properties();
        p.setProperty("tenantId", "11111111-1111-1111-1111-111111111111");
        p.setProperty("clientId", "22222222-2222-2222-2222-222222222222");
        p.setProperty("clientSecret", "dummy-secret");
        p.setProperty("nativeLibrary", missingLibrary());
        return p;
    }
    static void productBindings() throws Exception {
        java.util.Set<String> expected = java.util.Set.of("eh_abi_version_v1", "eh_open_v1", "eh_execute_v1",
                "eh_result_shape_v1", "eh_result_column_v1", "eh_result_cell_v1", "eh_connection_close_v1",
                "eh_result_close_v1", "eh_error_code_v1", "eh_error_message_v1", "eh_operation_prepare_v1",
                "eh_operation_execute_v1", "eh_operation_cancel_v1", "eh_operation_close_v1",
                "eh_result_next_v1", "eh_result_current_cell_v1");
        java.util.Set<String> declared = new java.util.HashSet<>();
        for (var method : SpikeLibrary.class.getDeclaredMethods()) {
            declared.add(method.getName());
            test(method.getReturnType() == int.class);
        }
        test(declared.equals(expected));
        for (int version : new int[] {0, 0x00010000, 0x00010001, -1}) {
            SpikeLibrary library = versionLibrary(version);
            state("08001", () -> NativeEngine.validateProduct(library, name -> {
                throw new AssertionError("Resolved symbols before rejecting ABI");
            }));
        }
        java.util.Set<String> resolved = new java.util.HashSet<>();
        NativeEngine.validateProduct(versionLibrary(0x00010003), resolved::add);
        test(resolved.equals(expected));
        for (String missing : expected) {
            state("08001", () -> NativeEngine.validateProduct(versionLibrary(0x00010003), name -> {
                if (name.equals(missing)) throw new UnsatisfiedLinkError("PRIVATE_SENTINEL");
            }));
        }
    }
    static SpikeLibrary versionLibrary(int version) {
        return (SpikeLibrary) Proxy.newProxyInstance(DriverTest.class.getClassLoader(), new Class<?>[] {SpikeLibrary.class},
                (proxy, method, args) -> {
                    if (!method.getName().equals("eh_abi_version_v1")) throw new AssertionError("Unexpected native admission call");
                    return version;
                });
    }
    static void dynamicValues() throws Exception {
        Script script = new Script();
        script.types = new int[] {10, 9, 10};
        script.names = new String[] {"Payload", "plain_text", "other_json"};
        script.cells = new String[] {"{}", "null", "\"null\""};
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT dynamic_values")) {
            ResultSetMetaData m = r.getMetaData();
            test(m.getColumnCount() == 3);
            for (int column : new int[] {1, 3}) {
                test(m.getColumnType(column) == Types.LONGVARCHAR);
                test(m.getColumnTypeName(column).equals("dynamic"));
                test(m.getColumnClassName(column).equals(String.class.getName()));
                test(!m.isSigned(column));
                test(m.isNullable(column) == ResultSetMetaData.columnNullable);
                test(m.isReadOnly(column) && !m.isWritable(column) && !m.isDefinitelyWritable(column));
                state("0A000", () -> m.getPrecision(column));
                state("0A000", () -> m.getScale(column));
                state("0A000", () -> m.getSchemaName(column));
            }
            test(m.getColumnName(1).equals("Payload") && m.getColumnLabel(1).equals("Payload"));
            test(m.getColumnType(2) == Types.VARCHAR && m.getColumnTypeName(2).equals("string"));
            state("HY010", () -> r.getString(1));
            state("HY010", () -> r.getObject("payload"));
            test(r.findColumn("pAyLoAd") == 1);
            test(r.next());
            state("HY010", r::wasNull);
            // Literal expected JSON text. No parser/canonicaliser or floating point oracle.
            String[] json = {"{}", "{\"z\":1, \"a\":[true,null,\"x\"]}", "[]", "[1, false, {\"x\":2}]",
                    "\"\"", "\"null\"", "\"text\"", "\"quote\\\" slash\\\\ line\\n tab\\t\"",
                    "\"Grüße 東京 😀\"", "\"\\u4e16\\u754c\\uD83D\\uDE00\"",
                    "0", "-0", "2147483648", "9007199254740993", "18446744073709551615",
                    "123456789012345678901234567890.123456789012345678900", "1.2300e+20", "1e-1000",
                    "true", "false", "\"2026-10-16T12:34:56Z\""};
            for (String text : json) {
                script.cells[0] = text;
                clearScriptCell(r);
                test(r.getString(1).equals(text) && !r.wasNull());
                test(r.getString("PAYLOAD").equals(text) && !r.wasNull());
                Object object = r.getObject(1);
                test(object instanceof String && object.equals(text) && !r.wasNull());
                test(r.getObject("payload").equals(text) && !r.wasNull());
                dynamicConversions(r);
            }
            test(r.getString(2).equals("null") && !r.wasNull());
            test(r.getObject(3).equals("\"null\"") && !r.wasNull());
            script.nullCells = true;
            clearScriptCell(r);
            test(r.getString(1) == null && r.wasNull());
            test(r.getString("payload") == null && r.wasNull());
            test(r.getObject(1) == null && r.wasNull());
            test(r.getObject("PAYLOAD") == null && r.wasNull());
            dynamicConversions(r);
            script.nullCells = false;
            script.cells[0] = "\"null\"";
            clearScriptCell(r);
            test(r.getString(1).equals("\"null\"") && !r.wasNull());
            test(r.getObject("payload").equals("\"null\"") && !r.wasNull());
            state("07009", () -> r.getString("missing"));
            state("07009", () -> r.getObject(0));
            state("0A000", () -> r.getObject(1, String.class));
            state("0A000", () -> r.getObject("payload", java.util.Map.of()));
            state("0A000", () -> r.getArray(1));
            state("0A000", () -> r.getSQLXML("payload"));
            test(!r.next());
            state("HY010", r::wasNull);
            state("HY010", () -> r.getString(1));
            state("HY010", () -> r.getObject("payload"));
        }
        test(script.resultCloses == 1 && script.connectionCloses == 1 && script.operations.isEmpty());
        // getInt must use its approved source types, not merely parse any native text.
        script.types = new int[] {4, 6, 10};
        script.cells = new String[] {"1", "2026-10-16T12:34:56Z", "1"};
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT conversion_sources")) {
            test(r.next());
            for (int column = 1; column <= 3; column++) {
                final int index = column;
                state("22018", () -> r.getInt(index));
                state("22018", () -> r.getInt(script.names[index - 1]));
            }
        }
    }
    static void dynamicConversions(ResultSet r) throws Exception {
        state("22018", () -> r.getInt(1));
        state("22018", () -> r.getInt("payload"));
        state("22018", () -> r.getLong(1));
        state("22018", () -> r.getLong("payload"));
        state("22018", () -> r.getDouble(1));
        state("22018", () -> r.getDouble("payload"));
        state("22018", () -> r.getBoolean(1));
        state("22018", () -> r.getBoolean("payload"));
        state("22018", () -> r.getTimestamp(1));
        state("22018", () -> r.getTimestamp("payload"));
        state("22018", () -> r.getBigDecimal(1));
        state("22018", () -> r.getBigDecimal("payload"));
    }
    static void streaming() throws Exception {
        test(new NativeEngine(new Script().api()).shape(2)[1] == -1L);
        state("08001", () -> NativeEngine.validateProduct(versionLibrary(0x00010002), name -> {
            throw new AssertionError("Old buffered ABI admitted");
        }));
        for (int total : new int[] {0, 3}) {
            Script rows = new Script();
            rows.totalRows = total;
            try (Connection c = JdbcObjects.connection(new NativeEngine(rows.api()), 1);
                 Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT rows")) {
                for (int i = 0; i < total; i++) {
                    test(r.next());
                    test(r.getInt(1) == 1 && r.getInt(1) == 1);
                }
                test(!r.next() && !r.next() && rows.nextCalls == total + 1);
                test(rows.operations.isEmpty());
            }
        }
        Script script = new Script();
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); Statement other = c.createStatement()) {
            ResultSet r = s.executeQuery("SELECT 1");
            test(script.operationCloses == 0 && script.operations.size() == 1);
            test(r.getMetaData().getColumnCount() == 3 && script.nextCalls == 0);
            state("HY010", () -> other.executeQuery("SELECT 1"));
            state("HY010", () -> r.getString(1));
            test(r.next());
            test(r.getString(1).equals("1") && r.getString(1).equals("1") && script.nextCalls == 1);
            test(!r.next() && script.operationCloses == 1 && script.operations.isEmpty());
            test(!r.next() && script.nextCalls == 2 && !r.isClosed());
            test(r.getMetaData().getColumnCount() == 3);
            state("HY010", () -> r.getInt(1));
            try (ResultSet reused = other.executeQuery("SELECT 1")) { test(reused.next()); }
            r.close();
            for (int status : new int[] {7, 5, 8, 10}) {
                script.nextStatus = 0;
                ResultSet late = s.executeQuery("SELECT 1");
                test(late.next());
                script.nextStatus = status;
                SQLException failure;
                try { late.next(); throw new AssertionError("Late failure returned EOF"); }
                catch (SQLException e) { failure = e; }
                test(failure.getErrorCode() == status);
                test(failure.getSQLState().equals(status == 8 ? "HY008" : status == 10 ? "HYT00" : status == 5 ? "08006" : "HY000"));
                test((failure instanceof SQLTimeoutException) == (status == 10));
                try { late.next(); throw new AssertionError("Lost latched failure"); }
                catch (SQLException e) { test(e == failure); }
                try { late.getString(1); throw new AssertionError("Lost getter terminal failure"); }
                catch (SQLException e) { test(e == failure); }
                test(script.operations.isEmpty() && late.getMetaData().getColumnCount() == 3);
                script.nextStatus = 0;
                try (ResultSet reused = other.executeQuery("SELECT 1")) { test(reused.next()); }
                late.close();
            }
        }
        for (int mode = 0; mode < 4; mode++) blockedFetch(mode);
        fetchSnapshotGap();
        for (int status : new int[] {8, 10, 7}) {
            idleGetterFailure(status, false);
            idleGetterFailure(status, true);
        }
        test(TransactionsSmoke.countReturnedUtf8(0, null) == 0);
        test(TransactionsSmoke.countReturnedUtf8(17, "") == 17);
        test(TransactionsSmoke.countReturnedUtf8(0, "é世界😀") == 12);
        test(TransactionsSmoke.countReturnedUtf8(4_194_304L, "a") == 4_194_305L);
        try { TransactionsSmoke.countReturnedUtf8(Long.MAX_VALUE, "a"); throw new AssertionError("Count overflow"); }
        catch (ArithmeticException expected) { test(true); }
    }
    static void idleGetterFailure(int status, boolean betweenSizingAndCopy) throws Exception {
        Script script = new Script();
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); Statement other = c.createStatement()) {
            ResultSet r = s.executeQuery("SELECT 1");
            test(r.next());
            if (betweenSizingAndCopy) script.afterCellSizing = () -> script.terminalStatus = status;
            else if (status == 8) s.cancel();
            else script.terminalStatus = status; // Native idle deadline/protocol terminal decision, no next.
            int nextCalls = script.nextCalls;
            SQLException failure;
            try { r.getString(1); throw new AssertionError("Idle getter returned a stale row"); }
            catch (SQLException e) { failure = e; }
            test(failure.getErrorCode() == status);
            test(failure.getSQLState().equals(status == 8 ? "HY008" : status == 10 ? "HYT00" : "HY000"));
            test((failure instanceof SQLTimeoutException) == (status == 10));
            test(script.nextCalls == nextCalls && script.operations.isEmpty() && script.operationCloses == 1);
            test(script.cellSizingCalls == 1 && script.cellCopyCalls == (betweenSizingAndCopy ? 1 : 0));
            for (int i = 0; i < 2; i++) {
                try { r.next(); throw new AssertionError("Terminal next returned EOF"); }
                catch (SQLException e) { test(e == failure); }
                try { r.getString(1); throw new AssertionError("Terminal getter returned stale row"); }
                catch (SQLException e) { test(e == failure); }
            }
            test(script.nextCalls == nextCalls && script.operationCloses == 1);
            test(r.getMetaData().getColumnCount() == 3);
            script.afterCellSizing = null;
            try (ResultSet reused = other.executeQuery("SELECT 1")) { test(reused.next()); }
            r.close();
            test(script.operationCloses == 2 && script.operations.isEmpty());
        }
    }
    @SuppressWarnings("try") // Verify explicit repeated close releases scratch once.
    static void cellCache() throws Exception {
        Script script = new Script();
        script.totalRows = 2;
        script.cells = new String[] {"123456789", "x", ""};
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT cells")) {
            JdbcObjects.Rows rows = (JdbcObjects.Rows) Proxy.getInvocationHandler(r);
            test(r.next());
            test(r.getString(1).equals("123456789"));
            test(script.cellSizingCalls == 1 && script.cellCopyCalls == 1 && rows.cells.capacity() == 9);
            var scratchField = NativeEngine.CellReader.class.getDeclaredField("scratch");
            scratchField.setAccessible(true);
            com.sun.jna.Memory allocation = (com.sun.jna.Memory) scratchField.get(rows.cells);
            test(r.getString("INTEGER").equals("123456789"));
            test(r.getObject(1).equals(123456789));
            test(script.cellSizingCalls == 3 && script.cellCopyCalls == 1);
            test(r.getString(2).equals("x"));
            test(script.cellSizingCalls == 3 && script.cellCopyCalls == 2 && rows.cells.capacity() == 9);
            test(r.getString(3).isEmpty()); // Short/empty copies must not expose trailing scratch bytes.
            test(script.cellSizingCalls == 3 && script.cellCopyCalls == 3);
            test(r.getString(1).equals("123456789")); // Only one column is cached.
            test(script.cellCopyCalls == 4);
            test(r.next());
            test(r.getString(1).equals("123456789") && script.cellCopyCalls == 5);
            script.terminalStatus = 10;
            state("HYT00", () -> r.getString("integer")); // Cached text cannot bypass native terminal state.
            test(script.cellCopyCalls == 5 && rows.cells.capacity() == 0 && script.operations.isEmpty());
            test(!allocation.valid());
            r.close(); r.close();
            test(rows.cells.capacity() == 0 && script.resultCloses == 1 && script.operationCloses == 1);
        }
        for (int mismatch = 0; mismatch < 3; mismatch++) {
            Script changed = new Script();
            changed.cells = new String[] {"1", "x", ""};
            try (Connection c = JdbcObjects.connection(new NativeEngine(changed.api()), 1);
                 Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT mismatch")) {
                test(r.next() && r.getString(1).equals("1"));
                if (mismatch == 0) changed.cells[0] = "12";
                else if (mismatch == 1) changed.types = new int[] {9, 9, 9};
                else changed.nullCells = true;
                state("HY000", () -> r.getString(1));
                test(((JdbcObjects.Rows) Proxy.getInvocationHandler(r)).cells.capacity() == 0);
                test(changed.operations.isEmpty() && changed.operationCloses == 1);
            }
        }
        for (int status : new int[] {8, 10, 7}) {
            Script idle = new Script();
            try (Connection c = JdbcObjects.connection(new NativeEngine(idle.api()), 1);
                 Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT idle")) {
                test(r.next() && r.getString(1).equals("1"));
                int copies = idle.cellCopyCalls;
                if (status == 8) s.cancel(); else idle.terminalStatus = status;
                String state = status == 8 ? "HY008" : status == 10 ? "HYT00" : "HY000";
                state(state, () -> r.getString("integer"));
                state(state, r::next);
                test(idle.cellCopyCalls == copies && idle.nextCalls == 1 && idle.operationCloses == 1);
            }
        }
        Script bounds = new Script();
        bounds.types = new int[] {9, 9, 9};
        bounds.cells = new String[] {"a", "a".repeat(4 * 1024 * 1024), "b".repeat(4 * 1024 * 1024 + 1)};
        try (Connection c = JdbcObjects.connection(new NativeEngine(bounds.api()), 1);
             Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT bounds")) {
            JdbcObjects.Rows rows = (JdbcObjects.Rows) Proxy.getInvocationHandler(r);
            test(r.next() && r.getString(1).equals("a"));
            var scratchField = NativeEngine.CellReader.class.getDeclaredField("scratch");
            scratchField.setAccessible(true);
            com.sun.jna.Memory small = (com.sun.jna.Memory) scratchField.get(rows.cells);
            test(r.getString(2).length() == 4 * 1024 * 1024);
            test(!small.valid() && rows.cells.capacity() == 4 * 1024 * 1024);
            com.sun.jna.Memory maximum = (com.sun.jna.Memory) scratchField.get(rows.cells);
            SQLException failure;
            try { r.getString(3); throw new AssertionError("Oversized representation returned a value"); }
            catch (SQLException e) { failure = e; }
            test("HY000".equals(failure.getSQLState()));
            test(!maximum.valid() && rows.cells.capacity() == 0 && bounds.operations.isEmpty());
            test(bounds.operationCloses == 1 && !r.isClosed());
            int nextCalls = bounds.nextCalls;
            for (int i = 0; i < 2; i++) {
                for (VoidChecked read : new VoidChecked[] {r::next, () -> r.getString(3), () -> r.getString("empty_text")}) {
                    try { read.run(); throw new AssertionError("Lost oversized representation terminal failure"); }
                    catch (SQLException e) { test(e == failure); }
                }
            }
            test(bounds.nextCalls == nextCalls && bounds.operationCloses == 1);
            // A separate statement must recover without closing the failed result first.
            try (Statement other = c.createStatement(); ResultSet recovered = other.executeQuery("SELECT recovery")) {
                test(!r.isClosed() && recovered.next());
                test("a".equals(recovered.getString(1)));
                test(!recovered.next());
            }
            test(bounds.operationCloses == 2 && bounds.operations.isEmpty());
            r.close(); r.close();
            test(r.isClosed() && bounds.resultCloses == 2 && bounds.operationCloses == 2);
        }
    }
    static void blockedFetch(int mode) throws Exception {
        Script script = new Script();
        CountDownLatch entered = new CountDownLatch(1), release = new CountDownLatch(1);
        script.beforeNext = () -> { entered.countDown(); barrier(release); };
        Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
        Statement s = c.createStatement();
        ResultSet r = s.executeQuery("SELECT 1");
        try {
            FutureTask<String> fetch = task(() -> {
                try { r.next(); return "OK"; } catch (SQLException e) { return e.getSQLState(); }
            });
            barrier(entered);
            state("HY010", r::next);
            state("HY010", () -> r.getString(1));
            test(r.getMetaData().getColumnCount() == 3);
            FutureTask<Void> action = task(() -> {
                if (mode == 0) s.cancel(); else if (mode == 1) s.close(); else if (mode == 2) c.close(); else r.close();
                return null;
            });
            if (mode == 0) finish(action);
            else {
                JdbcObjects.Rows rows = (JdbcObjects.Rows) Proxy.getInvocationHandler(r);
                synchronized (rows.lock()) { waitState(rows.lock(), () -> rows.closed); }
            }
            test(!fetch.isDone());
            release.countDown();
            test(finish(fetch).equals(mode == 0 ? "HY008" : "HY010"));
            finish(action);
            test(script.operations.isEmpty());
            if (mode == 0) {
                r.close(); script.beforeNext = null;
                try (ResultSet reused = s.executeQuery("SELECT 1")) { test(reused.next()); }
            }
        } finally { release.countDown(); c.close(); }
    }
    static void fetchSnapshotGap() throws Exception {
        Script script = new Script();
        script.ignoreCancel = true;
        CountDownLatch captured = new CountDownLatch(1), admit = new CountDownLatch(1);
        script.beforeCancel = () -> { captured.countDown(); barrier(admit); };
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); Statement other = c.createStatement()) {
            ResultSet r = s.executeQuery("SELECT 1");
            test(r.next());
            FutureTask<Void> cancel = task(() -> { s.cancel(); return null; });
            barrier(captured);
            FutureTask<Boolean> eof = task(r::next);
            JdbcObjects.Query query = (JdbcObjects.Query) Proxy.getInvocationHandler(s);
            synchronized (query.session) {
                waitState(query.session, () -> query.active != null && query.active.retiring);
                test(query.active.signals == 1 && !query.active.retired);
            }
            state("HY010", () -> other.executeQuery("SELECT 1"));
            test(!eof.isDone() && script.operationCloses == 0);
            admit.countDown(); finish(cancel);
            test(!finish(eof) && script.cancelledHandle == 100 && script.operationCloses == 1);
            script.beforeCancel = null; script.ignoreCancel = false;
            try (ResultSet reused = other.executeQuery("SELECT 1")) { test(reused.next()); }
            test(script.cancelCalls == 1 && script.operations.isEmpty());
            r.close();
        } finally { admit.countDown(); }
    }
    static void fabricEndpoints(Driver driver) throws Exception {
        String suffix = ".kusto.fabric.microsoft.com";
        String longest = "a".repeat(63) + "." + "b".repeat(63) + "." + "c".repeat(63) + "." + "d".repeat(34) + suffix;
        test(longest.length() == 253);
        for (String host : new String[] {"a" + suffix, "second-region.cluster-2" + suffix,
                "0-1.2" + suffix, "a".repeat(63) + suffix, longest}) {
            String url = Configuration.PREFIX + "//" + host + "/caf%C3%A9%20db";
            test(driver.acceptsURL(url));
            Configuration config = Configuration.parse(url, properties());
            test(config.host().equals(host));
            test(config.database().equals("café db"));
            Script script = new Script();
            NativeEngine engine = new NativeEngine(script.api());
            long handle = engine.open(config);
            test(script.openUri.equals("https://" + host));
            test(script.openDatabase.equals("café db"));
            engine.closeConnection(handle);
            test(script.connectionCloses == 1);
        }
        for (String authority : new String[] {"kusto.fabric.microsoft.com", suffix, "a..b" + suffix,
                "-a" + suffix, "a-" + suffix, "a_b" + suffix, "a".repeat(64) + suffix,
                "x" + longest, "a" + suffix + ".", "a" + suffix + ".evil.example",
                "akusto.fabric.microsoft.com", "a.kusto.fabric.microsoft.comx", "127.0.0.1", "[::1]",
                "user@a" + suffix, "user:pass@a" + suffix, "a" + suffix + ":443", "a" + suffix + ":8443",
                "a" + suffix + ":", "A" + suffix, "a.KUSTO.fabric.microsoft.com", "%61" + suffix,
                "a%2eb" + suffix, "é" + suffix, "a\\b" + suffix, "a\t" + suffix,
                "a\n" + suffix, "a\0" + suffix}) {
            String url = Configuration.PREFIX + "//" + authority + "/test";
            test(!driver.acceptsURL(url));
            state("08001", () -> Configuration.parse(url, properties()));
            state("08001", () -> driver.connect(url, properties()));
        }
        for (String ending : new String[] {"?", "#", "?q=x", "#x", "/", "/extra", "%2F", "\\"}) {
            String url = Configuration.PREFIX + "//a" + suffix + "/test" + ending;
            test(!driver.acceptsURL(url));
            state("08001", () -> Configuration.parse(url, properties()));
        }
        for (int status : new int[] {4, 6, 13, 14}) {
            String expected = switch (status) { case 4 -> "28000"; case 13 -> "42501"; case 14 -> "42000"; default -> "HY000"; };
            state(expected, () -> NativeEngine.check(status));
            Script script = new Script();
            script.executeStatus = status;
            try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
                 Statement s = c.createStatement()) {
                try { s.executeQuery("QUERY_SENTINEL"); throw new AssertionError("Expected status propagation"); }
                catch (SQLException e) {
                    test(e.getSQLState().equals(expected) && e.getErrorCode() == status);
                    test(!(e instanceof SQLTimeoutException));
                    test(!e.getMessage().contains("QUERY_SENTINEL") && e.getCause() == null);
                }
                test(script.operations.isEmpty());
                script.executeStatus = 0;
                try (ResultSet r = s.executeQuery("SELECT 1")) { test(r.next()); }
            }
        }
    }
    @SuppressWarnings("deprecation") // Verify both deprecated scale overloads reject explicitly.
    static void booleanDecimal() throws Exception {
        Script script = new Script();
        script.types = new int[] {1, 5, 9};
        script.names = new String[] {"flag", "amount", "text"};
        String exact = "123456789012345678901234567890.123456789012345678900";
        BigDecimal expected = new BigDecimal(new BigInteger("123456789012345678901234567890123456789012345678900"), 21);
        script.cells = new String[] {"true", exact, "1"};
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT known_values")) {
            state("HY010", () -> r.getBoolean(1));
            state("HY010", () -> r.getBigDecimal(2));
            test(r.next());
            test(r.getBoolean(1) && r.getBoolean("FLAG") && !r.wasNull());
            test(r.getObject(1) instanceof Boolean && r.getObject("flag").equals(Boolean.TRUE));
            test(r.getString(1).equals("true"));
            test(r.getBigDecimal(2).equals(expected) && !r.wasNull());
            test(r.getBigDecimal("AMOUNT").equals(expected));
            test(r.getBigDecimal(2).precision() == 51 && r.getBigDecimal(2).scale() == 21);
            test(r.getObject(2) instanceof BigDecimal && r.getObject("amount").equals(expected));
            test(r.getString(2).equals(exact) && r.getString("amount").equals(exact));
            ResultSetMetaData metadata = r.getMetaData();
            test(metadata.getColumnType(1) == Types.BOOLEAN);
            test(metadata.getColumnTypeName(1).equals("bool"));
            test(metadata.getColumnClassName(1).equals(Boolean.class.getName()));
            test(!metadata.isSigned(1));
            test(metadata.getColumnType(2) == Types.DECIMAL);
            test(metadata.getColumnTypeName(2).equals("decimal"));
            test(metadata.getColumnClassName(2).equals(BigDecimal.class.getName()));
            test(metadata.isSigned(2));
            for (int column : new int[] {1, 2}) {
                state("0A000", () -> metadata.getPrecision(column));
                state("0A000", () -> metadata.getScale(column));
                state("0A000", () -> r.getBigDecimal(column, 2));
                state("0A000", () -> r.getObject(column, Object.class));
            }
            state("0A000", () -> r.getBigDecimal("amount", 2));
            script.cells[0] = "false";
            clearScriptCell(r);
            test(!r.getBoolean(1) && !r.getBoolean("flag") && !r.wasNull());
            test(r.getObject(1).equals(Boolean.FALSE));
            for (String text : new String[] {"true", "false", "0", "1"}) {
                script.cells[2] = text;
                clearScriptCell(r);
                boolean truth = text.equals("true") || text.equals("1");
                test(r.getBoolean(3) == truth && r.getBoolean("text") == truth && !r.wasNull());
            }
            for (String text : new String[] {"TRUE", "False", "yes", "2", "-1", "1.0", " true", "false ", "", "null", "\"true\""}) {
                script.cells[0] = text;
                script.cells[2] = text;
                clearScriptCell(r);
                state("22018", () -> r.getBoolean(1));
                state("22018", () -> r.getObject("flag"));
                state("22018", () -> r.getBoolean("text"));
            }
            for (String text : new String[] {"0", "1"}) {
                script.cells[0] = text;
                clearScriptCell(r);
                state("22018", () -> r.getBoolean(1));
                state("22018", () -> r.getObject(1));
            }
            script.cells[0] = "true";
            String[] decimals = {"1.2300", "-1.25E+3", "+.125", "1.", "0001.00", "1e-1000", "-0.000", "1E+1000"};
            BigDecimal[] numbers = {new BigDecimal(BigInteger.valueOf(12300), 4),
                    new BigDecimal(BigInteger.valueOf(-125), -1), new BigDecimal(BigInteger.valueOf(125), 3),
                    BigDecimal.ONE, new BigDecimal(BigInteger.valueOf(100), 2),
                    new BigDecimal(BigInteger.ONE, 1000), new BigDecimal(BigInteger.ZERO, 3),
                    new BigDecimal(BigInteger.ONE, -1000)};
            for (int i = 0; i < decimals.length; i++) {
                script.cells[1] = decimals[i]; script.cells[2] = decimals[i];
                clearScriptCell(r);
                test(r.getBigDecimal(2).equals(numbers[i]) && r.getBigDecimal("amount").equals(numbers[i]));
                test(r.getObject(2).equals(numbers[i]));
                test(r.getBigDecimal(3).equals(numbers[i]) && r.getBigDecimal("text").equals(numbers[i]));
                test(r.getString(2).equals(decimals[i]));
            }
            script.cells[1] = "1e2147483648";
            clearScriptCell(r);
            state("22003", () -> r.getBigDecimal(2));
            for (String text : new String[] {"", " ", "1,2", " 1", "1 ", "NaN", "Infinity", "null", "0x1", "1d", ".", "+", "1e", "1e+", "\"1.2\"", "١", "1e2147483649", "1e-2147483649"}) {
                script.cells[1] = text; script.cells[2] = text;
                clearScriptCell(r);
                String expectedState = text.startsWith("1e214748") || text.startsWith("1e-214748") ? "22003" : "22018";
                try { state(expectedState, () -> r.getBigDecimal(2)); }
                catch (AssertionError e) { throw new AssertionError("Decimal negative case: " + text, e); }
                state(expectedState, () -> r.getObject("amount"));
                state(expectedState, () -> r.getBigDecimal("text"));
            }
            script.cells[1] = "1";
            clearScriptCell(r);
            state("22018", () -> r.getBoolean(2));
            state("22018", () -> r.getBigDecimal(1));
            state("22018", () -> r.getInt(2));
            state("22018", () -> r.getLong(2));
            state("22018", () -> r.getDouble(2));
            state("22018", () -> r.getTimestamp(2));
            state("22018", () -> r.getInt(1));
            state("22018", () -> r.getDouble(1));
            state("07009", () -> r.getBigDecimal("missing"));
            state("07009", () -> r.getBoolean(0));
            script.nullCells = true;
            clearScriptCell(r);
            test(!r.getBoolean(1) && r.wasNull());
            test(!r.getBoolean("flag") && r.wasNull());
            test(r.getBigDecimal(2) == null && r.wasNull());
            test(r.getBigDecimal("amount") == null && r.wasNull());
            test(r.getString(2) == null && r.wasNull());
            for (int column : new int[] {1, 2}) test(r.getObject(column) == null && r.wasNull());
            script.nullCells = false; script.cells[0] = "false"; script.cells[1] = "0.00";
            clearScriptCell(r);
            test(!r.getBoolean(1) && !r.wasNull());
            test(r.getBigDecimal(2).equals(new BigDecimal(BigInteger.ZERO, 2)) && !r.wasNull());
            test(!r.next());
            state("HY010", () -> r.getBoolean(1));
            state("HY010", () -> r.getBigDecimal(2));
        }
        Script numeric = new Script();
        numeric.types = new int[] {2, 3, 4};
        numeric.cells = new String[] {"-2147483648", "9223372036854775807", "1.25"};
        try (Connection c = JdbcObjects.connection(new NativeEngine(numeric.api()), 1);
             Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT numeric_values")) {
            test(r.next());
            test(r.getBigDecimal(1).equals(BigDecimal.valueOf(Integer.MIN_VALUE)));
            test(r.getBigDecimal(2).equals(BigDecimal.valueOf(Long.MAX_VALUE)));
            state("22018", () -> r.getBigDecimal(3));
            for (int column : new int[] {1, 2, 3}) state("22018", () -> r.getBoolean(column));
        }
    }
    static void numericBounds() throws Exception {
        int maximum = 4 * 1024 * 1024;
        String huge = "9".repeat(maximum);
        String zeroes = "0".repeat(maximum);
        for (String text : new String[] {huge, "-" + huge.substring(1), "+" + huge.substring(1)}) {
            state("22003", () -> Values.integer(text));
            state("22003", () -> Values.longInteger(text));
            state("22003", () -> Values.decimal(text));
        }
        for (String text : new String[] {huge.substring(1) + "x", zeroes.substring(1) + "x",
                "1e" + huge.substring(3) + "x", "0." + zeroes.substring(3) + "x"}) {
            state("22018", () -> Values.integer(text));
            state("22018", () -> Values.longInteger(text));
            state("22018", () -> Values.decimal(text));
        }
        test(Values.integer(zeroes) == 0 && Values.longInteger(zeroes) == 0);
        test(Values.integer(zeroes.substring(10) + "2147483647") == Integer.MAX_VALUE);
        test(Values.longInteger(zeroes.substring(19) + "9223372036854775807") == Long.MAX_VALUE);
        test(Values.integer("-" + zeroes.substring(11) + "2147483648") == Integer.MIN_VALUE);
        test(Values.longInteger("-" + zeroes.substring(20) + "9223372036854775808") == Long.MIN_VALUE);
        for (String[] pair : new String[][] {{"2147483647", "2147483648"}, {"-2147483648", "-2147483649"},
                {"+2147483647", "+2147483648"}}) {
            test(Values.integer(pair[0]) == Long.parseLong(pair[0]));
            state("22003", () -> Values.integer(pair[1]));
        }
        for (String[] pair : new String[][] {{"9223372036854775807", "9223372036854775808"},
                {"-9223372036854775808", "-9223372036854775809"}, {"+9223372036854775807", "+9223372036854775808"}}) {
            test(Values.longInteger(pair[0]) == Long.parseLong(pair[0]));
            state("22003", () -> Values.longInteger(pair[1]));
        }
        for (String text : new String[] {"", "+", "-", "١", "1.0", "1e0", " 1", "1 ", "1\n", "1+"}) {
            state("22018", () -> Values.integer(text));
            state("22018", () -> Values.longInteger(text));
        }
        for (int n : new int[] {4095, 4096}) {
            test(Values.decimal("0".repeat(n)).equals(BigDecimal.ZERO));
            test(Values.decimal("0".repeat(n - 1) + "1").equals(BigDecimal.ONE));
            test(Values.decimal("1e+" + "0".repeat(n - 4) + "1").equals(new BigDecimal(BigInteger.ONE, -1)));
        }
        for (int n : new int[] {1023, 1024}) {
            test(Values.decimal("9".repeat(n)).precision() == n);
            test(Values.decimal("0001" + "0".repeat(n - 1)).precision() == n);
        }
        for (int scale : new int[] {-10000, -9999, 9999, 10000}) {
            test(Values.decimal("1e" + -scale).equals(new BigDecimal(BigInteger.ONE, scale)));
            test(Values.decimal("0e" + -scale).equals(new BigDecimal(BigInteger.ZERO, scale)));
        }
        test(Values.decimal("1.00e10002").scale() == -10000);
        test(Values.decimal("0.00e-9998").scale() == 10000);
        test(Values.decimal("000.000").precision() == 1);
        test(Values.decimal("+." + "0".repeat(4093) + "1").scale() == 4094);
        state("22003", () -> Values.decimal("+." + "0".repeat(4094) + "1"));
        for (String text : new String[] {"0".repeat(4097), "9".repeat(1025), "0001" + "0".repeat(1024),
                "1e10001", "1e-10001", "0e10001", "0e-10001", "1.00e10003", "0.00e-9999",
                "1e" + huge.substring(2), "0e-" + huge.substring(3), zeroes}) {
            state("22003", () -> Values.decimal(text));
            String malformed = text.substring(0, text.length() - 1) + "x";
            state("22018", () -> Values.decimal(malformed));
        }
        Script script = new Script();
        script.types = new int[] {2, 3, 5};
        script.cells = new String[] {huge, huge, huge};
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT numeric_bounds")) {
            test(r.next());
            for (int column = 1; column <= 3; column++) {
                final int index = column;
                test(r.getString(index).length() == maximum);
                state("22003", () -> r.getObject(index));
                state("22003", () -> r.getObject(script.names[index - 1]));
                state("22003", () -> r.getBigDecimal(index));
                test(!r.wasNull());
            }
            state("22003", () -> r.getInt(1));
            state("22003", () -> r.getLong(2));
            script.cells = new String[] {zeroes, zeroes, "0.00"};
            clearScriptCell(r);
            test(r.getInt(1) == 0 && r.getLong(2) == 0L);
            test(r.getObject(1).equals(0) && r.getObject(2).equals(0L));
            test(r.getObject(3).equals(new BigDecimal(BigInteger.ZERO, 2)));
        }
        Script strings = new Script();
        strings.types = new int[] {9, 9, 9};
        strings.cells = new String[] {huge, zeroes, huge.substring(1) + "x"};
        try (Connection c = JdbcObjects.connection(new NativeEngine(strings.api()), 1);
             Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT string_bounds")) {
            test(r.next());
            for (Object column : new Object[] {1, "integer"}) {
                if (column instanceof Integer index) {
                    state("22003", () -> r.getInt(index));
                    state("22003", () -> r.getLong(index));
                    state("22003", () -> r.getBigDecimal(index));
                } else {
                    state("22003", () -> r.getInt((String) column));
                    state("22003", () -> r.getLong((String) column));
                    state("22003", () -> r.getBigDecimal((String) column));
                }
            }
            test(r.getInt(2) == 0 && r.getLong("null_text") == 0L && !r.wasNull());
            state("22003", () -> r.getBigDecimal(2));
            state("22018", () -> r.getInt(3));
            state("22018", () -> r.getLong("empty_text"));
            state("22018", () -> r.getBigDecimal(3));
            test(r.getObject(1).equals(huge) && r.getString(3).length() == maximum);
        }
    }
    @SuppressWarnings("try") // Deliberate concurrent and repeated resource close.
    static void numericAdmission() throws Exception {
        for (String getter : new String[] {"getInt", "getLong", "getBigDecimal", "getDouble", "getObject"}) {
            for (String action : new String[] {"cancel", "result", "statement", "connection"}) {
                Script script = new Script();
                int type = getter.equals("getObject") || getter.equals("getBigDecimal") ? 5
                        : getter.equals("getDouble") ? 4 : getter.equals("getLong") ? 3 : 2;
                script.types = new int[] {type, 9, 9};
                script.cells = new String[] {"123", "", ""};
                CountDownLatch captured = new CountDownLatch(1), release = new CountDownLatch(1), started = new CountDownLatch(1);
                try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
                     Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT admission")) {
                    test(r.next());
                    JdbcObjects.Rows rows = (JdbcObjects.Rows) Proxy.getInvocationHandler(r);
                    script.afterCellCopy = () -> {
                        if (!Thread.holdsLock(rows.lock())) throw new AssertionError("Native read without admission lock");
                        captured.countDown(); barrier(release);
                    };
                    FutureTask<Object> reading = task(() -> ResultSet.class.getMethod(getter, int.class).invoke(r, 1));
                    barrier(captured);
                    FutureTask<Void> ending = task(() -> {
                        started.countDown();
                        switch (action) {
                            case "cancel" -> s.cancel();
                            case "result" -> r.close();
                            case "statement" -> s.close();
                            case "connection" -> c.close();
                            default -> throw new AssertionError();
                        }
                        return null;
                    });
                    try { barrier(started); }
                    finally { release.countDown(); }
                    Object answer = finish(reading);
                    finish(ending);
                    test(answer.toString().equals(getter.equals("getDouble") ? "123.0" : "123"));
                    test(script.cellCopyCalls == 1 && script.cellSizingCalls == 1);
                    test(script.cancelCalls == (action.equals("result") ? 0 : 1));
                    if (action.equals("cancel")) {
                        test(!r.wasNull());
                        state("HY008", () -> r.getObject(1));
                    } else state("HY010", () -> r.getObject(1));
                } finally { release.countDown(); }
            }
        }
        // A captured immutable value remains usable after native scratch/cursor
        // disposal. Conversion must not consult live Rows state or native memory.
        Script script = new Script();
        script.types = new int[] {5, 9, 10};
        script.cells = new String[] {"123.4500", "", "null"};
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT snapshot")) {
            test(r.next());
            JdbcObjects.Rows rows = (JdbcObjects.Rows) Proxy.getInvocationHandler(r);
            NativeEngine.Value snapshot;
            synchronized (rows.lock()) { snapshot = rows.conversionValue(1); test(!rows.wasNull && rows.read); }
            r.close();
            int nativeCalls = script.cellCopyCalls + script.cellSizingCalls;
            test(JdbcObjects.Rows.convert("getBigDecimal", snapshot).equals(new BigDecimal("123.4500")));
            test(JdbcObjects.Rows.convert("getObject", snapshot).equals(new BigDecimal("123.4500")));
            test(script.cellCopyCalls + script.cellSizingCalls == nativeCalls);
        }
    }
    static void operations() throws Exception {
        test(SpikeLibrary.class.getDeclaredMethod("eh_operation_prepare_v1", long.class, int.class,
                LongByReference.class).getReturnType() == int.class);
        test(SpikeLibrary.class.getDeclaredMethod("eh_operation_execute_v1", long.class, byte[].class,
                SpikeLibrary.SizeT.class, LongByReference.class).getReturnType() == int.class);
        for (String name : new String[] {"eh_operation_cancel_v1", "eh_operation_close_v1"})
            test(SpikeLibrary.class.getDeclaredMethod(name, long.class).getReturnType() == int.class);
        Script script = new Script();
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement()) {
            test(s.getQueryTimeout() == 0);
            for (int timeout : new int[] {0, 1, 600}) {
                s.setQueryTimeout(timeout);
                test(s.getQueryTimeout() == timeout);
                s.executeQuery("SELECT 1").close();
                test(script.timeout == timeout * 1000);
            }
            for (int timeout : new int[] {-1, 601, Integer.MAX_VALUE})
                state("HY092", () -> s.setQueryTimeout(timeout));
            test(s.getQueryTimeout() == 600);
            s.cancel(); // No active operation must not stick to the next query.
            test(script.cancelCalls == 0);
            for (int status : new int[] {8, 10, 11, 12}) {
                script.executeStatus = status;
                try { s.executeQuery("SELECT 1"); throw new AssertionError("Expected operation failure"); }
                catch (SQLException e) {
                    test(e.getErrorCode() == status);
                    test(e.getSQLState().equals(status == 8 ? "HY008" : status == 10 ? "HYT00" : "HY010"));
                    test((e instanceof SQLTimeoutException) == (status == 10));
                }
                test(script.operations.isEmpty());
            }
            script.executeStatus = 0;
            script.prepareStatus = 12;
            state("HY010", () -> s.executeQuery("SELECT 1"));
            test(script.operations.isEmpty());
            script.prepareStatus = 0;
            state("HY000", () -> s.executeQuery(""));
            test(script.operations.isEmpty()); // Encoding/validation failure still closes prepared operation.
            try (ResultSet r = s.executeQuery("SELECT 1")) { test(r.next()); }
            int closes = script.resultCloses;
            script.operationCloseStatus = 6;
            state("HY000", () -> s.executeQuery("SELECT 1").close());
            test(script.resultCloses == closes + 1 && script.operations.isEmpty());
            script.executeStatus = 8;
            try { s.executeQuery("SELECT 1"); throw new AssertionError("Expected cancellation"); }
            catch (SQLException e) { test(e.getSQLState().equals("HY008") && e.getSuppressed().length == 1); }
            script.operationCloseStatus = 0;
            script.executeStatus = 0;
            try (ResultSet r = s.executeQuery("SELECT 1")) { test(r.next()); }
        }
        activeCancel(false);
        activeCancel(true);
        snapshotGap();
        closeRace(false);
        closeRace(true);
        closeSnapshotGap();
        unexpectedCancelError();
    }
    @SuppressWarnings("try") // Deliberate child close before same-connection recovery.
    static void retirementRecovery() throws Exception {
        Script script = new Script();
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); Statement other = c.createStatement()) {
            script.sessionMonitor = ((JdbcObjects.Query) Proxy.getInvocationHandler(s)).session;
            ResultSet r = s.executeQuery("SELECT 1");
            test(r.next());
            script.retainReservation = true;
            s.cancel();
            state("HY008", r::next);
            r.close(); s.close();
            test(script.operations.isEmpty() && script.reservation);
            CountDownLatch busy = new CountDownLatch(1);
            script.onBusy = busy::countDown;
            FutureTask<String> recovery = task(() -> queryState(other));
            barrier(busy);
            try {
                test(!recovery.isDone());
            } finally { script.reservation = false; script.retainReservation = false; }
            test(finish(recovery).equals("OK"));
            test(script.dispatches == 2);
            int calls = script.prepareCalls;
            script.prepareStatus = 12; // Even a known retirement has a finite budget.
            long start = System.nanoTime();
            try { other.executeQuery("SELECT 1"); throw new AssertionError("Unbounded drain"); }
            catch (SQLException expected) {
                test(expected.getErrorCode() == 12 && expected.getSQLState().equals("HY010"));
            }
            test(System.nanoTime() - start >= JdbcObjects.Query.DRAIN_NANOS && script.prepareCalls > calls);
            script.prepareStatus = 0;
        } finally { script.reservation = false; }
    }
    @SuppressWarnings("try") // Parent close during admission is the regression.
    static void admissionRaces() throws Exception {
        Script ordinary = new Script();
        try (Connection c = JdbcObjects.connection(new NativeEngine(ordinary.api()), 1);
             Statement s = c.createStatement()) {
            ordinary.prepareStatus = 12;
            state("HY010", () -> s.executeQuery("SELECT 1"));
            test(ordinary.prepareCalls == 1 && ordinary.dispatches == 0);
        }
        // A handle can arrive after cancel, parent close or the known-drain budget.
        // Admission is reserved already, but prepare must not hold the monitor.
        for (int mode = 0; mode < 6; mode++) {
            Script script = new Script();
            Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
            Statement s = c.createStatement();
            Statement other = c.createStatement();
            script.sessionMonitor = ((JdbcObjects.Query) Proxy.getInvocationHandler(s)).session;
            CountDownLatch entered = new CountDownLatch(1), release = new CountDownLatch(1);
            try {
                if (mode == 3 || mode == 4) s.executeQuery("SELECT seed").close();
                int dispatches = script.dispatches;
                script.beforePrepare = () -> { entered.countDown(); barrier(release); };
                FutureTask<String> execute = task(() -> {
                    try (ResultSet ignored = s.executeQuery("SELECT blocked")) { return ignored == null ? "null" : "OK"; }
                    catch (SQLException e) { return e.getSQLState() + ":" + e.getErrorCode(); }
                });
                barrier(entered);
                JdbcObjects.Query query = (JdbcObjects.Query) Proxy.getInvocationHandler(s);
                synchronized (query.session) {
                    test(query.admission != null && query.active == null && query.executingCall);
                }
                state("HY010", () -> other.executeQuery("SELECT concurrent"));
                FutureTask<Void> close = null;
                if (mode == 0 || mode == 4) {
                    finish(task(() -> { s.cancel(); return null; }));
                } else if (mode == 1 || mode == 2) {
                    final boolean connection = mode == 2;
                    close = task(() -> { if (connection) c.close(); else s.close(); return null; });
                    synchronized (query.session) { waitState(query.session, () -> query.closed()); }
                    test(!close.isDone());
                } else {
                    // One real monotonic deadline, not a reset on prepare success.
                    long started;
                    synchronized (query.session) { started = query.admission.started; }
                    while (System.nanoTime() - started < JdbcObjects.Query.DRAIN_NANOS)
                        Thread.sleep(10);
                }
                release.countDown();
                String expected = mode == 0 || mode == 4 ? "HY008:8" : mode == 3 ? "HY010:12" : mode == 5 ? "OK" : "HY010:0";
                test(finish(execute).equals(expected));
                if (close != null) finish(close);
                test(script.dispatches == dispatches + (mode == 5 ? 1 : 0) && script.operations.isEmpty() && !script.reservation);
                test(script.operationCloses == (mode == 3 || mode == 4 ? 2 : 1));
                synchronized (query.session) {
                    test(query.admission == null && query.active == null && !query.executingCall && query.session.executing == null);
                }
                script.beforePrepare = null;
                if (mode == 0 || mode == 3 || mode == 4) {
                    test(queryState(s).equals("OK")); // Cancellation cannot stick to the next admission.
                } else if (mode == 1) test(queryState(other).equals("OK"));
            } finally { release.countDown(); c.close(); }
        }
        Script failedRetirement = new Script();
        try (Connection c = JdbcObjects.connection(new NativeEngine(failedRetirement.api()), 1);
             Statement s = c.createStatement()) {
            ResultSet r = s.executeQuery("SELECT seed");
            failedRetirement.operationCloseStatus = 6;
            state("HY000", r::close);
            JdbcObjects.Query query = (JdbcObjects.Query) Proxy.getInvocationHandler(s);
            synchronized (query.session) { test(query.session.draining == null); }
            failedRetirement.prepareStatus = 12;
            int calls = failedRetirement.prepareCalls;
            state("HY010", () -> s.executeQuery("SELECT blocked"));
            test(failedRetirement.prepareCalls == calls + 1);
        }
        // Cancel interrupts a retry interval, not only an in-flight successful prepare.
        for (int mode = 0; mode < 3; mode++) {
            Script draining = new Script();
            try (Connection c = JdbcObjects.connection(new NativeEngine(draining.api()), 1);
                 Statement s = c.createStatement(); Statement other = c.createStatement()) {
                draining.sessionMonitor = ((JdbcObjects.Query) Proxy.getInvocationHandler(s)).session;
                s.executeQuery("SELECT seed").close();
                draining.reservation = true;
                CountDownLatch busy = new CountDownLatch(1);
                draining.onBusy = busy::countDown;
                FutureTask<String> execute = task(() -> queryState(s));
                barrier(busy);
                final int action = mode;
                finish(task(() -> { if (action == 0) s.cancel(); else if (action == 1) s.close(); else c.close(); return null; }));
                test(finish(execute).equals(mode == 0 ? "HY008" : "HY010"));
                test(draining.dispatches == 1 && draining.operations.isEmpty());
                draining.reservation = false;
                if (mode != 2) test(queryState(mode == 0 ? s : other).equals("OK"));
            } finally { draining.reservation = false; }
        }
    }
    static void activeCancel(boolean beforeDispatch) throws Exception {
        Script script = new Script();
        CountDownLatch entered = new CountDownLatch(1), release = new CountDownLatch(1);
        VoidChecked block = () -> { entered.countDown(); barrier(release); };
        if (beforeDispatch) script.beforeExecute = block;
        else script.afterDispatch = block;
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); Statement other = c.createStatement()) {
            FutureTask<String> execute = task(() -> queryState(s));
            barrier(entered);
            state("HY010", () -> other.executeQuery("SELECT 1"));
            state("HY010", () -> s.executeQuery("SELECT 1"));
            FutureTask<Void> cancel = task(() -> { s.cancel(); return null; });
            finish(cancel); // Must return while native execute remains blocked.
            test(!execute.isDone() && script.cancelCalls == 1);
            test(script.operations.containsValue(true));
            test(script.dispatches == (beforeDispatch ? 0 : 1));
            release.countDown();
            test(finish(execute).equals("HY008"));
            test(script.operations.isEmpty());
            script.beforeExecute = null;
            script.afterDispatch = null;
            try (ResultSet r = s.executeQuery("SELECT 1")) { test(r.next()); }
        } finally { release.countDown(); }
    }
    static void snapshotGap() throws Exception {
        Script script = new Script();
        script.ignoreCancel = true; // Native success won before the delayed signal.
        CountDownLatch executing = new CountDownLatch(1), complete = new CountDownLatch(1);
        CountDownLatch snapshotted = new CountDownLatch(1), admit = new CountDownLatch(1);
        script.beforeExecute = () -> { executing.countDown(); barrier(complete); };
        script.beforeCancel = () -> { snapshotted.countDown(); barrier(admit); };
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); Statement other = c.createStatement()) {
            FutureTask<String> execute = task(() -> queryState(s));
            barrier(executing);
            FutureTask<Void> cancel = task(() -> { s.cancel(); return null; });
            barrier(snapshotted); // Java captured A, fake native has not admitted cancel A.
            complete.countDown();
            JdbcObjects.Query query = (JdbcObjects.Query) Proxy.getInvocationHandler(s);
            synchronized (query.session) {
                waitState(query.session, () -> query.active != null && query.active.retiring);
                test(query.active.signals == 1 && !query.active.retired);
            }
            state("HY010", () -> other.executeQuery("SELECT 1"));
            test(script.operationCloses == 0 && !execute.isDone());
            admit.countDown();
            finish(cancel);
            test(finish(execute).equals("OK"));
            test(script.cancelledHandle == 100 && script.operationCloses == 1 && script.operations.isEmpty());
            script.beforeExecute = null; script.beforeCancel = null; script.ignoreCancel = false;
            try (ResultSet r = other.executeQuery("SELECT 1")) { test(r.next()); }
            test(script.cancelCalls == 1 && script.nextOperation == 102);
        } finally { complete.countDown(); admit.countDown(); }
    }
    static void closeRace(boolean connectionClose) throws Exception {
        Script script = new Script();
        script.ignoreCancel = true; // Success transferred natively, but parent closes before Java publication.
        CountDownLatch entered = new CountDownLatch(1), release = new CountDownLatch(1);
        CountDownLatch cancelled = new CountDownLatch(1);
        script.beforeExecute = () -> { entered.countDown(); barrier(release); };
        script.beforeCancel = cancelled::countDown;
        Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
        Statement s = c.createStatement();
        try {
            FutureTask<String> execute = task(() -> queryState(s));
            barrier(entered);
            FutureTask<Void> close = task(() -> { if (connectionClose) c.close(); else s.close(); return null; });
            barrier(cancelled);
            test(!close.isDone() && script.connectionCloses == 0);
            test(s.isClosed());
            state("HY010", s::cancel);
            release.countDown();
            test(finish(execute).equals("HY010"));
            finish(close);
            test(script.resultCloses == 1 && script.operationCloses == 1 && script.operations.isEmpty());
            test(script.connectionCloses == (connectionClose ? 1 : 0));
            s.close(); c.close(); c.close();
            test(script.connectionCloses == 1);
        } finally { release.countDown(); c.close(); }
    }
    static void unexpectedCancelError() throws Exception {
        Script script = new Script();
        CountDownLatch entered = new CountDownLatch(1), release = new CountDownLatch(1);
        script.beforeExecute = () -> { entered.countDown(); barrier(release); };
        script.cancelStatus = 3;
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement()) {
            FutureTask<String> execute = task(() -> queryState(s));
            barrier(entered);
            state("HY010", s::cancel); // A live-token error is not suppressed as retirement.
            release.countDown();
            test(finish(execute).equals("OK"));
            test(script.operations.isEmpty());
        } finally { release.countDown(); }
    }
    static void closeSnapshotGap() throws Exception {
        Script script = new Script();
        script.ignoreCancel = true;
        CountDownLatch executing = new CountDownLatch(1), complete = new CountDownLatch(1);
        CountDownLatch snapshotted = new CountDownLatch(1), admit = new CountDownLatch(1);
        script.beforeExecute = () -> { executing.countDown(); barrier(complete); };
        script.beforeCancel = () -> { snapshotted.countDown(); barrier(admit); };
        Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
        Statement s = c.createStatement();
        try {
            FutureTask<String> execute = task(() -> queryState(s));
            barrier(executing);
            FutureTask<Void> cancel = task(() -> { s.cancel(); return null; });
            barrier(snapshotted);
            complete.countDown();
            JdbcObjects.Query query = (JdbcObjects.Query) Proxy.getInvocationHandler(s);
            synchronized (query.session) {
                waitState(query.session, () -> query.active != null && query.active.retiring);
            }
            FutureTask<Void> close = task(() -> { c.close(); return null; });
            synchronized (query.session) { waitState(query.session, () -> query.session.closed); }
            test(!close.isDone() && !execute.isDone() && script.connectionCloses == 0);
            admit.countDown();
            finish(cancel);
            test(finish(execute).equals("HY010"));
            finish(close);
            test(script.operationCloses == 1 && script.connectionCloses == 1 && script.resultCloses == 1);
            test(script.operations.isEmpty() && script.cancelCalls == 1);
        } finally { complete.countDown(); admit.countDown(); c.close(); }
    }
    static String queryState(Statement s) throws Exception {
        try (ResultSet r = s.executeQuery("SELECT 1")) { while (r.next()) { /* Drain through validated EOF. */ } return "OK"; }
        catch (SQLException e) { return e.getSQLState(); }
    }
    static <T> FutureTask<T> task(java.util.concurrent.Callable<T> action) {
        FutureTask<T> future = new FutureTask<>(action);
        Thread thread = new Thread(future, "jdbc-operation-test");
        thread.setDaemon(true);
        thread.start();
        return future;
    }
    static <T> T finish(FutureTask<T> task) throws Exception { return task.get(10, TimeUnit.SECONDS); }
    static void barrier(CountDownLatch latch) throws InterruptedException {
        if (!latch.await(10, TimeUnit.SECONDS)) throw new AssertionError("Bounded barrier expired");
    }
    static void waitState(Object monitor, java.util.function.BooleanSupplier state) throws InterruptedException {
        long end = System.nanoTime() + TimeUnit.SECONDS.toNanos(10);
        while (!state.getAsBoolean()) {
            long remaining = end - System.nanoTime();
            if (remaining <= 0) throw new AssertionError("Bounded state wait expired");
            TimeUnit.NANOSECONDS.timedWait(monitor, remaining);
        }
    }
    static void typedValues() throws Exception {
        test(Values.longInteger("9223372036854775807") == Long.MAX_VALUE);
        test(Values.longInteger("-9223372036854775808") == Long.MIN_VALUE);
        for (String value : new String[] {"9223372036854775808", "-9223372036854775809"})
            state("22003", () -> Values.longInteger(value));
        for (String value : new String[] {"", "1.0", "1e2", " 1", "0x10"})
            state("22018", () -> Values.longInteger(value));
        test(Values.real("1.7976931348623157e308") == Double.MAX_VALUE);
        test(Values.real("4.9e-324") == Double.MIN_VALUE);
        test(Double.doubleToRawLongBits(Values.real("-0.0")) == Double.doubleToRawLongBits(-0.0));
        for (String value : new String[] {"1e309", "-1e309", "NaN", "Infinity", "-Infinity"})
            state("22003", () -> Values.real(value));
        for (String value : new String[] {"", " 1", "+1", "01", "1.", ".5", "1d", "0x1p0", "null"})
            state("22018", () -> Values.real(value));
        String utc = "2026-10-16T12:34:56.123456789Z";
        String json = "\"" + utc + "\"";
        test(Values.timestamp(utc).toInstant().equals(java.time.Instant.parse(utc)));
        test(Values.timestamp(utc).getNanos() == 123456789);
        test(Values.datetimeText(utc).equals(utc));
        test(Values.timestamp("2026-10-16T12:34:56+00:00").toInstant()
                .equals(java.time.Instant.parse("2026-10-16T12:34:56Z")));
        for (String value : new String[] {json, "null", "\"" + utc + "\"x", "2026-10-16T12:34:56",
                "2026-10-16T12:34:56+01:00", "2026-02-30T12:34:56Z",
                "2026-10-16T12:34:60Z", "2026-10-16T12:34:56.1234567890Z",
                "\"2026-10-16T12:34:56\\q\"", "\"\\uD800\"", "\"\\uZZZZ\"", "\"a\nb\"", "\"a\"b\""})
            state("22018", () -> Values.timestamp(value));
        Script script = new Script();
        script.types = new int[] {3, 4, 6};
        script.names = new String[] {"PartitionId", "amount", "EventProcessedUtcTime"};
        script.cells = new String[] {"9223372036854775807", "12.50", utc};
        try (Connection c = JdbcObjects.connection(new NativeEngine(script.api()), 1);
             Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT * FROM transactions")) {
            test(r.next());
            test(r.getLong(1) == Long.MAX_VALUE && r.getLong("partitionid") == Long.MAX_VALUE);
            test(r.getDouble(2) == 12.5 && r.getDouble("AMOUNT") == 12.5);
            test(r.getTimestamp(3).toInstant().equals(java.time.Instant.parse(utc)));
            test(r.getTimestamp("eventprocessedutctime").getNanos() == 123456789);
            test(r.getString(3).equals(utc) && r.getString("EventProcessedUtcTime").equals(utc));
            test(r.getString(2).equals("12.50"));
            test(r.getObject(1) instanceof Long && r.getObject("PartitionId").equals(Long.MAX_VALUE));
            test(r.getObject(2) instanceof Double && r.getObject("amount").equals(12.5));
            test(r.getObject(3) instanceof Timestamp && r.getObject("EventProcessedUtcTime").equals(Timestamp.from(java.time.Instant.parse(utc))));
            ResultSetMetaData m = r.getMetaData();
            int[] sqlTypes = {Types.BIGINT, Types.DOUBLE, Types.TIMESTAMP};
            String[] classes = {Long.class.getName(), Double.class.getName(), Timestamp.class.getName()};
            String[] names = {"long", "double", "datetime"};
            for (int i = 1; i <= 3; i++) {
                test(m.getColumnType(i) == sqlTypes[i - 1]);
                test(m.getColumnClassName(i).equals(classes[i - 1]));
                test(m.getColumnTypeName(i).equals(names[i - 1]));
                test(m.isSigned(i) == (i != 3));
            }
            state("0A000", () -> r.getObject(1, Long.class));
            state("0A000", () -> r.getObject("amount", java.util.Map.of()));
            state("0A000", () -> r.getTimestamp(3, java.util.Calendar.getInstance()));
            state("0A000", () -> r.getTimestamp("EventProcessedUtcTime", java.util.Calendar.getInstance()));
            state("22018", () -> r.getLong(2));
            state("22018", () -> r.getDouble(1));
            state("22018", () -> r.getTimestamp(1));
            script.nullCells = true;
            clearScriptCell(r);
            test(r.getLong(1) == 0 && r.wasNull());
            test(r.getDouble("amount") == 0.0 && r.wasNull());
            test(r.getTimestamp(3) == null && r.wasNull());
            for (int i = 1; i <= 3; i++) test(r.getObject(i) == null && r.wasNull());
            script.nullCells = false;
            script.cells[0] = "9223372036854775808";
            clearScriptCell(r);
            state("22003", () -> r.getLong("PartitionId"));
            state("22003", () -> r.getObject(1));
            script.cells[1] = "NaN";
            clearScriptCell(r);
            state("22003", () -> r.getDouble(2));
            script.cells[1] = "1.2x";
            clearScriptCell(r);
            state("22018", () -> r.getObject("amount"));
            script.cells[2] = json;
            clearScriptCell(r);
            state("22018", () -> r.getString(3));
            state("22018", () -> r.getTimestamp(3));
            state("22018", () -> r.getObject(3));
        }
        Script original = new Script();
        try (Connection c = JdbcObjects.connection(new NativeEngine(original.api()), 1);
             Statement s = c.createStatement(); ResultSet r = s.executeQuery("SELECT 1")) {
            test(r.next());
            test(r.getObject(1) instanceof Integer && r.getObject(1).equals(1));
            test(r.getObject(2) == null && r.wasNull());
            test(r.getObject(3) instanceof String && r.getObject(3).equals(""));
        }
    }
    static String missingLibrary() {
        Path path = Path.of("build", "wp005a-nonexistent-native", "missing-engine.dll").toAbsolutePath();
        if (Files.exists(path)) throw new AssertionError("Test requires absent native path");
        return path.toString();
    }
    static void rejectedLoader(boolean childJna) throws Exception {
        java.net.URL driverJar = EventhouseDriver.class.getProtectionDomain().getCodeSource().getLocation();
        java.net.URL jnaJar = Native.class.getProtectionDomain().getCodeSource().getLocation();
        try (URLClassLoader loader = new URLClassLoader(new java.net.URL[] {driverJar, jnaJar},
                childJna ? ClassLoader.getPlatformClassLoader() : ClassLoader.getSystemClassLoader()) {
            @Override protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
                if (!name.startsWith("com.microsoft.eventhouse.jdbc.")) return super.loadClass(name, resolve);
                synchronized (getClassLoadingLock(name)) {
                    Class<?> type = findLoadedClass(name);
                    if (type == null) type = findClass(name);
                    if (resolve) resolveClass(type);
                    return type;
                }
            }
        }) {
            Driver isolated = (Driver) Class.forName(EventhouseDriver.class.getName(), true, loader)
                    .getConstructor().newInstance();
            try { isolated.connect(URL, properties()); throw new AssertionError("Child loader admitted"); }
            catch (SQLException e) {
                test(e.getSQLState().equals("08001") && e.getMessage().contains("system class loader"));
            }
            // Rejection occurs before a JNI-capable operation. The child engine has no native roots.
            Class<?> engine = Class.forName(NativeEngine.class.getName(), false, loader);
            var retained = engine.getDeclaredField("retainedApi");
            retained.setAccessible(true);
            test(retained.get(null) == null);
        }
    }
    interface VoidChecked { void run() throws Exception; }
    static void state(String state, VoidChecked action) throws Exception {
        try { action.run(); throw new AssertionError("Missing SQLException"); }
        catch (SQLException e) { test(state.equals(e.getSQLState())); }
    }
    static void test(boolean condition) {
        if (!condition) throw new AssertionError("Assertion " + (passed + 1));
        passed++;
    }
    // Older conversion tables mutate a fake row in place. Real cursor rows are
    // immutable. Explicitly discard the test row's cached cell at each mutation.
    static void clearScriptCell(ResultSet result) {
        ((JdbcObjects.Rows) Proxy.getInvocationHandler(result)).cells.clear();
    }
    static final class Script {
        int resultCloses, connectionCloses, executeStatus, type = 2;
        volatile int timeout, cancelCalls, operationCloses, dispatches, cancelStatus, prepareStatus, operationCloseStatus;
        volatile long nextOperation = 100, cancelledHandle;
        volatile boolean ignoreCancel;
        volatile boolean reservation, retainReservation;
        volatile int prepareCalls;
        VoidChecked onBusy, beforePrepare;
        Object sessionMonitor = new Object();
        VoidChecked beforeExecute, afterDispatch, beforeCancel, beforeNext, afterCellSizing, afterCellCopy;
        int nextStatus, nextCalls, delivered, totalRows = 1;
        int terminalStatus, cellSizingCalls, cellCopyCalls;
        boolean resultClosed;
        long resultOperation;
        final java.util.concurrent.ConcurrentMap<Long, Boolean> operations = new java.util.concurrent.ConcurrentHashMap<>();
        String text = "";
        String openUri, openDatabase;
        String[] names = {"integer", "null_text", "empty_text"};
        int[] types;
        String[] cells;
        boolean nullCells;
        SpikeLibrary api() {
            return (SpikeLibrary) Proxy.newProxyInstance(getClass().getClassLoader(), new Class<?>[] {SpikeLibrary.class},
                    (proxy, method, a) -> switch (method.getName()) {
                        case "eh_open_v1" -> {
                            byte[] uri = (byte[]) a[0], database = (byte[]) a[2];
                            if (((SpikeLibrary.SizeT) a[1]).longValue() != uri.length
                                    || ((SpikeLibrary.SizeT) a[3]).longValue() != database.length)
                                throw new AssertionError("Native open byte length mismatch");
                            openUri = new String(uri, StandardCharsets.UTF_8);
                            openDatabase = new String(database, StandardCharsets.UTF_8);
                            ((LongByReference) a[11]).setValue(1);
                            yield 0;
                        }
                        case "eh_operation_prepare_v1" -> {
                            if (Thread.holdsLock(sessionMonitor)) throw new AssertionError("Prepare holds session monitor");
                            prepareCalls++;
                            if (beforePrepare != null) beforePrepare.run();
                            timeout = (int) a[1];
                            if (prepareStatus != 0) yield prepareStatus;
                            if (reservation) { if (onBusy != null) onBusy.run(); yield 12; }
                            long operation = nextOperation++;
                            operations.put(operation, false);
                            reservation = true;
                            ((LongByReference) a[2]).setValue(operation);
                            yield 0;
                        }
                        case "eh_operation_execute_v1" -> {
                            if (beforeExecute != null) beforeExecute.run();
                            Boolean cancelled = operations.get((long) a[0]);
                            if (cancelled == null) yield 3;
                            if (!cancelled) {
                                dispatches++;
                                if (afterDispatch != null) afterDispatch.run();
                                cancelled = operations.get((long) a[0]);
                                if (cancelled == null) yield 3;
                            }
                             int status = cancelled && !ignoreCancel ? 8 : executeStatus;
                             delivered = 0;
                             resultClosed = false;
                              resultOperation = (long) a[0];
                             terminalStatus = 0;
                            ((LongByReference) a[3]).setValue(status == 0 ? 2 : 0);
                            yield status;
                        }
                        case "eh_operation_cancel_v1" -> {
                            if (beforeCancel != null) beforeCancel.run();
                            if (cancelStatus != 0) yield cancelStatus;
                            cancelledHandle = (long) a[0];
                            if (operations.replace(cancelledHandle, true) == null) yield 3;
                            cancelCalls++;
                            yield 0;
                        }
                        case "eh_operation_close_v1" -> {
                            if (Thread.holdsLock(sessionMonitor)) throw new AssertionError("Operation close holds session monitor");
                            if (operations.remove((long) a[0]) == null) yield 3;
                            if (!retainReservation) reservation = false;
                            operationCloses++;
                            yield operationCloseStatus;
                        }
                         case "eh_result_shape_v1" -> {
                             size((SpikeLibrary.SizeRef) a[1], 3); size((SpikeLibrary.SizeRef) a[2], delivered <= totalRows ? -1L : totalRows); yield 0;
                         }
                         case "eh_result_next_v1" -> {
                             nextCalls++;
                             if (beforeNext != null) beforeNext.run();
                             if (resultClosed) yield 3;
                              if (terminalStatus == 0 && Boolean.TRUE.equals(operations.get(resultOperation)) && !ignoreCancel) terminalStatus = 8;
                              if (terminalStatus == 0 && nextStatus != 0) terminalStatus = nextStatus;
                              if (terminalStatus != 0) yield terminalStatus;
                             ((ByteByReference) a[1]).setValue((byte) (delivered++ < totalRows ? 1 : 0));
                             yield 0;
                         }
                         case "eh_result_cell_v1" -> throw new AssertionError("Indexed history API called");
                         case "eh_result_column_v1", "eh_result_current_cell_v1" -> {
                              boolean column = method.getName().contains("column");
                              if (!column) {
                                  if (a[4] == null) cellSizingCalls++; else cellCopyCalls++;
                                  if (terminalStatus == 0 && Boolean.TRUE.equals(operations.get(resultOperation)) && !ignoreCancel) terminalStatus = 8;
                                  if (terminalStatus != 0) yield terminalStatus;
                              }
                              if (!column && (delivered < 1 || delivered > totalRows)) yield 11;
                             int offset = 0;
                            int index = ((SpikeLibrary.SizeT) a[1 + offset]).intValue();
                            ((IntByReference) a[2 + offset]).setValue(types != null ? types[index] : index == 0 ? type : 9);
                            ((ByteByReference) a[3 + offset]).setValue((byte) (column || (cells != null ? nullCells : index == 1) ? 1 : 0));
                            byte[] value = (column ? names[index]
                                    : cells != null ? nullCells ? "" : cells[index]
                                    : index == 0 ? "1" : index == 2 ? text : "").getBytes(StandardCharsets.UTF_8);
                            size((SpikeLibrary.SizeRef) a[6 + offset], value.length);
                            Pointer pointer = (Pointer) a[4 + offset];
                             if (value.length > ((SpikeLibrary.SizeT) a[5 + offset]).longValue()) {
                                 if (!column && afterCellSizing != null) afterCellSizing.run();
                                 yield 2;
                             }
                             if (value.length != 0) pointer.write(0, value, 0, value.length);
                             if (!column && afterCellCopy != null) afterCellCopy.run();
                            yield 0;
                        }
                         case "eh_result_close_v1" -> { resultCloses++; resultClosed = true; yield 0; }
                        case "eh_connection_close_v1" -> { connectionCloses++; yield 0; }
                        default -> throw new AssertionError("Unexpected native call");
                    });
        }
        static void size(SpikeLibrary.SizeRef ref, long value) {
            if (Native.SIZE_T_SIZE == 8) ref.getPointer().setLong(0, value);
            else ref.getPointer().setInt(0, (int) value);
        }
    }
}
