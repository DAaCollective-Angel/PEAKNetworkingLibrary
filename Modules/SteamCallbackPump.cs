using System;
using UnityEngine;
using Steamworks;

public class SteamCallbackPump : MonoBehaviour
{
    void Update()
    {
        try
        {
            SteamAPI.RunCallbacks();
        }
        catch (Exception ex)
        {
            try { NetworkingLibrary.Net.Logger.LogError($"SteamAPI.RunCallbacks error: {ex}"); } catch { }
        }
    }
}
