package com.microsoft.eventhouse.jdbc;

import java.sql.*;
import java.util.Properties;
import java.util.logging.Logger;

public final class EventhouseDriver implements Driver {
    static {
        try { DriverManager.registerDriver(new EventhouseDriver()); }
        catch (SQLException e) { throw new ExceptionInInitializerError("Eventhouse JDBC registration failed."); }
    }
    @Override public Connection connect(String url, Properties info) throws SQLException {
        if (url == null || !url.startsWith(Configuration.PREFIX)) return null;
        NativeEngine.requireSystemLoaders();
        Configuration config = Configuration.parse(url, info);
        NativeEngine engine = NativeEngine.load(config.library());
        return JdbcObjects.connection(engine, engine.open(config));
    }
    @Override public boolean acceptsURL(String url) {
        try { Configuration.database(url); return true; } catch (SQLException ignored) { return false; }
    }
    @Override public DriverPropertyInfo[] getPropertyInfo(String url, Properties info) {
        return Configuration.KEYS.stream().sorted().map(name -> {
            DriverPropertyInfo p = new DriverPropertyInfo(name, name.equals("timeoutMillis") ? "30000" : null);
            p.required = !name.equals("timeoutMillis");
            p.description = name.equals("nativeLibrary") ? "Absolute native engine library path" : name;
            return p;
        }).toArray(DriverPropertyInfo[]::new);
    }
    @Override public int getMajorVersion() { return 0; }
    @Override public int getMinorVersion() { return 1; }
    @Override public boolean jdbcCompliant() { return false; }
    @Override public Logger getParentLogger() throws SQLFeatureNotSupportedException { throw JdbcObjects.unsupported(); }
}
