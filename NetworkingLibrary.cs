using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;
using UnityEngine;
using Mono.Cecil.Cil;
using System.Reflection;
using System.Linq;

using NetworkingLibrary.Services;
using NetworkingLibrary.Patches;
using NetworkingLibrary.Modules;
using NetworkingLibrary.Features;
using System.Xml.Linq;

namespace NetworkingLibrary
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]

    public class Net : BaseUnityPlugin
    {
        public static Net Instance { get; private set; } = null!;
        internal new static ManualLogSource Logger { get; private set; } = null!;
        internal static Harmony Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        public ConfigFile config = null!;
        public static INetworkingService? Service { get; private set; } 

        private void OnDestroy()
        {
            Service?.Shutdown();
        }

        private void Awake() {
            Logger = base.Logger;
            Instance = this;

            FileManager.InitializeConfig();

            Harmony.PatchAll();

            Service = NetworkingServiceFactory.CreateDefaultService();
            Service.Initialize();

            var go = new GameObject($"{MyPluginInfo.PLUGIN_NAME}.Poller");
            go.AddComponent<NetworkingPoller>().hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);

            Logger.LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} has fully loaded!");
        }
    }
}