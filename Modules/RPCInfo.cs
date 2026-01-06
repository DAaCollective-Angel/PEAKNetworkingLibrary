using Steamworks;

namespace NetworkingLibrary.Modules
{
    /// <summary>
    /// </summary>
    public readonly struct RPCInfo
    {
        /// <summary>
        /// </summary>
        public readonly ulong SteamId64;
        /// <summary>
        /// </summary>
        public readonly string SteamIdString;
        /// <summary>
        /// </summary>
        public readonly bool IsLocalLoopback;

        /// <summary>
        /// </summary>
        public RPCInfo(ulong steamId64, string steamIdString, bool isLocalLoopback = false)
        {
            SteamId64 = steamId64;
            SteamIdString = steamIdString;
            IsLocalLoopback = isLocalLoopback;
        }

        public RPCInfo(ulong steamId64) : this(steamId64, steamId64.ToString(), false) { }
        public RPCInfo(CSteamID sid) : this(sid.m_SteamID, sid.ToString(), sid == SteamUser.GetSteamID()) { }

    }
}
