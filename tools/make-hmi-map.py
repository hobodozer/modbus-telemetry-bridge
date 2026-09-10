#!/usr/bin/env python3
"""Generate the HMI-facing Modbus server map, and the SimHub subscriptions that feed it.

The Gamer HMI reads 4x-1..4x-2000. The layout is not ours to choose: it is fixed by the
compiled EasyBuilder project, verified by decoding the address field out of the .exob
(little-endian u16 at byte +31 of every NE/AE record - see tools/tia/README.md style notes
in docs). Field semantics and scaling come from the reference server the HMI was built
against, reference/src/PcTelemetryModbus/RegisterBank.cs, and reference/FS25.md.

This is a generator rather than hand-edited JSON because the component array alone is 1600
registers, and because the component count depends on the vehicle currently loaded.

    python tools/make-hmi-map.py rig/config/bridge.json [--components 16]

Scaling note: gain is raw -> engineering, so a field documented as "raw / 10" gets gain 0.1.
Min/Max are nullable and NULL DISABLES THE CLAMP - writing 0/0 pins every value to zero.
"""
import argparse
import collections
import io
import json


def scale(gain):
    # min/max stay null on purpose: 0/0 would clamp the value to zero.
    return collections.OrderedDict(
        [("gain", gain), ("offset", 0), ("min", None), ("max", None), ("deadband", 0)]
    )


def point(tag, offset, dtype, gain=1.0, desc="", length=0):
    p = collections.OrderedDict([
        ("tag", tag), ("offset", offset), ("dataType", dtype), ("bitIndex", -1),
        ("length", length), ("invert", False), ("access", "read"), ("failsafeValue", 0),
        ("enabled", True), ("description", desc), ("size", max(1, length)), ("deadband", 0),
    ])
    if gain != 1.0:
        p["scale"] = scale(gain)
    return p


def sub(prop, tag, gain=None, desc=""):
    s = collections.OrderedDict([("enabled", True), ("property", prop), ("tag", tag)])
    if gain is not None:
        s["scale"] = scale(gain)
    s["units"] = None
    s["description"] = desc
    return s


G = "DataCorePlugin.GameData."
RAW = "DataCorePlugin.GameRawData."

# ---- 0..15  host statistics, and the bridge's own state -------------------------------
HOST = [
    ("bridge.protocolVersion", 0, "uint16", 0.01, "Protocol version, 220 = 2.20"),
    ("bridge.statusFlags",     1, "uint16", 1.0,  "Status flags bitmask"),
    ("pc.uptimeMinutes",       2, "uint16", 1.0,  "Server uptime, minutes"),
    ("pc.cpuPercent",          3, "uint16", 0.1,  "CPU usage %"),
    ("pc.memPercent",          4, "uint16", 0.1,  "RAM usage %"),
    ("pc.memUsedGb",           5, "uint16", 0.1,  "RAM used GiB"),
    ("pc.memTotalGb",          6, "uint16", 0.1,  "RAM total GiB"),
    ("pc.gpuPercent",          7, "uint16", 0.1,  "GPU 3D usage %"),
    ("pc.diskPercent",         8, "uint16", 0.1,  "System drive usage %"),
    ("pc.netRxMbps",           9, "uint16", 0.1,  "Network receive Mbps"),
    ("pc.netTxMbps",          10, "uint16", 0.1,  "Network transmit Mbps"),
    ("bridge.connectedClients", 11, "uint16", 1.0, "Connected Modbus clients"),
    ("bridge.telemetryAgeMs", 12, "uint16", 1.0,  "Telemetry age ms, 65535 = never"),
    ("pc.processCount",       13, "uint16", 1.0,  "Process count"),
    ("pc.logicalCpus",        14, "uint16", 1.0,  "Logical CPU count"),
]

# ---- 100..142  normalised telemetry ---------------------------------------------------
# (offset, tag, type, gain, SimHub property or None, description)
NORM = [
    (100, "sim.speedKph", "uint16", 0.1, G + "SpeedKmh", "Speed km/h"),
    (101, "sim.speedMph", "uint16", 0.1, G + "SpeedMph", "Speed mph"),
    (102, "sim.rpm", "uint16", 1.0, G + "Rpms", "Engine rpm"),
    (103, "sim.maxRpm", "uint16", 1.0, G + "MaxRpm", "Max rpm"),
    (104, "sim.gear", "int16", 1.0, None, "Gear, -1 reverse 0 neutral"),
    (105, "sim.throttle", "uint16", 0.1, G + "Throttle", "Throttle %"),
    (106, "sim.brake", "uint16", 0.1, G + "Brake", "Brake %"),
    (107, "sim.clutch", "uint16", 0.1, G + "Clutch", "Clutch %"),
    (108, "sim.steering", "int16", 0.1, None, "Steering %"),
    (109, "fs.fuelPercent", "uint16", 0.1, None, "Fuel % (derived by the engine from level/capacity)"),
    (110, "sim.position", "uint16", 1.0, G + "Position", "Race position"),
    (111, "sim.lap", "uint16", 1.0, G + "CurrentLap", "Current lap"),
    (112, "sim.totalLaps", "uint16", 1.0, G + "TotalLaps", "Total laps"),
    (113, "sim.currentLapTime", "uint16", 0.01, G + "CurrentLapTime", "Current lap s"),
    (114, "sim.lastLapTime", "uint16", 0.01, G + "LastLapTime", "Last lap s"),
    (115, "sim.bestLapTime", "uint16", 0.01, G + "BestLapTime", "Best lap s"),
    (116, "sim.delta", "int16", 0.001, G + "DeltaToSessionBest", "Lap delta s"),
    (117, "sim.waterTempC", "uint16", 0.1, G + "WaterTemperature", "Coolant C"),
    (118, "sim.oilTempC", "uint16", 0.1, G + "OilTemperature", "Oil C"),
    (119, "sim.oilPressureKpa", "uint16", 0.1, G + "OilPressure", "Oil kPa"),
    (120, "sim.turboBoostKpa", "uint16", 0.1, None, "Boost kPa"),
    (121, "sim.latG", "int16", 0.001, None, "Lateral g"),
    (122, "sim.longG", "int16", 0.001, None, "Longitudinal g"),
    (123, "sim.vertG", "int16", 0.001, None, "Vertical g"),
    (124, "sim.tireTempFlC", "uint16", 0.1, G + "TyreTemperatureFrontLeft", "Tyre FL C"),
    (125, "sim.tireTempFrC", "uint16", 0.1, G + "TyreTemperatureFrontRight", "Tyre FR C"),
    (126, "sim.tireTempRlC", "uint16", 0.1, G + "TyreTemperatureRearLeft", "Tyre RL C"),
    (127, "sim.tireTempRrC", "uint16", 0.1, G + "TyreTemperatureRearRight", "Tyre RR C"),
    (128, "sim.tirePressureFlKpa", "uint16", 0.1, G + "TyrePressureFrontLeft", "Tyre FL kPa"),
    (129, "sim.tirePressureFrKpa", "uint16", 0.1, G + "TyrePressureFrontRight", "Tyre FR kPa"),
    (130, "sim.tirePressureRlKpa", "uint16", 0.1, G + "TyrePressureRearLeft", "Tyre RL kPa"),
    (131, "sim.tirePressureRrKpa", "uint16", 0.1, G + "TyrePressureRearRight", "Tyre RR kPa"),
    (132, "sim.brakeBias", "uint16", 0.1, G + "BrakeBias", "Brake bias %"),
    (133, "sim.absLevel", "uint16", 1.0, G + "ABSLevel", "ABS level"),
    (134, "sim.tcLevel", "uint16", 1.0, G + "TCLevel", "TC level"),
    (135, "sim.drsState", "uint16", 1.0, G + "DRSEnabled", "DRS state"),
    (136, "sim.pitLimiter", "uint16", 1.0, G + "PitLimiterOn", "Pit limiter"),
    (137, "sim.inPits", "uint16", 1.0, G + "IsInPit", "In pits"),
    (138, "sim.gameRunning", "uint16", 1.0, "DataCorePlugin.GameRunning", "Game running"),
    (139, "sim.paused", "uint16", 1.0, "DataCorePlugin.GamePaused", "Paused"),
    (140, "sim.fps", "uint16", 0.1, None, "Frame rate"),
    (141, "bridge.schemaVersion", "uint16", 0.01, None, "Schema version, 100 = 1.00"),
    (142, "bridge.gameCode", "uint16", 1.0, None, "0 none 1 FS25 2 FH6 3 BeamNG 255 other"),
]

# ---- 200..266  FS25 scalars -----------------------------------------------------------
# 32/64-bit compatibility fields at 207/211/215/227 are deliberately NOT mapped: FS25.md
# records that the HMI cannot render them and uses the 32-bit helpers at 258+ instead.
FS_SCALAR = [
    (200, "fs.schemaVersion", "uint16", 0.01, None, "FS25 block schema, 111 = 1.11"),
    (201, "fs.statusFlags", "uint16", 1.0, None, "FS25 status flags"),
    (202, "fs.day", "uint16", 1.0, RAW + "day", "Day number"),
    (203, "fs.dayTimeSeconds", "uint32", 1.0, RAW + "dayTime", "Seconds after midnight (source is SECONDS)"),
    (205, "fs.timeScale", "uint16", 0.01, RAW + "timeScale", "Time scale"),
    (206, "fs.timeScaleMultiplier", "uint16", 0.01, RAW + "timeScaleMultiplier", "Time-scale multiplier"),
    (219, "fs.vehiclePrice", "uint32", 0.01, RAW + "vehiclePrice", "Vehicle price"),
    (221, "fs.vehicleSellPrice", "uint32", 0.01, RAW + "vehicleSellPrice", "Vehicle sell price"),
    (223, "fs.fuelLevel", "uint32", 0.001, RAW + "fuelLevel", "Fuel litres"),
    (225, "fs.fuelCapacity", "uint32", 0.001, RAW + "fuelCapacity", "Fuel capacity litres"),
    (231, "fs.vehicleDamage", "uint16", 0.001, RAW + "vehicleDamageAmount", "Damage fraction"),
    (232, "fs.minRpm", "uint16", 1.0, RAW + "minRpm", "Minimum rpm"),
    (233, "fs.maxRpm", "uint16", 1.0, RAW + "maxRpm", "Maximum rpm"),
    (234, "fs.engineRpm", "uint16", 1.0, RAW + "Rpm", "Engine rpm"),
    (235, "fs.gear", "int16", 1.0, RAW + "gear", "Numeric gear index"),
    (236, "fs.rawSpeed", "int32", 0.001, RAW + "speed", "Mod native speed"),
    (238, "fs.motorTemperature", "uint16", 0.1, RAW + "motorTemperature", "Motor C"),
    (239, "fs.cruiseMaxSpeed", "uint16", 0.1, RAW + "cruiseControlMaxSpeed", "Cruise max km/h"),
    (240, "fs.componentSourceCount", "uint16", 1.0, None, "Source component count"),
    (241, "fs.componentStoredCount", "uint16", 1.0, None, "Stored component count"),
    (244, "fs.inVehicle", "uint16", 1.0, RAW + "isInVehicle", "In vehicle"),
    (245, "fs.leftIndicator", "uint16", 1.0, RAW + "leftTurnIndicator", "Left indicator"),
    (246, "fs.rightIndicator", "uint16", 1.0, RAW + "rightTurnIndicator", "Right indicator"),
    (247, "fs.beaconLights", "uint16", 1.0, RAW + "beaconLightsActive", "Beacon lights"),
    (248, "fs.cruiseControl", "uint16", 1.0, RAW + "cruiseControlActive", "Cruise control"),
    (249, "fs.gearsAvailable", "uint16", 1.0, RAW + "gearsAvailable", "Gears available"),
    (250, "fs.automatic", "uint16", 1.0, RAW + "isAutomatic", "Automatic transmission"),
    (251, "fs.gearChanging", "uint16", 1.0, RAW + "isGearChanging", "Gear changing"),
    (252, "fs.neutralWarning", "uint16", 1.0, RAW + "showNeutralWarning", "Neutral warning"),
    (253, "fs.motorStarted", "uint16", 1.0, RAW + "isMotorStarted", "Motor started"),
    (254, "fs.reverseDriving", "uint16", 1.0, RAW + "isReverseDriving", "Reverse driving"),
    (255, "fs.reverseDirection", "uint16", 1.0, RAW + "isReverseDirection", "Reverse direction"),
    (256, "fs.vehicleBroken", "uint16", 1.0, RAW + "isVehicleBroken", "Vehicle broken"),
    (257, "fs.motorFan", "uint16", 1.0, RAW + "motorFanEnabled", "Motor fan"),
    (258, "fs.playTimeSeconds", "uint32", 1.0, RAW + "playTime", "Play time (units unconfirmed)"),
    (260, "fs.physicsTimeSeconds", "uint32", 1000.0, RAW + "currentPhysicsTime", "Physics time s (source is MILLISECONDS)"),
    (262, "fs.money", "int32", 1.0, RAW + "money", "Money, whole units"),
    (264, "fs.operatingTimeSeconds", "uint32", 1000.0, RAW + "vehicleOperatingTime", "Operating time s (source is MILLISECONDS)"),
    (266, "fs.fuelPercent", "uint16", 0.1, None, "Fuel % helper (derived by the engine)"),
]

# ---- 272..379  FS25 text, byte counts from REGISTER_MAP.csv ---------------------------
FS_TEXT = [
    (144, 16, "bridge.simhubGame", None, "Active game name"),
    (272, 4, "fs.pluginVersion", RAW + "pluginVersion", "Plugin version"),
    (276, 16, "fs.mapTitle", RAW + "mapTitle", "Map title"),
    (292, 16, "fs.mapId", RAW + "mapId", "Map ID"),
    (308, 24, "fs.vehicleName", RAW + "vehicleName", "Vehicle name"),
    (332, 6, "fs.gearName", RAW + "gearName", "Gear name"),
    (338, 6, "fs.gearGroupName", RAW + "gearGroupName", "Gear group name"),
    (344, 6, "fs.prevGearName", RAW + "prevGearName", "Previous gear name"),
    (350, 6, "fs.nextGearName", RAW + "nextGearName", "Next gear name"),
    (356, 6, "fs.prevPrevGearName", RAW + "prevPrevGearName", "Previous-previous gear"),
    (362, 6, "fs.nextNextGearName", RAW + "nextNextGearName", "Next-next gear"),
    (368, 12, "fs.operatingTimeText", RAW + "vehicleOperatingTimeText", "Operating-time text"),
]

# ---- 400..1999  component array, 16 registers per component ---------------------------
COMPONENT_FIELDS = [
    (0, "angularVelocityX", "int16", 0.001), (1, "angularVelocityY", "int16", 0.001),
    (2, "angularVelocityZ", "int16", 0.001),
    (3, "linearVelocityX", "int16", 0.01), (4, "linearVelocityY", "int16", 0.01),
    (5, "linearVelocityZ", "int16", 0.01),
    (6, "worldTranslationX", "int32", 0.01), (8, "worldTranslationY", "int32", 0.01),
    (10, "worldTranslationZ", "int32", 0.01),
    (12, "quaternionX", "int16", 0.0001), (13, "quaternionY", "int16", 0.0001),
    (14, "quaternionZ", "int16", 0.0001), (15, "quaternionW", "int16", 0.0001),
]


def build(components):
    points, subs = [], []

    for tag, off, dtype, gain, desc in HOST:
        points.append(point(tag, off, dtype, gain, desc))

    for off, tag, dtype, gain, prop, desc in NORM + FS_SCALAR:
        points.append(point(tag, off, dtype, gain, desc))
        if prop:
            subs.append(sub(prop, tag, desc=desc))

    for off, words, tag, prop, desc in FS_TEXT:
        points.append(point(tag, off, "string", 1.0, desc + f" ({words * 2} bytes)", length=words))
        if prop:
            subs.append(sub(prop, tag, desc=desc))

    for n in range(components):
        base = 400 + 16 * n
        for rel, field, dtype, gain in COMPONENT_FIELDS:
            tag = f"fs.comp{n + 1:02d}.{field}"
            points.append(point(tag, base + rel, dtype, gain, f"Component {n + 1} {field}"))
            subs.append(sub(f"{RAW}vehicleComponents{n + 1:02d}.{field}", tag))

    points.sort(key=lambda p: p["offset"])
    return points, subs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("config")
    ap.add_argument("--components", type=int, default=16,
                    help="vehicle components to map. The HMI reserves 100 and each one costs "
                         "13 subscriptions. The old 8 KB single-datagram ceiling is gone - the "
                         "schema is chunked and MaxDatagram is 60000 - but the plugin must "
                         "speak wire version 2, so reinstall it before raising this")
    args = ap.parse_args()

    points, subs = build(args.components)

    d = json.load(io.open(args.config, encoding="utf-8-sig"),
                  object_pairs_hook=collections.OrderedDict)
    server = d["servers"][0]
    server["addressBase"] = 0          # Weintek 4x-1 is wire address 0
    mapping = server["maps"][0]
    mapping["blocks"] = [collections.OrderedDict([
        ("name", "HMI telemetry"), ("enabled", True), ("area", "holdingRegister"),
        ("startAddress", 0), ("size", 2000),
        ("staleBehavior", "holdLastValue"), ("staleTimeoutMs", 0),
        ("points", points),
    ])]
    d["telemetry"]["subscriptions"] = subs

    io.open(args.config, "w", encoding="utf-8").write(json.dumps(d, indent=2))
    print(f"points        : {len(points)}")
    print(f"subscriptions : {len(subs)}")
    print(f"components    : {args.components} (offsets 400..{400 + 16 * args.components - 1})")

    # Mirrors TelemetryProtocol.BuildSchema: HeaderLength(8) + 10 bytes of chunk header per
    # datagram, then 1 type byte + 2 length bytes + the UTF-8 name for each property. Printed
    # so raising --components is an informed choice rather than a guess.
    MAX_DATAGRAM = 60000
    chunks, used = 1, 8 + 10
    for s in subs:
        size = 3 + len(s["property"].encode("utf-8"))
        if used + size > MAX_DATAGRAM:
            chunks += 1
            used = 8 + 10
        used += size
    print(f"schema        : {chunks} datagram(s) of {MAX_DATAGRAM} bytes"
          f"{'' if chunks == 1 else '  <- needs a wire-version-2 plugin'}")


if __name__ == "__main__":
    main()
