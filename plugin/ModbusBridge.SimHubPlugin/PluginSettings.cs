using System;
using System.IO;
using Newtonsoft.Json;

namespace ModbusBridge.SimHubPlugin
{
    /// <summary>
    /// The little the plugin needs to know locally. Everything else - which properties to stream,
    /// which values come back - is negotiated with the bridge over UDP, so this file rarely changes.
    /// </summary>
    public sealed class PluginSettings
    {
        /// <summary>Where the bridge is listening. Must match the bridge's telemetry listen port.</summary>
        public string BridgeHost { get; set; } = "127.0.0.1";
        public int BridgePort { get; set; } = 15600;

        /// <summary>Floor on how often data frames are sent, whatever rate SimHub calls us at.</summary>
        public int MinIntervalMs { get; set; } = 10;

        /// <summary>How often to announce ourselves while no bridge has answered.</summary>
        public int HelloIntervalMs { get; set; } = 1000;

        /// <summary>Root under which bridge values appear as SimHub properties.</summary>
        public string FeedbackPrefix { get; set; } = "Bridge";

        /// <summary>Raise a SimHub event when a fed-back value goes from zero to non-zero.</summary>
        public bool RaiseEventsOnFeedback { get; set; } = true;

        public bool VerboseLogging { get; set; }

        private const string FileName = "ModbusBridge.SimHubPlugin.json";

        /// <summary>
        /// Looks beside the DLL first (so a portable SimHub install keeps everything together),
        /// then LocalAppData. Missing or unreadable falls back to defaults rather than failing to
        /// load - a plugin that refuses to start is far worse than one using default settings.
        /// </summary>
        public static PluginSettings Load(out string loadedFrom)
        {
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var settings = JsonConvert.DeserializeObject<PluginSettings>(File.ReadAllText(path));
                    if (settings == null) continue;
                    loadedFrom = path;
                    return settings;
                }
                catch (Exception)
                {
                    // Try the next candidate.
                }
            }

            loadedFrom = "(defaults)";
            return new PluginSettings();
        }

        /// <summary>Writes a settings file to LocalAppData, which is always writable.</summary>
        public void SaveToUserProfile(out string savedTo)
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModbusBridge");
            Directory.CreateDirectory(directory);
            savedTo = Path.Combine(directory, FileName);
            File.WriteAllText(savedTo, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        private static string[] CandidatePaths()
        {
            var beside = Path.GetDirectoryName(typeof(PluginSettings).Assembly.Location) ?? ".";
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModbusBridge");

            return new[]
            {
                Path.Combine(beside, FileName),
                Path.Combine(appData, FileName)
            };
        }
    }
}
