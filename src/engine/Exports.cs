using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eventhouse.Engine.Spike;

public static unsafe class Exports
{
    [UnmanagedCallersOnly(EntryPoint = SpikeContract.ExportPrefix + "result_next_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint Next(ulong result, byte* hasRow)
    {
        try { return (uint)NativeBoundary.Next(result, hasRow); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = SpikeContract.ExportPrefix + "result_current_cell_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint CurrentCell(ulong result, nuint column, uint* type, byte* isNull, byte* value, nuint capacity, nuint* required)
    {
        try { return (uint)NativeBoundary.CurrentCell(result, column, type, isNull, value, capacity, required); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = "eh_operation_prepare_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint PrepareOperation(ulong connection, uint timeoutMilliseconds, ulong* operation)
    {
        try { return (uint)NativeBoundary.PrepareOperation(connection, timeoutMilliseconds, operation); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = "eh_operation_execute_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint ExecuteOperation(ulong operation, byte* sql, nuint sqlLength, ulong* result)
    {
        try { return (uint)NativeBoundary.ExecuteOperation(operation, sql, sqlLength, result); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = "eh_operation_cancel_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint CancelOperation(ulong operation)
    {
        try { return (uint)NativeBoundary.CancelOperation(operation); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = "eh_operation_close_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint CloseOperation(ulong operation)
    {
        try { return (uint)NativeBoundary.CloseOperation(operation); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = SpikeContract.ExportPrefix + "abi_version_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint AbiVersion()
    {
        try { return NativeBoundary.AbiVersion(); }
        catch { Diagnostics.SetBoundaryFailure(); return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = SpikeContract.ExportPrefix + "open_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint Open(byte* queryUri, nuint queryUriLength, byte* database, nuint databaseLength,
        byte* tenantId, nuint tenantIdLength, byte* clientId, nuint clientIdLength,
        byte* clientSecret, nuint clientSecretLength, uint timeoutMilliseconds, ulong* connectionHandle)
    {
        try
        {
            return (uint)NativeBoundary.Open(queryUri, queryUriLength, database, databaseLength, tenantId, tenantIdLength,
                clientId, clientIdLength, clientSecret, clientSecretLength, timeoutMilliseconds, connectionHandle);
        }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = SpikeContract.ExportPrefix + "execute_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint Execute(ulong connectionHandle, byte* sql, nuint sqlLength, ulong* resultHandle)
    {
        try { return (uint)NativeBoundary.Execute(connectionHandle, sql, sqlLength, resultHandle); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = SpikeContract.ExportPrefix + "result_shape_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint ResultShape(ulong resultHandle, nuint* columnCount, nuint* rowCount)
    {
        try { return (uint)NativeBoundary.ResultShape(resultHandle, columnCount, rowCount); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = SpikeContract.ExportPrefix + "result_column_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint ResultColumn(ulong resultHandle, nuint columnIndex, uint* type, byte* nullable,
        byte* nameBuffer, nuint nameCapacity, nuint* nameRequired)
    {
        try { return (uint)NativeBoundary.Column(resultHandle, columnIndex, type, nullable, nameBuffer, nameCapacity, nameRequired); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = SpikeContract.ExportPrefix + "result_cell_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint ResultCell(ulong resultHandle, nuint rowIndex, nuint columnIndex, uint* type, byte* isNull,
        byte* valueBuffer, nuint valueCapacity, nuint* valueRequired)
    {
        try { return (uint)NativeBoundary.Cell(resultHandle, rowIndex, columnIndex, type, isNull, valueBuffer, valueCapacity, valueRequired); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = SpikeContract.ExportPrefix + "connection_close_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint CloseConnection(ulong connectionHandle)
    {
        try { return (uint)NativeBoundary.CloseConnection(connectionHandle); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = SpikeContract.ExportPrefix + "result_close_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint CloseResult(ulong resultHandle)
    {
        try { return (uint)NativeBoundary.CloseResult(resultHandle); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = SpikeContract.ExportPrefix + "error_code_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint ErrorCode()
    {
        try { return (uint)Diagnostics.Code; }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = SpikeContract.ExportPrefix + "error_message_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint ErrorMessage(byte* buffer, nuint capacity, nuint* required)
    {
        try { return (uint)NativeBoundary.CopyLastError(buffer, capacity, required); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

#if WP003_SPIKE_TEST_HOOKS
    [UnmanagedCallersOnly(EntryPoint = "eh_spike_test_result_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint TestResult(ulong* resultHandle)
    {
        try { return (uint)NativeBoundary.CreateTestResult(resultHandle); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = "eh_spike_test_validate_utf8_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint TestValidateUtf8(uint kind, byte* value, nuint length)
    {
        try { return (uint)NativeBoundary.ValidateTestUtf8(kind, value, length); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }

    [UnmanagedCallersOnly(EntryPoint = "eh_spike_test_throw_v1", CallConvs = [typeof(CallConvCdecl)])]
    public static uint TestThrow()
    {
        try { return (uint)NativeBoundary.ThrowTestException(); }
        catch { return (uint)Diagnostics.SetBoundaryFailure(); }
    }
#endif
}
