#if !UNITY_EDITOR
using NetworkingLibrary.Modules;
using pworld.Scripts;
using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace NetworkingLibrary.Services
{
    /// <summary>
    /// </summary>
    public class SteamNetworkingService : INetworkingService
    {
        const int CHANNEL = 120;
        const int MAX_IN_MESSAGES = 500;
        static IntPtr[] inMessages = new IntPtr[MAX_IN_MESSAGES]; 
        private readonly object rpcLock = new object();

        /// <summary>
        /// </summary>
        public bool IsInitialized { get; private set; } = false;
        /// <summary>
        /// </summary>
        public bool InLobby { get; private set; } = false;
        /// <summary>
        /// </summary>
        public ulong HostSteamId64
        {
            get
            {
                if (Lobby == CSteamID.Nil) return 0UL;
                var owner = SteamMatchmaking.GetLobbyOwner(Lobby);
                if (owner == CSteamID.Nil) return 0UL;
                return owner.m_SteamID;
            }
        }
        /// <summary>
        /// </summary>
        public string HostIdString
        {
            get
            {
                if (Lobby == CSteamID.Nil) return string.Empty;
                var owner = SteamMatchmaking.GetLobbyOwner(Lobby);
                return owner == CSteamID.Nil ? string.Empty : owner.ToString();
            }
        }

        public ulong GetLocalSteam64()
        {
            try
            {
                return SteamUser.GetSteamID().m_SteamID;
            }
            catch
            {
                return 0UL;
            }
        }

        public ulong[] GetLobbyMemberSteamIds()
        {
            if (!InLobby || Lobby == CSteamID.Nil)
                return Array.Empty<ulong>();

            try
            {
                int count = SteamMatchmaking.GetNumLobbyMembers(Lobby);
                if (count <= 0) return Array.Empty<ulong>();

                var outArr = new ulong[count];
                for (int i = 0; i < count; i++)
                {
                    var member = SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i);
                    outArr[i] = member == CSteamID.Nil ? 0UL : member.m_SteamID;
                }
                return outArr;
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"GetLobbyMemberSteamIds error: {ex}");
                return Array.Empty<ulong>();
            }
        }


        /// <summary>
        /// </summary>
        public CSteamID Lobby { get; private set; } = CSteamID.Nil;
        private CSteamID[] players = Array.Empty<CSteamID>();

        /// <summary>
        /// </summary>
        public bool IsHost
        {
            get
            {
                try
                {
                    if (!InLobby) return false;
                    var owner = SteamMatchmaking.GetLobbyOwner(Lobby);
                    if (owner == CSteamID.Nil) return false;
                    return owner == SteamUser.GetSteamID();
                }
                catch
                {
                    return false;
                }
            }
        }
        /// <summary>
        /// </summary>
        public event Action? LobbyCreated;
        /// <summary>
        /// </summary>
        public event Action? LobbyEntered;
        /// <summary>
        /// </summary>
        public event Action? LobbyLeft;
        /// <summary>
        /// </summary>
        public event Action<ulong>? PlayerEntered;
        /// <summary>
        /// </summary>
        public event Action<ulong>? PlayerLeft;
        /// <summary>
        /// </summary>
        public event Action<string[]>? LobbyDataChanged;
        /// <summary>
        /// </summary>
        public event Action<ulong, string[]>? PlayerDataChanged;

        /// <summary>
        /// </summary>
        public Func<Message, ulong, bool>? IncomingValidator { get; set; }

        private static readonly List<string> lobbyDataKeys = new();
        private static readonly List<string> playerDataKeys = new();
        private static readonly Dictionary<CSteamID, Dictionary<string, string>> lastPlayerData = new();
        private static readonly Dictionary<string, string> lastLobbyData = new();

        Callback<LobbyEnter_t>? cbLobbyEnter;
        Callback<LobbyCreated_t>? cbLobbyCreated;
        Callback<LobbyChatUpdate_t>? cbLobbyChatUpdate;
        Callback<LobbyDataUpdate_t>? cbLobbyDataUpdate;

        readonly Dictionary<uint, Dictionary<string, List<MessageHandler>>> rpcs = new();

        readonly Queue<QueuedSend> highQueue = new();
        readonly Queue<QueuedSend> normalQueue = new();
        readonly Queue<QueuedSend> lowQueue = new();
        readonly object queueLock = new();

        readonly Dictionary<(ulong target, ulong msgId), UnackedMessage> unacked = new();
        readonly object unackedLock = new();
        TimeSpan ackTimeout = TimeSpan.FromSeconds(1.2);
        int maxRetransmitAttempts = 5;

        private long _nextMessageId = 0;
        private ulong NextMessageId() => (ulong)Interlocked.Increment(ref _nextMessageId);
        readonly Dictionary<uint, ulong> outgoingSequencePerMod = new();
        readonly Dictionary<ulong, Dictionary<uint, ulong>> lastSeenSequence = new();

        readonly Dictionary<ulong, SlidingWindowRateLimiter> rateLimiters = new();

        readonly Dictionary<ulong, byte[]> perPeerSymmetricKey = new();
        byte[]? globalSharedSecret = null;
        HMACSHA256? globalHmac = null;

        readonly Dictionary<uint, Func<byte[], byte[]>> modSigners = new();
        readonly Dictionary<uint, RSAParameters> modPublicKeys = new();

        readonly Dictionary<ulong, HandshakeState> handshakeStates = new();

        const byte FRAG_FLAG = 0x1;
        const byte COMPRESSED_FLAG = 0x2;
        const byte HMAC_FLAG = 0x4;
        const byte SIGN_FLAG = 0x8;
        const byte ACK_FLAG = 0x10;

        RSACryptoServiceProvider? LocalRsa;

        /// <summary>
        /// </summary>
        public SteamNetworkingService() { }

        readonly Dictionary<(ulong sender, ulong msgId), FragmentBuffer> fragmentBuffers = new();
        readonly object fragmentLock = new();
        readonly TimeSpan FragmentTimeout = TimeSpan.FromSeconds(30);

        class FragmentBuffer
        {
            public int Total;
            public DateTime FirstSeen = DateTime.UtcNow;
            public Dictionary<int, byte[]> Fragments = new();
        }

        /// <summary>
        /// </summary>
        public void Initialize()
        {
            if (IsInitialized) return;

            if (Application.isPlaying)
            {
                try
                {
                    var go = GameObject.Find("SteamCallbackPump");
                    if (go == null)
                    {
                        go = new GameObject("SteamCallbackPump");
                        GameObject.DontDestroyOnLoad(go);
                        go.AddComponent<SteamCallbackPump>();
                        Net.Logger.LogInfo("Created SteamCallbackPump GameObject.");
                    }
                }
                catch (Exception ex)
                {
                    Net.Logger.LogError($"Failed to create SteamCallbackPump: {ex}");
                }
            }

            try
            {
                Message.MaxSize = (int)Constants.k_cbMaxSteamNetworkingSocketsMessageSizeSend;
            }
            catch { }

            cbLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            cbLobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            cbLobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            cbLobbyDataUpdate = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);

            LocalRsa = new RSACryptoServiceProvider(2048);

            IsInitialized = true;
            Net.Logger.LogInfo("SteamNetworkingService initialized");
        }

        /// <summary>
        /// </summary>
        public void Shutdown()
        {
            cbLobbyEnter = null;
            cbLobbyCreated = null;
            cbLobbyChatUpdate = null;
            cbLobbyDataUpdate = null;

            rpcs.Clear();
            unacked.Clear();
            handshakeStates.Clear();
            perPeerSymmetricKey.Clear();
            globalHmac?.Dispose();
            globalHmac = null;
            LocalRsa?.Dispose();
            LocalRsa = null;

            IsInitialized = false;
            lock (fragmentLock) fragmentBuffers.Clear();
            Net.Logger.LogInfo("SteamNetworkingService shutdown");
        }

        /// <summary>
        /// </summary>
        public void CreateLobby(int maxPlayers = 8) => SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, maxPlayers);
        /// <summary>
        /// </summary>
        public void JoinLobby(ulong lobbySteamId64) => SteamMatchmaking.JoinLobby(new CSteamID(lobbySteamId64));
        /// <summary>
        /// </summary>
        public void LeaveLobby() => OnLobbyLeftInternal();
        /// <summary>
        /// </summary>
        public void InviteToLobby(ulong steamId64) => SteamMatchmaking.InviteUserToLobby(Lobby, new CSteamID(steamId64));

        void OnLobbyEnter(LobbyEnter_t param)
        {
            Net.Logger.LogDebug($"LobbyEnter {param.m_ulSteamIDLobby}");
            Lobby = new CSteamID(param.m_ulSteamIDLobby);
            InLobby = true;
            RefreshPlayerList();
            LobbyEntered?.Invoke();
        }

        void OnLobbyCreated(LobbyCreated_t param)
        {
            Net.Logger.LogDebug($"LobbyCreated: {param.m_eResult}");
            if (param.m_eResult == EResult.k_EResultOK)
            {
                Lobby = new CSteamID(param.m_ulSteamIDLobby);
                InLobby = true;
                RefreshPlayerList();
                LobbyCreated?.Invoke();
            }
            else
            {
                Net.Logger.LogError($"Lobby creation failed: {param.m_eResult}");
            }
        }

        void OnLobbyChatUpdate(LobbyChatUpdate_t param)
        {
            try
            {
                RefreshPlayerList();
                var player = new CSteamID(param.m_ulSteamIDUserChanged);
                var change = (EChatMemberStateChange)param.m_rgfChatMemberStateChange;

                if ((change & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
                {
                    //Net.Logger.LogInfo($"OnLobbyChatUpdate: Entered -> {player}");
                    PlayerEntered?.Invoke(player.m_SteamID);
                }

                var leftMask =
                    EChatMemberStateChange.k_EChatMemberStateChangeLeft |
                    EChatMemberStateChange.k_EChatMemberStateChangeDisconnected |
                    EChatMemberStateChange.k_EChatMemberStateChangeKicked |
                    EChatMemberStateChange.k_EChatMemberStateChangeBanned;

                if ((change & leftMask) != 0)
                {
                    //Net.Logger.LogInfo($"OnLobbyChatUpdate: Left/Disconnected/Kicked/Banned -> {player}");
                    PlayerLeft?.Invoke(player.m_SteamID);
                }
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"OnLobbyChatUpdate error: {ex}");
            }
        }

        internal void OnLobbyLeftInternal()
        {
            RefreshPlayerList();
            lastLobbyData.Clear();
            lastPlayerData.Clear();
            InLobby = false;
            LobbyLeft?.Invoke();
        }

        void RefreshPlayerList()
        {
            try
            {
                //Net.Logger.LogInfo($"RefreshPlayerList: Lobby={Lobby} Owner={SteamMatchmaking.GetLobbyOwner(Lobby)} Local={SteamUser.GetSteamID()} InLobby={InLobby}");

                if (Lobby == null || Lobby == CSteamID.Nil)
                {
                    //Net.Logger.LogWarning("RefreshPlayerList: Lobby is Nil; cannot query members.");
                    players = Array.Empty<CSteamID>();
                    return;
                }

                int count = SteamMatchmaking.GetNumLobbyMembers(Lobby);
                //Net.Logger.LogInfo($"RefreshPlayerList: SteamMatchmaking.GetNumLobbyMembers returned {count}");
                players = new CSteamID[count];

                for (int i = 0; i < players.Length; i++)
                {
                    players[i] = SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i);
                    //Net.Logger.LogInfo($"RefreshPlayerList: member[{i}] = {players[i]}");
                }
                Net.Logger.LogDebug($"RefreshPlayerList: total members = {players.Length}");
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"RefreshPlayerList error: {ex}");
                players = Array.Empty<CSteamID>();
            }
        }

        void OnLobbyDataUpdate(LobbyDataUpdate_t param)
        {
            if (!InLobby) return;
            if (param.m_ulSteamIDLobby != Lobby.m_SteamID) return;

            if (param.m_ulSteamIDLobby == param.m_ulSteamIDMember)
            {
                var changed = new List<string>();
                foreach (var key in lobbyDataKeys)
                {
                    var data = SteamMatchmaking.GetLobbyData(Lobby, key);
                    if (!lastLobbyData.TryGetValue(key, out var prev) || prev != data)
                    {
                        changed.Add(key);
                        lastLobbyData[key] = data;
                    }
                }
                if (changed.Count > 0) LobbyDataChanged?.Invoke(changed.ToArray());
            }
            else
            {
                var player = new CSteamID(param.m_ulSteamIDMember);
                if (!lastPlayerData.ContainsKey(player)) lastPlayerData[player] = new Dictionary<string, string>();
                var changed = new List<string>();
                foreach (var key in playerDataKeys)
                {
                    var data = SteamMatchmaking.GetLobbyMemberData(Lobby, player, key);
                    if (!lastPlayerData[player].TryGetValue(key, out var prev) || prev != data)
                    {
                        changed.Add(key);
                        lastPlayerData[player][key] = data;
                    }
                }
                if (changed.Count > 0) PlayerDataChanged?.Invoke(player.m_SteamID, changed.ToArray());
            }
        }

        /// <summary>
        /// </summary>
        public void RegisterLobbyDataKey(string key)
        {
            if (lobbyDataKeys.Contains(key)) Net.Logger.LogWarning($"Lobby key {key} already registered");
            else lobbyDataKeys.Add(key);
        }

        /// <summary>
        /// </summary>
        public void SetLobbyData(string key, object value)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot set lobby data when not in lobby."); return; }
            if (!lobbyDataKeys.Contains(key)) Net.Logger.LogWarning($"Accessing unregistered lobby key '{key}'.");
            SteamMatchmaking.SetLobbyData(Lobby, key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// </summary>
        public T GetLobbyData<T>(string key)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot get lobby data when not in lobby."); return default(T)!; }
            if (!lobbyDataKeys.Contains(key)) Net.Logger.LogWarning($"Accessing unregistered lobby key '{key}'.");
            string v = SteamMatchmaking.GetLobbyData(Lobby, key);
            if (string.IsNullOrEmpty(v)) return default(T)!;
            try { return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
            catch { Net.Logger.LogError($"Could not parse lobby data [{key},{v}] as {typeof(T).Name}"); return default(T)!; }
        }

        /// <summary>
        /// </summary>
        public void RegisterPlayerDataKey(string key)
        {
            if (playerDataKeys.Contains(key)) Net.Logger.LogWarning($"Player key {key} already registered");
            else playerDataKeys.Add(key);
        }

        /// <summary>
        /// </summary>
        public void SetPlayerData(string key, object value)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot set player data when not in lobby."); return; }
            if (!playerDataKeys.Contains(key)) Net.Logger.LogWarning($"Accessing unregistered player key '{key}'.");
            SteamMatchmaking.SetLobbyMemberData(Lobby, key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// </summary>
        public T GetPlayerData<T>(ulong steamId64, string key)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot get player data when not in lobby."); return default(T)!; }
            if (!playerDataKeys.Contains(key)) Net.Logger.LogWarning($"Accessing unregistered player key '{key}'.");
            var player = new CSteamID(steamId64);
            string v = SteamMatchmaking.GetLobbyMemberData(Lobby, player, key);
            if (string.IsNullOrEmpty(v)) return default(T)!;
            try { return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
            catch { Net.Logger.LogError($"Could not parse player data [{key},{v}] as {typeof(T).Name}"); return default(T)!; }
        }

        /// <summary>
        /// </summary>
        public IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            return RegisterNetworkTypeInternal(instance.GetType(), instance, modId, mask);
        }
        /// <summary>
        /// </summary>
        public IDisposable RegisterNetworkType(Type type, uint modId, int mask = 0)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            return RegisterNetworkTypeInternal(type, null, modId, mask);
        }
        private IDisposable RegisterNetworkTypeInternal(Type type, object? instance, uint modId, int mask)
        {
            int registered = 0;

            lock (rpcLock)
            {
                var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var method in methods)
                {
                    var attrs = method.GetCustomAttributes(false).OfType<CustomRPCAttribute>().ToArray();
                    if (attrs.Length == 0) continue;

                    if (!rpcs.ContainsKey(modId)) rpcs[modId] = new Dictionary<string, List<MessageHandler>>();
                    if (!rpcs[modId].ContainsKey(method.Name)) rpcs[modId][method.Name] = new List<MessageHandler>();

                    var mh = new MessageHandler
                    {
                        Target = method.IsStatic ? null! : instance!,
                        Method = method,
                        Parameters = method.GetParameters(),
                        TakesInfo = method.GetParameters().Length > 0 && method.GetParameters().Last().ParameterType.Name == "RPCInfo",
                        Mask = mask
                    };
                    rpcs[modId][method.Name].Add(mh);
                    registered++;
                }
            }

            if (instance != null)
                Net.Logger.LogInfo($"Registered {registered} RPCs for mod {modId} on {instance} ({instance.GetType().FullName})");
            else
                Net.Logger.LogInfo($"Registered {registered} static RPCs for mod {modId} on type {type.FullName}");

            return new RegistrationToken(this, instance, type, modId, mask);
        }

        /// <summary>
        /// </summary>
        public void DeregisterNetworkObject(object instance, uint modId, int mask = 0)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            DeregisterNetworkObjectInternal(instance.GetType(), instance, modId, mask);
        }
        /// <summary>
        /// </summary>
        public void DeregisterNetworkType(Type type, uint modId, int mask = 0)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            DeregisterNetworkObjectInternal(type, null, modId, mask);
        }
        private void DeregisterNetworkObjectInternal(Type type, object? instanceOrNull, uint modId, int mask)
        {
            lock (rpcLock)
            {
                if (!rpcs.TryGetValue(modId, out var methods))
                {
                    Net.Logger.LogWarning($"No RPCs for mod {modId}");
                    return;
                }

                int removed = 0;
                var methodNames = methods.Keys.ToArray();
                foreach (var methodName in methodNames)
                {
                    if (!methods.TryGetValue(methodName, out var handlers)) continue;

                    for (int i = handlers.Count - 1; i >= 0; i--)
                    {
                        var mh = handlers[i];

                        if (instanceOrNull != null && mh.Target != null && ReferenceEquals(mh.Target, instanceOrNull) && mh.Mask == mask)
                        {
                            handlers.RemoveAt(i);
                            removed++;
                            continue;
                        }
                        if (instanceOrNull == null && mh.Target == null && mh.Method.DeclaringType == type && mh.Mask == mask)
                        {
                            handlers.RemoveAt(i);
                            removed++;
                            continue;
                        }
                    }

                    if (handlers.Count == 0) methods.Remove(methodName);
                }

                if (methods.Count == 0) rpcs.Remove(modId);

                Net.Logger.LogInfo($"Deregistered {removed} RPCs for mod {modId} (type/instance {type.FullName})");
            }
        }

        sealed class RegistrationToken : IDisposable
        {
            private readonly SteamNetworkingService svc;
            private readonly object? instance;
            private readonly Type? registeredType;
            private readonly uint modId;
            private readonly int mask;
            private bool disposed;

            public RegistrationToken(SteamNetworkingService svc, object? instance, Type? registeredType, uint modId, int mask)
            {
                this.svc = svc;
                this.instance = instance;
                this.registeredType = registeredType;
                this.modId = modId;
                this.mask = mask;
                this.disposed = false;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;

                if (instance != null)
                {
                    svc.DeregisterNetworkObject(instance, modId, mask);
                }
                else if (registeredType != null)
                {
                    svc.DeregisterNetworkType(registeredType, modId, mask);
                }
            }
        }

        /// <summary>
        /// </summary>
        public void RPC(uint modId, string methodName, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { Net.Logger.LogError("RPC called while not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters);
            if (msg == null) return;

            //Net.Logger.LogError($"{players}");
            foreach (var p in players)
            {
                //Net.Logger.LogError($"{p}");
                if (p == SteamUser.GetSteamID())
                {
                    //Net.Logger.LogError($"{SteamUser.GetSteamID()} == {p}");
                    continue;
                }
                EnqueueOrSend(BuildFramedBytesWithMeta(msg, modId, reliable), p, reliable, DeterminePriority(modId, methodName));
                //Net.Logger.LogError($"Fired to user: {p}");
            }

            InvokeLocalMessage(new Message(msg.ToArray()), SteamUser.GetSteamID());
        }

        /// <summary>
        /// </summary>
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters)
        {
            RPCTarget(modId, methodName, new CSteamID(targetSteamId64), reliable, parameters);
        }

        /// <summary>
        /// </summary>
        public void RPCTarget(uint modId, string methodName, CSteamID target, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot RPC target when not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters);
            if (msg == null) return;
            var framed = BuildFramedBytesWithMeta(msg, modId, reliable);
            EnqueueOrSend(framed, target, reliable, DeterminePriority(modId, methodName));
        }

        /// <summary>
        /// </summary>
        public void RPCToHost(uint modId, string methodName, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { Net.Logger.LogError("Not in lobby"); return; }
            var host = SteamMatchmaking.GetLobbyOwner(Lobby);
            if (host == CSteamID.Nil) { Net.Logger.LogError("No host set"); return; }
            RPCTarget(modId, methodName, host, reliable, parameters);
        }

        Priority DeterminePriority(uint modId, string methodName)
        {
            var lower = methodName.ToLowerInvariant();
            if (lower.Contains("admin") || lower.Contains("control") || lower.Contains("critical") || lower.Contains("sync")) return Priority.High;
            return Priority.Normal;
        }

        void EnqueueOrSend(byte[] framed, CSteamID target, ReliableType reliable, Priority p)
        {
            var rl = GetOrCreateRateLimiter(target.m_SteamID);
            if (!rl.Allowed())
            {
                //Net.Logger.LogWarning($"Rate limit: dropping send to {target}");
                return;
            }

            if (p == Priority.High)
            {
                SendWithPossibleAck(framed, target, reliable);
                return;
            }

            lock (queueLock)
            {
                var q = p == Priority.Normal ? normalQueue : lowQueue;
                q.Enqueue(new QueuedSend { Framed = framed, Target = target, Reliable = reliable, Enqueued = DateTime.UtcNow });
            }
        }

        void FlushQueues(int maxPerFrame = 8)
        {
            int sent = 0;
            while (sent < maxPerFrame)
            {
                QueuedSend item = null!;
                lock (queueLock)
                {
                    if (normalQueue.Count > 0) item = normalQueue.Dequeue();
                    else if (lowQueue.Count > 0) item = lowQueue.Dequeue();
                    else break;
                }
                if (item != null)
                {
                    SendWithPossibleAck(item.Framed, item.Target, item.Reliable);
                    sent++;
                }
            }
        }

        byte[] BuildFramedBytesWithMeta(Message msg, uint modId, ReliableType reliable)
        {
            var payload = msg.ToArray();
            bool compress = payload.Length > 1024;
            if (compress) payload = msg.CompressPayload(); // Uncertain of this messes up everything or not, did not test this.

            byte flags = 0;
            if (compress) flags |= COMPRESSED_FLAG;
            if (globalHmac != null) flags |= HMAC_FLAG;
            if (modSigners.ContainsKey(modId)) flags |= SIGN_FLAG;

            ulong seq;
            lock (outgoingSequencePerMod)
            {
                if (!outgoingSequencePerMod.TryGetValue(modId, out var cur)) cur = 0;
                seq = ++cur;
                outgoingSequencePerMod[modId] = cur;
            }

            ulong msgId = NextMessageId();

            using var ms = new MemoryStream();
            ms.WriteByte(flags);
            ms.Write(BitConverter.GetBytes(msgId), 0, 8);
            ms.Write(BitConverter.GetBytes(seq), 0, 8);
            ms.Write(BitConverter.GetBytes(1), 0, 4);
            ms.Write(BitConverter.GetBytes(0), 0, 4);
            ms.Write(payload, 0, payload.Length);

            byte[] headerAndPayload = ms.ToArray();

            if (modSigners.TryGetValue(modId, out var signer))
            {
                var sig = signer(headerAndPayload);
                using var ms2 = new MemoryStream();
                ms2.Write(headerAndPayload, 0, headerAndPayload.Length);
                var len = (ushort)sig.Length;
                ms2.Write(BitConverter.GetBytes(len), 0, 2);
                ms2.Write(sig, 0, sig.Length);
                headerAndPayload = ms2.ToArray();
            }

            if (globalHmac != null)
            {
                var mac = globalHmac!.ComputeHash(headerAndPayload);
                using var ms3 = new MemoryStream();
                ms3.Write(headerAndPayload, 0, headerAndPayload.Length);
                ms3.Write(mac, 0, mac.Length);
                headerAndPayload = ms3.ToArray();
            }

            if (reliable == ReliableType.Reliable) headerAndPayload[0] = (byte)(headerAndPayload[0] | ACK_FLAG);

            return headerAndPayload;
        }

        void SendWithPossibleAck(byte[] framed, CSteamID target, ReliableType reliable)
        {
            if (perPeerSymmetricKey.TryGetValue(target.m_SteamID, out var sym))
            {
                using var h = new HMACSHA256(sym);
                var mac = h.ComputeHash(framed);
                using var ms = new MemoryStream();
                ms.Write(framed, 0, framed.Length);
                ms.Write(mac, 0, mac.Length);
                framed = ms.ToArray();
                framed[0] = (byte)(framed[0] | HMAC_FLAG);
            }

            bool requestAck = (framed[0] & ACK_FLAG) != 0;

            if (requestAck)
            {
                var msgId = BitConverter.ToUInt64(framed, 1);
                var key = (target.m_SteamID, msgId);
                lock (unackedLock)
                {
                    unacked[key] = new UnackedMessage { Framed = framed, Target = target, Reliable = reliable, LastSent = DateTime.UtcNow, Attempts = 1 };
                }
            }

            SendBytes(framed, target, reliable);
        }
        void SendBytes(byte[] data, CSteamID target, ReliableType reliable)
        {
            if (data.Length > Message.MaxSize)
            {
                Net.Logger.LogError($"Send length {data.Length} exceeds Message.MaxSize {Message.MaxSize}");
                return;
            }

            if (target == SteamUser.GetSteamID())
            {
                var m = new Message(data);
                InvokeLocalMessage(m, SteamUser.GetSteamID());
                return;
            }

            var id = new SteamNetworkingIdentity();
            id.SetSteamID(target);
            GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr p = pinned.AddrOfPinnedObject();

                int flags = Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
                switch (reliable)
                {
                    case ReliableType.Unreliable: flags |= Constants.k_nSteamNetworkingSend_Unreliable; break;
                    case ReliableType.Reliable: flags |= Constants.k_nSteamNetworkingSend_Reliable; break;
                    case ReliableType.UnreliableNoDelay: flags |= Constants.k_nSteamNetworkingSend_UnreliableNoDelay; break;
                }

                var res = SteamNetworkingMessages.SendMessageToUser(ref id, p, (uint)data.Length, flags, CHANNEL);
                //Net.Logger.LogInfo($"SendMessageToUser -> res={res} to={target} framedLen={data.Length}");
                if (res != EResult.k_EResultOK)
                {
                    Net.Logger.LogError($"SendMessageToUser failed: {res} to {target}");
                }
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"SendBytes exception: {ex}");
            }
            finally
            {
                if (pinned.IsAllocated) pinned.Free();
            }
        }

        /// <summary>
        /// </summary>
        public void PollReceive()
        {
            try
            {
                SteamAPI.RunCallbacks();
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"SteamAPI.RunCallbacks error: {ex}");
            }

            FlushQueues();
            RetransmitUnacked();
            ReceiveMessages();
        }

        void RetransmitUnacked()
        {
            var toRetransmit = new List<UnackedMessage>();
            lock (unackedLock)
            {
                var now = DateTime.UtcNow;
                var keys = unacked.Keys.ToArray();
                foreach (var key in keys)
                {
                    var info = unacked[key];
                    if (now - info.LastSent > ackTimeout)
                    {
                        if (info.Attempts >= maxRetransmitAttempts)
                        {
                            unacked.Remove(key);
                            //Net.Logger.LogDebug($"RetransmitUnacked: Message {key.msgId} to {key.target} dropped after {info.Attempts} attempts. framedLen={info.Framed?.Length ?? 0}");
                        }
                        else
                        {
                            info.Attempts++;
                            info.LastSent = now;
                            unacked[key] = info;
                            toRetransmit.Add(info);
                            //Net.Logger.LogDebug($"RetransmitUnacked: scheduling retransmit attempt {info.Attempts} for msg {key.msgId} to {key.target}");
                        }
                    }
                }
            }

            foreach (var item in toRetransmit)
            {
                try
                {
                    SendBytes(item.Framed, item.Target, item.Reliable);
                }
                catch (Exception ex)
                {
                    Net.Logger.LogError($"RetransmitUnacked: retransmit SendBytes exception: {ex}");
                }
            }
        }

        void ReceiveMessages()
        {
            try
            {
                int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(CHANNEL, inMessages, MAX_IN_MESSAGES);
                //if (count > 0) Net.Logger.LogInfo($"ReceiveMessages: count={count} (channel {CHANNEL})");
                if (count <= 0) return;

                for (int i = 0; i < count; i++)
                {
                    IntPtr outPtr = inMessages[i];
                    SteamNetworkingMessage_t steamMsg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(outPtr);
                    int size = (int)steamMsg.m_cbSize;

                    //Net.Logger.LogInfo($"ReceiveMessages: rawMsg[{i}] size={size} ptr={outPtr}");

                    if (size <= 0)
                    {
                        //Net.Logger.LogWarning("ReceiveMessages: msg size <= 0, releasing.");
                        SteamNetworkingMessage_t.Release(outPtr);
                        continue;
                    }

                    if (size > Message.MaxSize)
                    {
                        //Net.Logger.LogError($"Incoming message size {size} > max {Message.MaxSize} (dropping)");
                        SteamNetworkingMessage_t.Release(outPtr);
                        continue;
                    }

                    CSteamID sender = steamMsg.m_identityPeer.GetSteamID();
                    if (sender == CSteamID.Nil)
                    {
                        //Net.Logger.LogWarning("ReceiveMessages: sender is Nil - skipping");
                        SteamNetworkingMessage_t.Release(outPtr);
                        continue;
                    }

                    byte[] bytes = new byte[size];
                    Marshal.Copy(steamMsg.m_pData, bytes, 0, size);

                    /*int dumpLen = Math.Min(32, bytes.Length);
                    var sb = new System.Text.StringBuilder();
                    for (int b = 0; b < dumpLen; b++) sb.AppendFormat("{0:X2} ", bytes[b]);
                    Net.Logger.LogInfo($"ReceiveMessages: from={sender} size={size} preview={sb}");*/
                    try
                    {
                        ProcessIncomingFrame(bytes, sender);
                    }
                    catch (Exception ex)
                    {
                        Net.Logger.LogError($"ProcessIncomingFrame exception: {ex}");
                    }

                    SteamNetworkingMessage_t.Release(outPtr);
                }
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"ReceiveMessages outer exception: {ex}");
            }
        }

        void ProcessIncomingFrame(byte[] frame, CSteamID sender)
        {
            //Net.Logger.LogInfo($"ProcessIncomingFrame: from={sender} bytes={frame.Length}");

            using var msHeader = new MemoryStream(frame);
            int flags = msHeader.ReadByte();
            if (flags < 0)
            {
                //Net.Logger.LogWarning("ProcessIncomingFrame: flags read < 0");
                return;
            }

            bool compressed = (flags & COMPRESSED_FLAG) != 0;
            bool hasHmac = (flags & HMAC_FLAG) != 0;
            bool hasSign = (flags & SIGN_FLAG) != 0;
            bool requiresAck = (flags & ACK_FLAG) != 0;

            try
            {
                ulong msgId = ReadU64(msHeader);
                ulong seq = ReadU64(msHeader);
                int total = ReadI32(msHeader);
                int index = ReadI32(msHeader);

                //Net.Logger.LogInfo($"ProcessIncomingFrame: header flags=0x{flags:X2} msgId={msgId} seq={seq} total={total} idx={index}");

                int remainingHeader = (int)(msHeader.Length - msHeader.Position);
                if (remainingHeader <= 0)
                {
                    //Net.Logger.LogWarning("ProcessIncomingFrame: Empty payload received (remainingHeader<=0)");
                    return;
                }

                var payloadFragment = new byte[remainingHeader];
                msHeader.Read(payloadFragment, 0, remainingHeader);

                byte[] assembledPayload;
                if (total > 1)
                {
                    var key = (sender.m_SteamID, msgId);
                    FragmentBuffer fb;
                    lock (fragmentLock)
                    {
                        if (!fragmentBuffers.TryGetValue(key, out fb))
                        {
                            fb = new FragmentBuffer { Total = total, FirstSeen = DateTime.UtcNow };
                            fragmentBuffers[key] = fb;
                        }

                        fb.Fragments[index] = payloadFragment;
                        //Net.Logger.LogInfo($"ProcessIncomingFrame: stored fragment {index}/{total - 1} for key {sender}:{msgId} (fragments={fb.Fragments.Count})");

                        var stale = fragmentBuffers.Where(kv => DateTime.UtcNow - kv.Value.FirstSeen > FragmentTimeout)
                                                  .Select(kv => kv.Key).ToList();
                        foreach (var k in stale)
                        {
                            //Net.Logger.LogWarning($"ProcessIncomingFrame: removing stale fragment buffer for key {k}");
                            fragmentBuffers.Remove(k);
                        }

                        if (fb.Fragments.Count != fb.Total)
                        {
                            //Net.Logger.LogInfo($"ProcessIncomingFrame: waiting for more fragments ({fb.Fragments.Count}/{fb.Total})");
                            return;
                        }

                        using var outMs = new MemoryStream();
                        for (int i = 0; i < fb.Total; i++)
                        {
                            if (!fb.Fragments.TryGetValue(i, out var part))
                            {
                                //Net.Logger.LogWarning($"ProcessIncomingFrame: missing fragment {i}; discarding buffer for key {key}");
                                fragmentBuffers.Remove(key);
                                return;
                            }
                            outMs.Write(part, 0, part.Length);
                        }
                        assembledPayload = outMs.ToArray();
                        fragmentBuffers.Remove(key);
                        //Net.Logger.LogInfo($"ProcessIncomingFrame: reassembled payload len={assembledPayload.Length} for key {sender}:{msgId}");
                    }
                }
                else
                {
                    assembledPayload = payloadFragment;
                }

                byte[] payloadWithOptionalMacAndSig = assembledPayload;
                byte[] payloadToProcess = payloadWithOptionalMacAndSig;

                if (hasHmac)
                {
                    if (payloadWithOptionalMacAndSig.Length < 32)
                    {
                        //Net.Logger.LogWarning("ProcessIncomingFrame: Invalid HMAC payload (too short)");
                        return;
                    }

                    int dataLen = payloadWithOptionalMacAndSig.Length - 32;
                    var dataOnlyForMacCheck = new byte[dataLen];
                    Array.Copy(payloadWithOptionalMacAndSig, 0, dataOnlyForMacCheck, 0, dataLen);
                    var receivedMac = new byte[32];
                    Array.Copy(payloadWithOptionalMacAndSig, dataLen, receivedMac, 0, 32);

                    if (perPeerSymmetricKey.TryGetValue(sender.m_SteamID, out var peerSym))
                    {
                        using var h = new HMACSHA256(peerSym);
                        var computed = h.ComputeHash(dataOnlyForMacCheck);
                        if (!computed.SequenceEqual(receivedMac))
                        {
                            //Net.Logger.LogWarning("ProcessIncomingFrame: Per-peer HMAC mismatch; dropping");
                            return;
                        }
                        payloadToProcess = dataOnlyForMacCheck;
                    }
                    else if (globalHmac != null)
                    {
                        var computed = globalHmac!.ComputeHash(dataOnlyForMacCheck);
                        if (!computed.SequenceEqual(receivedMac))
                        {
                            //Net.Logger.LogWarning("ProcessIncomingFrame: Global HMAC mismatch; dropping");
                            return;
                        }
                        payloadToProcess = dataOnlyForMacCheck;
                    }
                    else
                    {
                        //Net.Logger.LogWarning("ProcessIncomingFrame: HMAC flag present but no key available; dropping");
                        return;
                    }
                }

                if (hasSign)
                {
                    if (payloadToProcess.Length < 5)
                    {
                        //Net.Logger.LogWarning("ProcessIncomingFrame: Signed payload too small");
                        return;
                    }

                    if (payloadToProcess.Length < 1 + 4)
                    {
                        //Net.Logger.LogWarning("ProcessIncomingFrame: Signed payload too small to contain ModID");
                        return;
                    }

                    uint modId = BitConverter.ToUInt32(payloadToProcess, 1);
                    if (!modPublicKeys.TryGetValue(modId, out var rsaParams))
                    {
                        //Net.Logger.LogWarning($"ProcessIncomingFrame: No public key registered for mod {modId}; dropping signed msg");
                        return;
                    }

                    int expectedSigLen = rsaParams.Modulus?.Length ?? 0;
                    if (expectedSigLen <= 0 || payloadToProcess.Length < expectedSigLen + 2)
                    {
                        //Net.Logger.LogWarning("ProcessIncomingFrame: Signed payload too small for expected signature length");
                        return;
                    }

                    int sigSectionStart = payloadToProcess.Length - expectedSigLen - 2;
                    if (sigSectionStart < 0)
                    {
                        //Net.Logger.LogWarning("ProcessIncomingFrame: Signature section invalid");
                        return;
                    }

                    ushort declaredLen = BitConverter.ToUInt16(payloadToProcess, sigSectionStart);
                    if (declaredLen != expectedSigLen)
                    {
                        //Net.Logger.LogWarning($"ProcessIncomingFrame: Signature length mismatch (declared={declaredLen}, expected={expectedSigLen}); dropping");
                        return;
                    }

                    var signature = new byte[expectedSigLen];
                    Array.Copy(payloadToProcess, sigSectionStart + 2, signature, 0, expectedSigLen);
                    var dataOnly = new byte[sigSectionStart];
                    Array.Copy(payloadToProcess, 0, dataOnly, 0, sigSectionStart);

                    try
                    {
                        using var rsa = new RSACryptoServiceProvider();
                        rsa.ImportParameters(rsaParams);
                        var ok = rsa.VerifyData(dataOnly, CryptoConfig.MapNameToOID("SHA256"), signature);
                        if (!ok)
                        {
                            //Net.Logger.LogWarning("ProcessIncomingFrame: Signature verification failed; dropping");
                            return;
                        }
                        payloadToProcess = dataOnly;
                    }
                    catch (Exception ex)
                    {
                        Net.Logger.LogError($"ProcessIncomingFrame: Signature verification error: {ex}");
                        return;
                    }
                }

                if (compressed)
                {
                    try
                    {
                        payloadToProcess = Message.DecompressPayload(payloadToProcess);
                    }
                    catch (Exception ex)
                    {
                        Net.Logger.LogError($"ProcessIncomingFrame: Decompression failed: {ex}");
                        return;
                    }
                }

                var message = new Message(payloadToProcess);

                if (message.ModID == 0)
                {
                    //Net.Logger.LogInfo($"ProcessIncomingFrame: internal message {message.MethodName}");
                    HandleInternalMessage(message, sender, msgId, seq, requiresAck);
                    if (requiresAck)
                    {
                        //Net.Logger.LogInfo($"ProcessIncomingFrame: sending ACK for msgId={msgId} to {sender}");
                        SendAckToSender(sender, msgId);
                    }
                    return;
                }

                var sender64 = sender.m_SteamID;
                var rl = GetOrCreateRateLimiter(sender64);
                if (!rl.IncomingAllowed())
                {
                    //Net.Logger.LogWarning($"ProcessIncomingFrame: Rate limit: dropping incoming from {sender64}");
                    return;
                }

                if (!CheckAndUpdateSequence(sender64, message.ModID, seq))
                {
                    //Net.Logger.LogDebug($"ProcessIncomingFrame: Dropped replay/out-of-order seq {seq} from {sender64} for mod {message.ModID}");
                    return;
                }

                if (requiresAck)
                {
                    //Net.Logger.LogInfo($"ProcessIncomingFrame: will send ACK for msgId={msgId} to {sender}");
                    SendAckToSender(sender, msgId);
                }

                if (IncomingValidator != null && !IncomingValidator(message, sender64))
                {
                    //Net.Logger.LogDebug($"ProcessIncomingFrame: Incoming message from {sender64} rejected by validator");
                    return;
                }

                DispatchIncoming(message, sender);
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"ProcessIncomingFrame top-level exception: {ex}");
            }
        }
        private static ulong ReadU64(Stream s) { var b = new byte[8]; s.Read(b, 0, 8); return BitConverter.ToUInt64(b, 0); }
        private static int ReadI32(Stream s) { var b = new byte[4]; s.Read(b, 0, 4); return BitConverter.ToInt32(b, 0); }


        void SendAckToSender(CSteamID sender, ulong msgId)
        {
            //Net.Logger.LogInfo($"SendAckToSender: to={sender} ackId={msgId}");
            var ackMsg = new Message(0u, "NETWORK_INTERNAL_ACK", 0);
            ackMsg.WriteULong(msgId);
            var framed = BuildFramedBytesWithMeta(ackMsg, 0, ReliableType.Reliable);
            SendBytes(framed, sender, ReliableType.Reliable);

            var key = (sender.m_SteamID, msgId);
            lock (unackedLock)
            {
                if (unacked.ContainsKey(key)) unacked.Remove(key);
            }
        }

        void HandleInternalMessage(Message message, CSteamID sender, ulong msgId, ulong seq, bool requiresAck)
        {
            try
            {
                switch (message.MethodName)
                {
                    case "NETWORK_INTERNAL_HANDSHAKE_PUBKEY":
                        {
                            string pub = message.ReadString();
                            string nonce = message.ReadString();
                            StartHandshakeReply(sender, pub, nonce);
                            break;
                        }
                    case "NETWORK_INTERNAL_HANDSHAKE_SECRET":
                        {
                            var enc = (byte[])message.ReadObject(typeof(byte[]));
                            string initiator = message.ReadString();
                            CompleteHandshakeReceiver(sender, enc, initiator);
                            break;
                        }
                    case "NETWORK_INTERNAL_HANDSHAKE_CONFIRM":
                        {
                            string initiator = message.ReadString();
                            var confirm = (byte[])message.ReadObject(typeof(byte[]));
                            CompleteHandshakeInitiator(sender, initiator, confirm);
                            break;
                        }
                    case "NETWORK_INTERNAL_ACK":
                        {
                            ulong ackId = message.ReadULong();
                            var key = (sender.m_SteamID, ackId);
                            lock (unackedLock) { if (unacked.ContainsKey(key)) unacked.Remove(key); }
                            break;
                        }
                    default:
                        Net.Logger.LogWarning($"Unknown internal method {message.MethodName}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"HandleInternalMessage error: {ex}");
            }
        }

        bool CheckAndUpdateSequence(ulong sender64, uint modId, ulong seq)
        {
            lock (lastSeenSequence)
            {
                if (!lastSeenSequence.TryGetValue(sender64, out var dict)) dict = lastSeenSequence[sender64] = new Dictionary<uint, ulong>();
                if (!dict.TryGetValue(modId, out var last)) last = 0;
                if (seq <= last) return false;
                dict[modId] = seq;
                return true;
            }
        }

        void DispatchIncoming(Message message, CSteamID sender)
        {
            if (!rpcs.TryGetValue(message.ModID, out var methods))
            {
                Net.Logger.LogWarning($"Dropping message for unknown mod {message.ModID}");
                return;
            }

            if (!methods.TryGetValue(message.MethodName, out var handlers))
            {
                Net.Logger.LogWarning($"Dropping message for method {message.MethodName} not registered for {message.ModID}");
                return;
            }

            bool invoked = false;
            var candidates = handlers.Where(h => h.Mask == message.Mask).ToList();
            foreach (var handler in candidates)
            {
                var msgCopy = new Message(message.ToArray());
                try
                {
                    var paramInfos = handler.Parameters;
                    int paramCount = handler.TakesInfo ? paramInfos.Length - 1 : paramInfos.Length;
                    var callParams = new object[paramInfos.Length];

                    for (int i = 0; i < paramCount; i++)
                    {
                        var t = paramInfos[i].ParameterType;
                        callParams[i] = msgCopy.ReadObject(t);
                    }

                    if (handler.TakesInfo)
                    {
                        var infoType = paramInfos[paramInfos.Length - 1].ParameterType;
                        callParams[paramInfos.Length - 1] = CreateRpcInfoInstance(infoType, sender);
                    }

                    handler.Method.Invoke(handler.Target, callParams);
                    invoked = true;
                    break;
                }
                catch
                {
                    continue;
                }
            }

            if (!invoked)
            {
                Net.Logger.LogWarning($"No handler matched for {message.ModID}:{message.MethodName}");
            }

            if (!invoked)
                Net.Logger.LogWarning($"No handler matched for {message.ModID}:{message.MethodName}");
        }

        object CreateRpcInfoInstance(Type infoType, CSteamID sender)
        {
            try
            {
                var ctorFull = infoType.GetConstructor(new Type[] { typeof(ulong), typeof(string), typeof(bool) });
                if (ctorFull != null)
                {
                    bool isLocal = false;
                    try { isLocal = (sender == SteamUser.GetSteamID()); } catch { isLocal = false; }
                    return ctorFull.Invoke(new object[] { sender.m_SteamID, sender.ToString(), isLocal });
                }

                var ci = infoType.GetConstructor(new Type[] { typeof(CSteamID) });
                if (ci != null) return ci.Invoke(new object[] { sender });

                var ci2 = infoType.GetConstructor(new Type[] { typeof(ulong) });
                if (ci2 != null) return ci2.Invoke(new object[] { sender.m_SteamID });

                var paramless = infoType.GetConstructor(Type.EmptyTypes);
                if (paramless != null)
                {
                    var obj = paramless.Invoke(null);
                    var f = infoType.GetField("SenderSteamID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null) f.SetValue(obj, sender);
                    var f2 = infoType.GetField("Sender", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f2 != null) f2.SetValue(obj, sender);
                    return obj;
                }
            }
            catch { }
            return null!;
        }

        class HandshakeState { public string? PeerPub; public string LocalNonce = string.Empty; public byte[]? Sym; public bool Completed = false; }

        /// <summary>
        /// </summary>
        public void StartHandshake(CSteamID target)
        {
            if (LocalRsa == null) return;
            var pub = SerializeRsaPublicKey(LocalRsa);
            var rng = RandomNumberGenerator.Create();
            var nonceBytes = new byte[16]; rng.GetBytes(nonceBytes);
            var nonce = Convert.ToBase64String(nonceBytes);

            handshakeStates[target.m_SteamID] = new HandshakeState { PeerPub = null, LocalNonce = nonce, Completed = false };

            var m = new Message(0u, "NETWORK_INTERNAL_HANDSHAKE_PUBKEY", 0);
            m.WriteString(pub);
            m.WriteString(nonce);
            var framed = BuildFramedBytesWithMeta(m, 0, ReliableType.Reliable);
            SendBytes(framed, target, ReliableType.Reliable);
        }

        void StartHandshakeReply(CSteamID sender, string peerPubKeySerialized, string peerNonce)
        {
            if (LocalRsa == null) return;
            var state = handshakeStates.ContainsKey(sender.m_SteamID) ? handshakeStates[sender.m_SteamID] : new HandshakeState();
            state.PeerPub = peerPubKeySerialized;
            if (string.IsNullOrEmpty(state.LocalNonce))
            {
                var rng = RandomNumberGenerator.Create();
                var localNonceBytes = new byte[16]; rng.GetBytes(localNonceBytes);
                state.LocalNonce = Convert.ToBase64String(localNonceBytes);
                handshakeStates[sender.m_SteamID] = state;

                var myPub = SerializeRsaPublicKey(LocalRsa);
                var m = new Message(0u, "NETWORK_INTERNAL_HANDSHAKE_PUBKEY", 0);
                m.WriteString(myPub);
                m.WriteString(state.LocalNonce);
                var framed = BuildFramedBytesWithMeta(m, 0, ReliableType.Reliable);
                SendBytes(framed, sender, ReliableType.Reliable);
                return;
            }

            if (!state.Completed && !string.IsNullOrEmpty(state.LocalNonce) && !string.IsNullOrEmpty(state.PeerPub))
            {
                var rng = RandomNumberGenerator.Create();
                var sym = new byte[32]; rng.GetBytes(sym);

                var rsaPeer = new RSACryptoServiceProvider();
                var rsaParams = DeserializeRsaPublicKey(state.PeerPub!);
                rsaPeer.ImportParameters(rsaParams);
                var enc = rsaPeer.Encrypt(sym, false);

                var m2 = new Message(0u, "NETWORK_INTERNAL_HANDSHAKE_SECRET", 0);
                m2.WriteBytes(enc);
                m2.WriteString(state.LocalNonce);
                var framed2 = BuildFramedBytesWithMeta(m2, 0, ReliableType.Reliable);
                SendBytes(framed2, sender, ReliableType.Reliable);

                state.Sym = sym; state.Completed = true;
                handshakeStates[sender.m_SteamID] = state;
                perPeerSymmetricKey[sender.m_SteamID] = sym;
            }
        }

        void CompleteHandshakeReceiver(CSteamID sender, byte[] encSecret, string initiatorNonce)
        {
            if (LocalRsa == null) return;
            try
            {
                var sym = LocalRsa.Decrypt(encSecret, false);
                var state = handshakeStates.ContainsKey(sender.m_SteamID) ? handshakeStates[sender.m_SteamID] : new HandshakeState();
                state.Sym = sym; state.Completed = true; handshakeStates[sender.m_SteamID] = state;
                perPeerSymmetricKey[sender.m_SteamID] = sym;

                var confirm = HmacSha256Raw(sym, Encoding.UTF8.GetBytes(initiatorNonce));
                var m = new Message(0u, "NETWORK_INTERNAL_HANDSHAKE_CONFIRM", 0);
                m.WriteString(initiatorNonce);
                m.WriteBytes(confirm);
                var framed = BuildFramedBytesWithMeta(m, 0, ReliableType.Reliable);
                SendBytes(framed, sender, ReliableType.Reliable);
            }
            catch (Exception ex) { Net.Logger.LogError($"Handshake decryption error: {ex}"); }
        }

        void CompleteHandshakeInitiator(CSteamID sender, string initiatorNonce, byte[] confirmHmac)
        {
            if (!handshakeStates.TryGetValue(sender.m_SteamID, out var state) || state.Sym == null)
            {
                Net.Logger.LogWarning("Handshake confirm received but no state");
                return;
            }

            var expected = HmacSha256Raw(state.Sym, Encoding.UTF8.GetBytes(initiatorNonce));
            if (!expected.SequenceEqual(confirmHmac))
            {
                Net.Logger.LogWarning("Handshake confirm HMAC mismatch");
                return;
            }

            state.Completed = true;
            handshakeStates[sender.m_SteamID] = state;
            perPeerSymmetricKey[sender.m_SteamID] = state.Sym!;
        }

        static byte[] HmacSha256Raw(byte[] key, byte[] payload)
        {
            using var h = new HMACSHA256(key);
            return h.ComputeHash(payload);
        }

        static string SerializeRsaPublicKey(RSACryptoServiceProvider rsa)
        {
            var parms = rsa.ExportParameters(false);
            var mod = Convert.ToBase64String(parms.Modulus ?? Array.Empty<byte>());
            var exp = Convert.ToBase64String(parms.Exponent ?? Array.Empty<byte>());
            return $"{mod}:{exp}";
        }

        static RSAParameters DeserializeRsaPublicKey(string s)
        {
            var parts = s.Split(':');
            var mod = Convert.FromBase64String(parts[0]);
            var exp = Convert.FromBase64String(parts[1]);
            return new RSAParameters { Modulus = mod, Exponent = exp };
        }

        /// <summary>
        /// </summary>
        public void SetSharedSecret(byte[]? secret)
        {
            if (secret == null) { globalSharedSecret = null; globalHmac?.Dispose(); globalHmac = null; return; }
            globalSharedSecret = (byte[])secret.Clone();
            globalHmac?.Dispose();
            globalHmac = new HMACSHA256(globalSharedSecret);
        }

        /// <summary>
        /// </summary>
        public void RegisterModSigner(uint modId, Func<byte[], byte[]> signerDelegate) => modSigners[modId] = signerDelegate;
        /// <summary>
        /// </summary>
        public void RegisterModPublicKey(uint modId, RSAParameters pub) => modPublicKeys[modId] = pub;

        void InvokeLocalMessage(Message message, CSteamID localSender)
        {
            ulong id = localSender.m_SteamID;
            if (IncomingValidator != null && !IncomingValidator(message, id)) return;

            if (!rpcs.TryGetValue(message.ModID, out var methods)) return;
            if (!methods.TryGetValue(message.MethodName, out var handlers)) return;

            foreach (var handler in handlers)
            {
                if (handler.Mask != message.Mask) continue;
                var paramInfos = handler.Parameters;
                int paramCount = handler.TakesInfo ? paramInfos.Length - 1 : paramInfos.Length;
                var callParams = new object[paramInfos.Length];

                for (int i = 0; i < paramCount; i++)
                    callParams[i] = message.ReadObject(paramInfos[i].ParameterType);

                if (handler.TakesInfo)
                {
                    var infoType = paramInfos[paramInfos.Length - 1].ParameterType;
                    callParams[paramInfos.Length - 1] = CreateRpcInfoInstance(infoType, localSender);
                }

                try { handler.Method.Invoke(handler.Target, callParams); }
                catch (Exception ex) { Net.Logger.LogError($"Local invoke error: {ex}"); }
            }
        }

        Message? BuildMessage(uint modId, string methodName, int mask, object[] parameters)
        {
            try
            {
                var msg = new Message(modId, methodName, mask);

                if (rpcs.TryGetValue(modId, out var methods) && methods.TryGetValue(methodName, out var handlers) && handlers.Count > 0)
                {
                    MessageHandler chosen = null!;
                    foreach (var h in handlers)
                    {
                        if (h.Mask != mask) continue;
                        var expected = h.Parameters;
                        int expectedCount = h.TakesInfo ? expected.Length - 1 : expected.Length;
                        if (expectedCount != parameters.Length) continue;

                        bool ok = true;
                        for (int i = 0; i < expectedCount; i++)
                        {
                            if (!expected[i].ParameterType.IsAssignableFrom(parameters[i].GetType())) { ok = false; break; }
                        }
                        if (ok) { chosen = h; break; }
                    }

                    if (chosen == null)
                    {
                        chosen = handlers.FirstOrDefault(h =>
                        {
                            int expectedCount = h.TakesInfo ? h.Parameters.Length - 1 : h.Parameters.Length;
                            return expectedCount == parameters.Length && h.Mask == mask;
                        }) ?? handlers[0];
                    }

                    var expectedParams = chosen.Parameters;
                    int expectedCountFinal = chosen.TakesInfo ? expectedParams.Length - 1 : expectedParams.Length;
                    for (int i = 0; i < expectedCountFinal; i++)
                    {
                        var t = expectedParams[i].ParameterType;
                        var p = parameters[i] ?? throw new Exception($"Null parameter {i}");
                        if (!t.IsAssignableFrom(p.GetType()))
                            throw new Exception($"Parameter {i} type mismatch: expected {t}, got {p.GetType()}");
                        msg.WriteObject(t, p);
                    }
                }

                if (msg.Length() > Message.MaxSize * 16)
                {
                    Net.Logger.LogError("Message exceeds maximum allowed overall size.");
                    return null;
                }

                return msg;
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"BuildMessage failed: {ex}");
                return null;
            }
        }

        SlidingWindowRateLimiter GetOrCreateRateLimiter(ulong steam64)
        {
            lock (rateLimiters)
            {
                if (!rateLimiters.TryGetValue(steam64, out var rl))
                {
                    rl = new SlidingWindowRateLimiter(100, TimeSpan.FromSeconds(1));
                    rateLimiters[steam64] = rl;
                }
                return rl;
            }
        }

        class SlidingWindowRateLimiter
        {
            readonly int limit;
            readonly TimeSpan window;
            readonly Queue<DateTime> q = new();
            public SlidingWindowRateLimiter(int limit, TimeSpan window) { this.limit = limit; this.window = window; }
            public bool Allowed()
            {
                var now = DateTime.UtcNow;
                while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
                if (q.Count >= limit) return false;
                q.Enqueue(now);
                return true;
            }
            public bool IncomingAllowed() => Allowed();
        }

        enum Priority { High = 0, Normal = 1, Low = 2 }
        class QueuedSend { public byte[] Framed = null!; public CSteamID Target; public ReliableType Reliable; public DateTime Enqueued; }
        class UnackedMessage { public byte[] Framed = null!; public CSteamID Target; public ReliableType Reliable; public DateTime LastSent; public int Attempts; public ulong msgId => BitConverter.ToUInt64(Framed, 1); }
        class MessageHandler { public object Target = null!; public MethodInfo Method = null!; public ParameterInfo[] Parameters = null!; public bool TakesInfo; public int Mask; }

        static byte[] HmacSha256RawStatic(byte[] key, byte[] payload) { using var h = new HMACSHA256(key); return h.ComputeHash(payload); }
    }
}
#endif
