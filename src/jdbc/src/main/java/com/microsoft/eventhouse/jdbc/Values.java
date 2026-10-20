package com.microsoft.eventhouse.jdbc;

import java.math.BigDecimal;
import java.sql.SQLException;
import java.sql.Timestamp;
import java.time.OffsetDateTime;
import java.time.format.DateTimeParseException;

final class Values {
    private Values() { }
    static SQLException malformed() { return new SQLException("Invalid value conversion.", "22018"); }
    static boolean bool(String text, boolean string) throws SQLException {
        return switch (text) {
            case "true" -> true;
            case "false" -> false;
            case "1" -> { if (!string) throw malformed(); yield true; }
            case "0" -> { if (!string) throw malformed(); yield false; }
            default -> throw malformed();
        };
    }
    static BigDecimal decimal(String text) throws SQLException {
        // Scan the entire syntax before reporting any budget error. All counters
        // are bounded by String length except exponent, which saturates above
        // every possible fractional count plus the admitted scale envelope.
        int length = text.length(), i = 0, digits = 0, fraction = 0, precision = 0;
        boolean significant = false;
        if (i < length && (text.charAt(i) == '+' || text.charAt(i) == '-')) i++;
        boolean dot = false;
        while (i < length) {
            char c = text.charAt(i);
            if (c == '.' && !dot) { dot = true; i++; continue; }
            if (c < '0' || c > '9') break;
            digits++;
            if (dot) fraction++;
            if (c != '0') significant = true;
            if (significant) precision++;
            i++;
        }
        if (digits == 0) throw malformed();
        long exponent = 0;
        boolean negativeExponent = false;
        if (i < length && (text.charAt(i) == 'e' || text.charAt(i) == 'E')) {
            i++;
            if (i < length && (text.charAt(i) == '+' || text.charAt(i) == '-'))
                negativeExponent = text.charAt(i++) == '-';
            int start = i;
            long cap = (long) Integer.MAX_VALUE + 10001;
            while (i < length && text.charAt(i) >= '0' && text.charAt(i) <= '9') {
                exponent = Math.min(cap, exponent * 10 + text.charAt(i++) - '0');
            }
            if (start == i) throw malformed();
        }
        if (i != length) throw malformed();
        long scale = fraction - (negativeExponent ? -exponent : exponent);
        if (length > 4096 || Math.max(1, precision) > 1024 || scale < -10000 || scale > 10000)
            throw range();
        try { return new BigDecimal(text); }
        catch (NumberFormatException ignored) { throw malformed(); }
    }
    static SQLException range() { return new SQLException("Numeric conversion exceeds JDBC range or budget.", "22003"); }
    static int integer(String text) throws SQLException { return (int) integer(text, true); }
    static long longInteger(String text) throws SQLException {
        return integer(text, false);
    }
    private static long integer(String text, boolean int32) throws SQLException {
        int length = text.length(), i = 0;
        boolean negative = length > 0 && text.charAt(0) == '-';
        if (length > 0 && (negative || text.charAt(0) == '+')) i++;
        int start = i, significant = -1;
        boolean invalid = false;
        for (; i < length; i++) {
            char c = text.charAt(i);
            if (c < '0' || c > '9') invalid = true;
            else if (c != '0' && significant < 0) significant = i;
        }
        if (start == length || invalid) throw malformed();
        if (significant < 0) return 0;
        String limit = int32 ? (negative ? "2147483648" : "2147483647")
                : (negative ? "9223372036854775808" : "9223372036854775807");
        int count = length - significant;
        if (count > limit.length()) throw range();
        if (count == limit.length()) {
            for (int n = 0; n < count; n++) {
                int comparison = text.charAt(significant + n) - limit.charAt(n);
                if (comparison > 0) throw range();
                if (comparison < 0) break;
            }
        }
        // Negative accumulation also represents MIN_VALUE without overflow.
        long value = 0;
        for (i = significant; i < length; i++) value = value * 10 - (text.charAt(i) - '0');
        return negative ? value : -value;
    }
    static double real(String text) throws SQLException {
        if (text.equals("NaN") || text.equals("Infinity") || text.equals("-Infinity") || text.equals("+Infinity"))
            throw new SQLException("Nonfinite double value.", "22003");
        if (!text.matches("-?(?:0|[1-9][0-9]*)(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?")) throw malformed();
        double value;
        try { value = Double.parseDouble(text); }
        catch (NumberFormatException ignored) { throw malformed(); }
        if (!Double.isFinite(value)) throw new SQLException("Double exceeds finite range.", "22003");
        return value;
    }
    static String datetimeText(String text) throws SQLException {
        timestampText(text); // Validate UTC even when requested through getString.
        return text;
    }
    static Timestamp timestamp(String text) throws SQLException { return timestampText(text); }
    private static Timestamp timestampText(String text) throws SQLException {
        if (!text.matches("[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,9})?(?:Z|\\+00:00)"))
            throw malformed();
        try {
            OffsetDateTime value = OffsetDateTime.parse(text);
            if (value.getYear() < 1) throw malformed();
            return Timestamp.from(value.toInstant());
        } catch (DateTimeParseException | IllegalArgumentException ignored) { throw malformed(); }
    }
}
