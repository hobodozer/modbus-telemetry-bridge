using ModbusBridge.Core.Data;

namespace ModbusBridge.Core.Config;

/// <summary>
/// How the addresses written in this config map onto the numbers that go on the wire.
/// The wire address is always <c>configured - base</c>.
/// </summary>
public static class Addressing
{
    /// <summary>Addresses are already wire addresses: coil 0 is the first coil.</summary>
    public static readonly IReadOnlyDictionary<ModbusArea, int> ZeroBased = Uniform(0);

    /// <summary>Addresses count from 1 within each area: coil 1 is the first coil.</summary>
    public static readonly IReadOnlyDictionary<ModbusArea, int> OneBased = Uniform(1);

    /// <summary>
    /// Classic Modicon notation, where the leading digit encodes the area:
    /// 00001 coils, 10001 discrete inputs, 30001 input registers, 40001 holding registers.
    /// </summary>
    public static readonly IReadOnlyDictionary<ModbusArea, int> Modicon = new Dictionary<ModbusArea, int>
    {
        [ModbusArea.Coil] = 1,
        [ModbusArea.DiscreteInput] = 10001,
        [ModbusArea.InputRegister] = 30001,
        [ModbusArea.HoldingRegister] = 40001
    };

    private static Dictionary<ModbusArea, int> Uniform(int value) => new()
    {
        [ModbusArea.Coil] = value,
        [ModbusArea.DiscreteInput] = value,
        [ModbusArea.InputRegister] = value,
        [ModbusArea.HoldingRegister] = value
    };

    /// <summary>Named presets offered in the UI dropdown.</summary>
    public static IReadOnlyList<string> PresetNames { get; } =
        new[] { "Zero-based (wire)", "One-based", "Modicon 4xxxx" };

    public static Dictionary<ModbusArea, int>? Preset(string name) => name switch
    {
        "Zero-based (wire)" => null,                                   // scalar base of 0 covers it
        "One-based" => new Dictionary<ModbusArea, int>(OneBased),
        "Modicon 4xxxx" => new Dictionary<ModbusArea, int>(Modicon),
        _ => null
    };

    /// <summary>Best-effort reverse lookup so the UI can show which preset a config matches.</summary>
    public static string Describe(int scalarBase, IReadOnlyDictionary<ModbusArea, int>? perArea)
    {
        if (perArea is null || perArea.Count == 0)
            return scalarBase == 1 ? "One-based" : scalarBase == 0 ? "Zero-based (wire)" : $"Offset {scalarBase}";
        if (Matches(perArea, Modicon)) return "Modicon 4xxxx";
        if (Matches(perArea, OneBased)) return "One-based";
        if (Matches(perArea, ZeroBased)) return "Zero-based (wire)";
        return "Custom";
    }

    private static bool Matches(IReadOnlyDictionary<ModbusArea, int> a, IReadOnlyDictionary<ModbusArea, int> b) =>
        b.All(kv => a.TryGetValue(kv.Key, out var value) && value == kv.Value);
}
