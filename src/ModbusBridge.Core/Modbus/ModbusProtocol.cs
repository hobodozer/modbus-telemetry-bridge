namespace ModbusBridge.Core.Modbus;

public static class FunctionCode
{
    public const byte ReadCoils = 0x01;
    public const byte ReadDiscreteInputs = 0x02;
    public const byte ReadHoldingRegisters = 0x03;
    public const byte ReadInputRegisters = 0x04;
    public const byte WriteSingleCoil = 0x05;
    public const byte WriteSingleRegister = 0x06;
    public const byte ReadExceptionStatus = 0x07;
    public const byte Diagnostics = 0x08;
    public const byte WriteMultipleCoils = 0x0F;
    public const byte WriteMultipleRegisters = 0x10;
    public const byte ReportServerId = 0x11;
    public const byte MaskWriteRegister = 0x16;
    public const byte ReadWriteMultipleRegisters = 0x17;
    public const byte ReadDeviceIdentification = 0x2B;

    public const byte ExceptionFlag = 0x80;
}

public static class ModbusExceptionCode
{
    public const byte IllegalFunction = 0x01;
    public const byte IllegalDataAddress = 0x02;
    public const byte IllegalDataValue = 0x03;
    public const byte ServerDeviceFailure = 0x04;
    public const byte Acknowledge = 0x05;
    public const byte ServerDeviceBusy = 0x06;
    public const byte GatewayPathUnavailable = 0x0A;
    public const byte GatewayTargetFailedToRespond = 0x0B;

    public static string Describe(byte code) => code switch
    {
        IllegalFunction => "illegal function (01) - the device does not support this function code",
        IllegalDataAddress => "illegal data address (02) - the address or quantity is outside the device's map",
        IllegalDataValue => "illegal data value (03) - a value in the request is not allowed",
        ServerDeviceFailure => "server device failure (04) - the device hit an unrecoverable error",
        Acknowledge => "acknowledge (05) - request accepted, still processing",
        ServerDeviceBusy => "server device busy (06) - retry later",
        GatewayPathUnavailable => "gateway path unavailable (0A) - bad unit id for this gateway",
        GatewayTargetFailedToRespond => "gateway target failed to respond (0B) - the downstream device is silent",
        _ => $"exception code {code:X2}"
    };
}

/// <summary>A Modbus exception response returned by the remote device.</summary>
public sealed class ModbusProtocolException : Exception
{
    public ModbusProtocolException(byte functionCode, byte exceptionCode)
        : base($"Function {functionCode:X2}: {ModbusExceptionCode.Describe(exceptionCode)}")
    {
        FunctionCode = functionCode;
        ExceptionCode = exceptionCode;
    }

    public byte FunctionCode { get; }
    public byte ExceptionCode { get; }
}

/// <summary>Protocol limits from the Modbus Application Protocol spec v1.1b3.</summary>
public static class ModbusLimits
{
    public const int MaxPduLength = 253;
    public const int MaxAduLength = 260;      // MBAP (7) + PDU (253)
    public const int MaxReadCoils = 2000;
    public const int MaxReadRegisters = 125;
    public const int MaxWriteCoils = 1968;
    public const int MaxWriteRegisters = 123;
}
