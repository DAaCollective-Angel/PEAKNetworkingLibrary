using System;
using UnityEngine;

namespace NetworkingLibrary.Services
{
    public class NetworkingPoller : MonoBehaviour
    {
        void Update()
        {
            try
            {
                Net.Service?.PollReceive();
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"NetworkingPollerDebug PollReceive error: {ex}");
            }
            /*
            if (Time.unscaledTime - lastLog > 1f)
            {
                lastLog = Time.unscaledTime;
                Net.Logger.LogInfo("NetworkingPollerDebug: PollReceive tick");
            }*/
        }
        //private float lastLog = 0f;
        void Awake()
        {
            DontDestroyOnLoad(this.gameObject);
        }

        void OnDestroy()
        {
            // nothing
        }
    }
}