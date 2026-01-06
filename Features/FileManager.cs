using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using BepInEx;
using UnityEngine;
using UnityEngine.Analytics;

namespace NetworkingLibrary.Features
{
    public static class FileManager
    {
        internal static ConfigEntry<T> BindConfig<T>(string Header, string Features, T Value, string? Info = "")
        {
            return Net.Instance.config.Bind(Header, Features, Value, Info);
        }
        internal static void InitializeConfig()
        {
            string ConfigFolderPath = Path.Combine(Paths.ConfigPath, $"DAa Mods/{MyPluginInfo.PLUGIN_NAME}");
            if (!Directory.Exists(ConfigFolderPath)) Directory.CreateDirectory(ConfigFolderPath);
            Net.Instance.config = new ConfigFile(Path.Combine(ConfigFolderPath, "config.cfg"), true);

            if (BindConfig("Version", "Current Version", "").Value != MyPluginInfo.PLUGIN_VERSION) Net.Instance.config.Clear();

            DefineConfig();
            Net.Logger.LogWarning($"< Config initialized >");
        }
        internal static void DefineConfig()
        {
            BindConfig("Version", "Current Version", MyPluginInfo.PLUGIN_VERSION, "Autoupdates the config / lets the mod know what version of config it is.");
        }
    }
}
