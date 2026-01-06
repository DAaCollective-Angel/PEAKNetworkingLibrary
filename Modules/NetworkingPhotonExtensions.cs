using System;
using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using Steamworks;
using NetworkingLibrary.Services;

namespace NetworkingLibrary.Modules
{
    public static class NetworkingPhotonExtensions
    {
        public static Dictionary<int, ulong> MapPhotonActorsToSteam(INetworkingService svc)
        {
            var map = new Dictionary<int, ulong>();
            if (svc == null) return map;

            var lobbyIds = svc.GetLobbyMemberSteamIds();
            if (lobbyIds == null || lobbyIds.Length == 0) return map;
            if (!PhotonNetwork.InRoom) return map;

            var personaByName = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (var sid in lobbyIds)
            {
                try
                {
                    var name = SteamFriends.GetFriendPersonaName(new CSteamID(sid));
                    if (!string.IsNullOrEmpty(name) && !personaByName.ContainsKey(name))
                        personaByName[name] = sid;
                }
                catch { }
            }

            foreach (var kv in PhotonNetwork.CurrentRoom.Players)
            {
                int actor = kv.Key;
                Photon.Realtime.Player p = kv.Value;

                if (!string.IsNullOrEmpty(p.NickName) && personaByName.TryGetValue(p.NickName, out var matched))
                {
                    map[actor] = matched;
                }
            }

            return map;
        }
    }
}