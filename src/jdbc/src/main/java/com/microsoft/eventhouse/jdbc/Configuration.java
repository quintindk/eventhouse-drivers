package com.microsoft.eventhouse.jdbc;

import java.net.URI;
import java.io.ByteArrayOutputStream;
import java.nio.file.Path;
import java.sql.SQLException;
import java.util.Properties;
import java.util.Set;

record Configuration(String host, String database, String tenant, String client, String secret, Path library, int timeout) {
    static final String PREFIX = "jdbc:eventhouse:";
    private static final String SUFFIX = ".kusto.fabric.microsoft.com";
    static final Set<String> KEYS = Set.of("tenantId", "clientId", "clientSecret", "nativeLibrary", "timeoutMillis");

    static String database(String url) throws SQLException {
        try {
            if (url == null || !url.startsWith(PREFIX)) throw new IllegalArgumentException();
            URI uri = new URI("https:" + url.substring(PREFIX.length()));
            String path = uri.getRawPath();
            if (!validHost(uri.getRawAuthority()) || uri.getRawQuery() != null || uri.getRawFragment() != null
                    || path == null || !path.startsWith("/") || path.length() < 2
                    || path.substring(1).contains("/") || path.contains("\\"))
                throw new IllegalArgumentException();
            // URI validates escape syntax. Decode bytes ourselves so malformed UTF-8
            // rejects rather than being replaced, and '+' is never treated as a space.
            byte[] raw = Utf8.encode(path.substring(1), 768, "08001");
            ByteArrayOutputStream decoded = new ByteArrayOutputStream();
            for (int i = 0; i < raw.length; i++) {
                if (raw[i] == '%') {
                    int high = ++i < raw.length ? Character.digit((char) raw[i], 16) : -1;
                    int low = ++i < raw.length ? Character.digit((char) raw[i], 16) : -1;
                    if (high < 0 || low < 0) throw invalid();
                    decoded.write((high << 4) | low);
                } else decoded.write(raw[i]);
            }
            if (decoded.size() > 256) throw invalid();
            String database = Utf8.decode(decoded.toByteArray());
            if (database.isBlank() || database.equals(".") || database.equals("..")
                    || database.contains("/") || database.contains("\\")
                    || database.codePoints().anyMatch(Character::isISOControl)) throw invalid();
            return database;
        } catch (Exception ignored) { throw invalid(); }
    }

    private static boolean validHost(String host) {
        if (host == null || host.length() > 253 || host.length() <= SUFFIX.length() || !host.endsWith(SUFFIX)) return false;
        // Raw authority only: no URI normalisation, IDN conversion or default-port
        // equivalence. This rejects even explicit :443 and upper-case spellings.
        for (String label : host.split("\\.", -1))
            if (!label.matches("[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?")) return false;
        return true;
    }

    static Configuration parse(String url, Properties properties) throws SQLException {
        String db = database(url);
        Properties p = properties == null ? new Properties() : properties;
        for (Object key : p.keySet()) if (!(key instanceof String s) || !KEYS.contains(s)
                || !(p.get(key) instanceof String)) throw invalid();
        for (String key : p.stringPropertyNames()) if (!KEYS.contains(key)) throw invalid();
        try {
            String tenant = required(p, "tenantId", 64), client = required(p, "clientId", 64);
            String secret = required(p, "clientSecret", 4096);
            String guid = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
            if (!tenant.matches(guid) || !client.matches(guid)
                    || java.util.UUID.fromString(tenant).equals(new java.util.UUID(0, 0))
                    || java.util.UUID.fromString(client).equals(new java.util.UUID(0, 0))) throw invalid();
            Path library = Path.of(required(p, "nativeLibrary", 32768));
            if (!library.isAbsolute()) throw invalid();
            String timeoutText = p.getProperty("timeoutMillis", "30000");
            if (!timeoutText.matches("[0-9]+")) throw invalid();
            int timeout = Integer.parseInt(timeoutText);
            if (timeout < 1 || timeout > 600000) throw invalid();
            String host = new URI("https:" + url.substring(PREFIX.length())).getRawAuthority();
            return new Configuration(host, db, tenant, client, secret, library, timeout);
        } catch (java.net.URISyntaxException ignored) { throw invalid();
        } catch (RuntimeException ignored) { throw invalid(); }
    }

    private static String required(Properties p, String key, int maximum) throws SQLException {
        String value = p.getProperty(key);
        if (value == null || value.isEmpty()) throw invalid();
        Utf8.encode(value, maximum, "08001");
        return value;
    }

    static SQLException invalid() { return new SQLException("Invalid Eventhouse JDBC configuration.", "08001"); }
    // A record's generated toString would disclose the client secret.
    @Override public String toString() { return "Eventhouse JDBC configuration [redacted]"; }
}
