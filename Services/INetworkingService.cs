using System;
using NetworkingLibrary.Modules;

namespace NetworkingLibrary.Services
{
    /// <summary>
    /// </summary>
    public enum ReliableType { Unreliable, Reliable, UnreliableNoDelay }

    /// <summary>
    /// </summary>
    public interface INetworkingService
    {
        /// <summary>
        /// </summary>
        bool IsInitialized { get; }
        /// <summary>
        /// </summary>
        bool InLobby { get; }
        /// <summary>
        /// </summary>
        ulong HostSteamId64 { get; }
        /// <summary>
        /// </summary>
        string HostIdString { get; }
        /// <summary>
        /// </summary>
        bool IsHost { get; }
        /// <summary>
        /// </summary>
        ulong GetLocalSteam64();
        /// <summary>
        /// </summary>
        ulong[] GetLobbyMemberSteamIds();
        /// <summary>
        /// </summary>
        void Initialize();
        /// <summary>
        /// </summary>
        void Shutdown();

        /// <summary>
        /// </summary>
        void CreateLobby(int maxPlayers = 8);
        /// <summary>
        /// </summary>
        void JoinLobby(ulong lobbySteamId64);
        /// <summary>
        /// </summary>
        void LeaveLobby();
        /// <summary>
        /// </summary>
        void InviteToLobby(ulong steamId64);

        /// <summary>
        /// </summary>
        IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0);
        /// <summary>
        /// </summary>
        IDisposable RegisterNetworkType(Type type, uint modId, int mask = 0);
        /// <summary>
        /// </summary>
        void DeregisterNetworkObject(object instance, uint modId, int mask = 0);
        /// <summary>
        /// </summary>
        void DeregisterNetworkType(Type type, uint modId, int mask = 0);

        /// <summary>
        /// </summary>
        void RPC(uint modId, string methodName, ReliableType reliable, params object[] parameters);

        /// <summary>
        /// </summary>
        void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters);

        /// <summary>
        /// </summary>
        void RPCToHost(uint modId, string methodName, ReliableType reliable, params object[] parameters);

        /// <summary>
        /// </summary>
        void RegisterLobbyDataKey(string key);
        /// <summary>
        /// </summary>
        void SetLobbyData(string key, object value);
        /// <summary>
        /// </summary>
        T GetLobbyData<T>(string key);

        /// <summary>
        /// </summary>
        void RegisterPlayerDataKey(string key);
        /// <summary>
        /// </summary>
        void SetPlayerData(string key, object value);
        /// <summary>
        /// </summary>
        T GetPlayerData<T>(ulong steamId64, string key);

        /// <summary>
        /// </summary>
        void PollReceive();

        void RegisterModSigner(uint modId, Func<byte[], byte[]> signerDelegate);
        void RegisterModPublicKey(uint modId, System.Security.Cryptography.RSAParameters pub);

        /// <summary>
        /// </summary>
        event Action? LobbyCreated;
        /// <summary>
        /// </summary>
        event Action? LobbyEntered;
        /// <summary>
        /// </summary>
        event Action? LobbyLeft;
        /// <summary>
        /// </summary>
        event Action<ulong>? PlayerEntered;
        /// <summary>
        /// </summary>
        event Action<ulong>? PlayerLeft;
        /// <summary>
        /// </summary>
        event Action<string[]>? LobbyDataChanged;
        /// <summary>
        /// </summary>
        event Action<ulong, string[]>? PlayerDataChanged;

        /// <summary>
        /// </summary>
        Func<Message, ulong, bool>? IncomingValidator { get; set; }
    }
}
