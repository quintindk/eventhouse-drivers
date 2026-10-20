package com.microsoft.eventhouse.jdbc;

import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.StandardCharsets;
import java.sql.SQLException;

final class Utf8 {
    private Utf8() { }
    static byte[] encode(String value, int maximum, String state) throws SQLException {
        try {
            if (value == null || value.length() > maximum || value.indexOf('\0') >= 0) throw new CharacterCodingException();
            ByteBuffer buffer = StandardCharsets.UTF_8.newEncoder().encode(CharBuffer.wrap(value));
            if (buffer.remaining() > maximum) throw new CharacterCodingException();
            byte[] bytes = new byte[buffer.remaining()];
            buffer.get(bytes);
            return bytes;
        } catch (CharacterCodingException ignored) { throw new SQLException("Invalid UTF-8 input or length.", state); }
    }
    static String decode(byte[] bytes) throws SQLException {
        try { return StandardCharsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(bytes)).toString(); }
        catch (CharacterCodingException ignored) { throw new SQLException("Invalid native UTF-8 output.", "HY000"); }
    }
}
