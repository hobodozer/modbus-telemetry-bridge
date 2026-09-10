// Bench and invariant check for ServerDataStore, the class that turns the tag bus into the
// register image an HMI reads.
//
// It exists because two bugs hid there that the smoke test could not see. The smoke test asserts
// behaviour at one block size and one stale policy; this sweeps the shapes and prints numbers, so
// a cost that scales with the size of the map shows up as a curve instead of a pass.
//
//   dotnet run --project tools/store-probe
//
// What to look for:
//   - "per read" must stay roughly flat as the point count grows. If it tracks the point count,
//     something is being re-evaluated per register instead of per request.
//   - failsafe/2000 must stay within a small factor of holdLastValue/0. A stale-sensitive block
//     re-encodes every point on every pass, so it is the configuration that exposes the problem
//     first - and it is what DefaultConfig ships.

using System.Diagnostics;
using ModbusBridge.Core.Config;
using ModbusBridge.Core.Data;
using ModbusBridge.Core.Engine;
using ModbusBridge.Core.Modbus;
using ModbusBridge.Core.Tags;

var context = new ModbusRequestContext("probe", "198.51.100.7:1");
var failures = 0;

void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    if (!ok) failures++;
}

static ServerDataStore Build(TagBus bus, ModbusArea area, StaleBehavior behavior, int staleMs,
                             params PointConfig[] points)
{
    var block = new ServerBlockConfig
    {
        Name = "probe", Area = area, StartAddress = 0, Size = 2000,
        StaleBehavior = behavior, StaleTimeoutMs = staleMs
    };
    foreach (var point in points) block.Points.Add(point);

    var map = new ServerMapConfig { Name = "map", UnitId = 1 };
    map.Blocks.Add(block);

    var config = new ModbusServerConfig { Name = "probe" };
    config.Maps.Clear();
    config.Maps.Add(map);
    return new ServerDataStore(config, bus);
}

static PointConfig[] Numbers(int count, AccessMode access)
{
    var points = new PointConfig[count];
    for (var i = 0; i < count; i++)
        points[i] = new PointConfig
        {
            Tag = $"probe.n{i}", Offset = i, DataType = PointDataType.UInt16, Access = access
        };
    return points;
}

// ---- Read cost against map size ------------------------------------------------------------
Console.WriteLine("Read cost, 125-register request (the largest a single FC3 can ask for):");
Console.WriteLine();
Console.WriteLine("  points   holdLastValue/0      failsafe/2000ms");
Console.WriteLine("  ------   ------------------   ------------------");

var scaling = new List<(int Points, double Hold, double Failsafe)>();

foreach (var pointCount in new[] { 32, 125, 317, 1000 })
{
    var row = new double[2];
    var policies = new[]
    {
        (Behaviour: StaleBehavior.HoldLastValue, StaleMs: 0),
        (Behaviour: StaleBehavior.Failsafe, StaleMs: 2000)
    };

    for (var p = 0; p < policies.Length; p++)
    {
        var bus = new TagBus();
        var store = Build(bus, ModbusArea.HoldingRegister, policies[p].Behaviour, policies[p].StaleMs,
                          Numbers(pointCount, AccessMode.Read));
        for (var i = 0; i < pointCount; i++) bus.GetOrAdd($"probe.n{i}").Set(TagValue.Good(i), "probe");

        var buffer = new ushort[125];
        for (var i = 0; i < 300; i++) store.ReadHoldingRegisters(1, 0, 125, buffer);   // warm up

        const int iterations = 3000;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) store.ReadHoldingRegisters(1, 0, 125, buffer);
        clock.Stop();

        row[p] = clock.Elapsed.TotalMilliseconds / iterations;
    }

    scaling.Add((pointCount, row[0], row[1]));
    Console.WriteLine($"  {pointCount,6}   {row[0],8:F3} ms/read   {row[1],8:F3} ms/read");
}

Console.WriteLine();

// A read costs what it costs, but it must not cost more just because the map got bigger: the
// request still only asks for 125 registers.
var smallest = scaling[0];
var largest = scaling[^1];
var growth = largest.Failsafe / Math.Max(smallest.Failsafe, 1e-6);
var sizeRatio = (double)largest.Points / smallest.Points;

Console.WriteLine($"  map grew {sizeRatio:F0}x, read cost grew {growth:F1}x");
Check(growth < sizeRatio / 2,
      $"read cost does not scale with map size ({growth:F1}x for a {sizeRatio:F0}x map)");
Check(largest.Failsafe < largest.Hold * 8,
      $"the stale-sensitive policy stays within 8x of the cheap one " +
      $"({largest.Failsafe / Math.Max(largest.Hold, 1e-6):F1}x)");

// ---- Write masking ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("Write masking:");
{
    var bus = new TagBus();
    var store = Build(bus, ModbusArea.HoldingRegister, StaleBehavior.HoldLastValue, 0,
        new PointConfig { Tag = "probe.ro", Offset = 0, DataType = PointDataType.UInt16, Access = AccessMode.Read },
        new PointConfig { Tag = "probe.rw", Offset = 1, DataType = PointDataType.UInt16, Access = AccessMode.ReadWrite },
        new PointConfig { Tag = "probe.b0", Offset = 2, DataType = PointDataType.Bool, BitIndex = 0, Access = AccessMode.ReadWrite },
        new PointConfig { Tag = "probe.b1", Offset = 2, DataType = PointDataType.Bool, BitIndex = 1, Access = AccessMode.Read });

    bus.GetOrAdd("probe.ro").Set(TagValue.Good(1234), "probe");
    bus.GetOrAdd("probe.rw").Set(TagValue.Good(5), "probe");
    bus.GetOrAdd("probe.b0").Set(TagValue.Good(0), "probe");
    bus.GetOrAdd("probe.b1").Set(TagValue.Good(1), "probe");

    store.WriteRegisters(1, 0, new ushort[] { 0xDEAD, 0xBEEF, 0x0001 }, context);

    var image = new ushort[4];
    store.ReadHoldingRegisters(1, 0, 4, image);

    Check(image[0] == 1234, $"a read-only point ignores a write over it (got {image[0]})");
    Check(image[1] == 0xBEEF, $"a writable point in the same request takes it (got 0x{image[1]:X4})");
    Check(image[2] == 0x0003, $"a writable bit sets without clearing its read-only neighbour (got 0x{image[2]:X4})");
    Check(image[3] == 0, "an unmapped address inside the block still reads as 0");

    var refused = store.WriteRegisters(1, 0, new ushort[] { 0xFFFF }, context);
    Check(refused == ModbusExceptionCode.IllegalDataAddress, "an all-read-only write is refused");
    store.ReadHoldingRegisters(1, 0, 1, image);
    Check(image[0] == 1234, "and the refused write changed nothing");
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "probe clean" : $"{failures} check(s) failed");
return failures == 0 ? 0 : 1;
