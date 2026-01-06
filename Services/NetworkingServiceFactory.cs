using System;
using System.Reflection;
using Steamworks;
using UnityEngine;

namespace NetworkingLibrary.Services
{
    public static class NetworkingServiceFactory
    {
        public static INetworkingService CreateDefaultService()
        {
            if (SteamAPI.IsSteamRunning())
            {
                Net.Logger.LogInfo("Steam type present. Creating SteamNetworkingService.");
                return new SteamNetworkingService();
            } 
            else
            {
                Net.Logger.LogInfo("Steam not available. Creating OfflineNetworkingService.");
                return new OfflineNetworkingService();
            }
        }
    }
}
