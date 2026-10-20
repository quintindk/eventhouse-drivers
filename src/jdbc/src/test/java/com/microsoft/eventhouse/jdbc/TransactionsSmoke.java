package com.microsoft.eventhouse.jdbc;

import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.sql.*;
import java.util.Objects;
import java.util.Properties;

/** Bounded by the parent process. Never prints configuration, exceptions or cells. */
public final class TransactionsSmoke {
    private TransactionsSmoke() { }

    public static void main(String[] args) {
        String stage = "configuration";
        long rows = 0;
        long returnedUtf8Bytes = 0;
        try {
            require(args.length == 0 || args.length == 1 && args[0].equals("--top10"));
            boolean top10 = args.length == 1;
            String profile = top10 ? "TOP10" : "UNRESTRICTED";
            String host = required("EVENTHOUSE_QUERY_HOST");
            Properties properties = new Properties();
            for (String[] pair : new String[][] {{"tenantId", "AZURE_TENANT_ID"},
                    {"clientId", "AZURE_SPN_CLIENT_ID"}, {"clientSecret", "AZURE_SPN_SECRET"},
                    {"nativeLibrary", "EVENTHOUSE_NATIVE_LIBRARY"}}) {
                properties.setProperty(pair[0], required(pair[1]));
            }
            String database = required("EVENTHOUSE_DB_NAME");
            String guid = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
            require(properties.getProperty("tenantId").matches(guid));
            require(properties.getProperty("clientId").matches(guid));
            require(Path.of(properties.getProperty("nativeLibrary")).isAbsolute());
            // Acceptance caller deadline includes idle consumer time and all getter checks.
            // This does not alter the driver's default or its operation deadline policy.
            properties.setProperty("timeoutMillis", top10 ? "30000" : "300000");
            String encoded = URLEncoder.encode(database, StandardCharsets.UTF_8).replace("+", "%20");
            long nulls = 0;
            int columns;
            stage = "native-open";
            try (Connection connection = DriverManager.getConnection(
                    "jdbc:eventhouse://" + host + "/" + encoded, properties)) {
                stage = "statement-create";
                try (Statement statement = connection.createStatement()) {
                    stage = "execute";
                    try (ResultSet result = statement.executeQuery(top10
                             ? "SELECT TOP 10 * FROM transactions" : "SELECT * FROM transactions")) {
                    stage = "metadata";
                    ResultSetMetaData metadata = result.getMetaData();
                    columns = metadata.getColumnCount();
                     require(columns > 0);
                     if (top10) require(columns == 28);
                     for (int column = 1; column <= columns; column++) {
                         if (metadata.getColumnType(column) == Types.LONGVARCHAR) {
                             require(metadata.getColumnTypeName(column).equals("dynamic"));
                             require(metadata.getColumnClassName(column).equals(String.class.getName()));
                             require(!metadata.isSigned(column));
                         }
                         if (top10 && column >= 26) require(metadata.getColumnType(column) == Types.LONGVARCHAR);
                    }
                    while (true) {
                        stage = "fetch";
                        if (!result.next()) break;
                        for (int column = 1; column <= columns; column++) {
                            stage = "getter-object";
                            Object object = result.getObject(column);
                            boolean isNull = result.wasNull();
                            require((object == null) == isNull);
                            if (isNull) nulls++;
                            stage = "getter-typed";
                            Object typed = switch (metadata.getColumnType(column)) {
                                case Types.BOOLEAN -> result.getBoolean(column);
                                case Types.DECIMAL -> result.getBigDecimal(column);
                                case Types.INTEGER -> result.getInt(column);
                                case Types.BIGINT -> result.getLong(column);
                                case Types.DOUBLE -> result.getDouble(column);
                                case Types.TIMESTAMP -> result.getTimestamp(column);
                                case Types.VARCHAR, Types.LONGVARCHAR -> result.getString(column);
                                default -> throw new SQLException("Unsupported type", "0A000");
                            };
                            require(result.wasNull() == isNull);
                            if (!isNull) require(Objects.equals(typed, object));
                            else require(typed == null || Objects.equals(typed, 0)
                                    || Objects.equals(typed, 0L) || Objects.equals(typed, 0.0)
                                    || Objects.equals(typed, false));
                            stage = "getter-named-object";
                            Object named = result.getObject(metadata.getColumnLabel(column));
                            require(result.wasNull() == isNull && Objects.equals(named, object));
                            stage = "getter-string";
                             String text = result.getString(column);
                             returnedUtf8Bytes = countReturnedUtf8(returnedUtf8Bytes, text);
                            require(result.wasNull() == isNull && (text == null) == isNull);
                            stage = "getter-named-string";
                            require(Objects.equals(result.getString(metadata.getColumnLabel(column)), text)
                                    && result.wasNull() == isNull);
                            if (metadata.getColumnType(column) == Types.LONGVARCHAR) {
                                require(isNull || object instanceof String);
                                require(Objects.equals(text, object));
                            }
                        }
                        rows++;
                    }
                     require(top10 ? rows == 10 : rows > 0);
                     stage = "returned-byte-threshold";
                     require(top10 || returnedUtf8Bytes > 4_194_304L);
                    stage = "result-statement-close";
                    }
                }
                stage = "connection-close";
            }
             System.out.println("PASS WP005Q_" + profile + " rows=" + rows + " columns=" + columns
                     + " null_cells=" + nulls + " returned_utf8_bytes=" + returnedUtf8Bytes);
        } catch (SQLException exception) {
            String state = exception.getSQLState();
            if (state == null || !state.matches("[A-Z0-9]{5}")) state = "HY000";
             System.out.println("FAIL WP005Q_TRANSACTIONS stage=" + stage + " sqlstate=" + state
                    + " vendor=" + exception.getErrorCode() + " rows=" + rows
                    + " returned_utf8_bytes=" + returnedUtf8Bytes);
            System.exit(1);
        } catch (Throwable exception) {
             System.out.println("FAIL WP005Q_TRANSACTIONS stage=" + stage + " rows=" + rows
                     + " returned_utf8_bytes=" + returnedUtf8Bytes);
            System.exit(1);
        }
    }

    private static String required(String name) {
        String value = System.getenv(name);
        if (value == null || value.isBlank()) {
             System.out.println("BLOCKED WP005Q_TRANSACTIONS missing=" + name);
            System.exit(2);
        }
        return value;
    }

    private static void require(boolean condition) throws SQLException {
        if (!condition) throw new SQLException("Smoke assertion failed", "HY000");
    }
    // Called once per cell from indexed getString, not from repeated validation reads.
    // Counts returned text only, never wire bytes, metadata, nulls or framing.
    static long countReturnedUtf8(long count, String text) {
        return text == null ? count : Math.addExact(count, text.getBytes(StandardCharsets.UTF_8).length);
    }
}
