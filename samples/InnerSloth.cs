// Each instance of Client class is equivalent to a Lobby going to be joined and Ducked.

using Hazel;
using Hazel.Dtls;
using Hazel.Udp;
using Impostor.Api.Games;
using Impostor.Api.Innersloth;
using Impostor.Api.Net.Inner;
using Impostor.Api.Net.Messages;
using MatchDucking.UdpConnection;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace MatchDucking.InnerSloth;

public class Client
{
    public enum ClientState
    {
        Idle,
        Connecting,
        Connected,
        Failed,
        Banned,
        Completed,
        GameDeleted,
        JoinFailed
    }

    public ClientState State { get; private set; } = ClientState.Idle;
    public event EventHandler<ClientState>? StateChanged;
    public event EventHandler<GameCode>? GameShouldBeRemoved;
    public static event EventHandler<GameCode>? GameJoinFailed;
    public event EventHandler<GameCode>? OnBecomeHost;
    public string AssociatedMmToken { get; private set; }
    public DateTime StartTime { get; private set; }
    public DateTime? EndTime { get; private set; }
    public int Id { get; private set; }
    public string Name { get; private set; }
    public string MmToken { get; private set; }
    public IPEndPoint EndPoint { get; private set; }
    public GameVersion Version { get; private set; }
    public Platforms Platform { get; private set; }
    public string FriendCode { get; private set; }
    public string Puid { get; private set; }
    public GameCode GameCode { get; private set; }
    public PlatformSpecificData PlatformSpecificData { get; private set; }
    public DtlsUnityConnection? DtlsConnection { get; private set; }
    private int ClientId { get; set; } = -1;
    private int HostId { get; set; } = -1;
    public uint VoteBanNetId { get; set; } = 0;
    public uint PlayerNetId { get; set; } = 0;
    public uint PlayerPhysicsNetId { get; set; } = 0;
    private List<int> AllClients { get; set; } = [];
    private long ticks = 0;
    private long votebantick = -1;
    private bool setname = false;
    private bool setcolor = false;
    private bool sendchat = false;
    private bool _hasTriggeredJoinFailed = false;
    private bool _hasTriggeredBecomeHost = false;

    public Client(int id, string name, string mmtoken, IPEndPoint endPoint, GameVersion version, Platforms platform, GameCode code, uint voteban)
    {
        Id = id;
        Name = name;
        MmToken = mmtoken;
        AssociatedMmToken = mmtoken;
        EndPoint = endPoint;
        Version = version;
        Platform = platform;

        FriendCode = Getfriendcode(mmtoken);
        Puid = GetPuid(mmtoken);

        PlatformSpecificData = GeneratePlatformSpecificData(platform);

        GameCode = code;
        VoteBanNetId = voteban;
    }

    public async ValueTask<bool> StartAsync()
    {
        StartTime = DateTime.Now;
        UpdateState(ClientState.Connecting);

        try
        {
            var connection = HazelConnection.CreateDtlsConnection(EndPoint);

            connection.KeepAliveInterval = 1000;
            connection.DisconnectTimeoutMs = 7500;
            connection.ResendPingMultiplier = 1.2f;

            connection.DataReceived += OnDataReceived;
            connection.Disconnected += OnDisconnected;

            DtlsConnection = connection;

            connection.ConnectAsync(HazelConnection.GetConnectionData(Name, MmToken, null, Version, PlatformSpecificData, FriendCode));

            while (DtlsConnection != null && DtlsConnection.State == ConnectionState.Connecting)
            {
                await Task.Delay(33);
            }

            if (DtlsConnection == null)
            {
                UpdateState(ClientState.JoinFailed);
                Console.WriteLine($"Client {Id} dtls connection was disposed during handshake.");
                return false;
            }

            if (DtlsConnection.State != ConnectionState.Connected)
            {
                UpdateState(ClientState.JoinFailed);
                Console.WriteLine($"Client {Id} dtls connection failed to connect.");
                return false;
            }

            UpdateState(ClientState.Connected);
            await OnConnected();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Client {Id} connection error: {ex.Message}");
            UpdateState(ClientState.Failed);
        }
        return false;
    }

    public void FixedUpdate()
    {
        ticks++;

        DtlsConnection?.FixedUpdate();

        if (ticks > 330)
        {
            UpdateState(ClientState.Failed);
            ExitGame();
        }
        else if (ticks - votebantick > 30 && votebantick > 0)
        {
            UpdateState(ClientState.Completed);
            ExitGame();
        }
    }

    private void UpdateState(ClientState newState)
    {
        if (State != newState)
        {
            State = newState;
            if (newState == ClientState.Completed || newState == ClientState.Banned || newState == ClientState.Failed)
            {
                EndTime = DateTime.Now;
            }
            if (newState == ClientState.JoinFailed && !_hasTriggeredJoinFailed)
            {
                _hasTriggeredJoinFailed = true;
                GameJoinFailed?.Invoke(this, GameCode);
            }
            StateChanged?.Invoke(this, newState);
        }
    }

    private async ValueTask<bool> OnConnected()
    {
        Console.WriteLine($"Client {Id} connected to server {EndPoint.ToString()}");
        MessageApi.SendJoinGame(DtlsConnection!, GameCode);

        if (ticks > 100)
        {
            ticks -= 100;
        }
        while (ClientId < 0 && DtlsConnection != null && DtlsConnection.State == ConnectionState.Connected)
        {
            await Task.Delay(33);
        }

        if (ClientId > 0)
        {
            OnGameJoined();
            return true;
        }
        else
        {
            Console.WriteLine($"Client {Id} failed to join game.");
            DtlsConnection?.Dispose();
            DtlsConnection = null;
            UpdateState(ClientState.JoinFailed);
            return false;
        }
    }

    private void OnGameJoined()
    {
        Console.WriteLine($"Client {Id} joined game with ID {ClientId}, sending scene change.");
        MessageApi.SendSceneChange(DtlsConnection!, GameCode, "OnlineGame", ClientId);

        if (ticks > 150)
        {
            ticks -= 150;
        }
    }


    private void OnDataReceived(Hazel.DataReceivedEventArgs e)
    {
        MessageReader message = e.Message;
        try
        {
            while (message.Position < message.Length)
            {
                HandleMessage(message.ReadMessage(), e.SendOption);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Client {Id} error while handling message: {ex.Message}");
        }
        finally
        {
            message.Recycle();
        }
    }

    private void OnDisconnected(object? sender, DisconnectedEventArgs e)
    {
        MessageReader message = e.Message;
        if (message != null && message.Position < message.Length)
        {
            if (message.ReadByte() == 1)
            {
                MessageReader messageReader = message.ReadMessage();
                DisconnectReason reason = (DisconnectReason)messageReader.ReadByte();
                Console.WriteLine($"Client {Id} disconnected by server {EndPoint} for reason: {reason}");

                if (reason is DisconnectReason.Banned)
                {
                    OnBanReceived();
                }

                if (reason is DisconnectReason.GameNotFound or DisconnectReason.GameStarted)
                {
                    OnGameDeleted();
                }

                if (reason is DisconnectReason.Error or DisconnectReason.GameFull)
                {
                    UpdateState(ClientState.JoinFailed);
                }
            }
            else
            {
                Console.WriteLine($"Client {Id} disconnected by server {EndPoint} for Event: {e.Reason}");
            }
        }
        else
        {
            Console.WriteLine($"Client {Id} disconnected by self error from {EndPoint} for {e.Reason}");
        }

        DtlsConnection?.Dispose();
        DtlsConnection = null;
    }

    private ValueTask HandleMessage(MessageReader reader, SendOption sendOption)
    {
        switch (reader.Tag)
        {
            case MessageFlags.JoinGame:
                reader.ReadInt32();
                var joined = reader.ReadInt32();
                HostId = reader.ReadInt32();

                reader.ReadString();
                reader.ReadMessage();
                reader.ReadPackedUInt32();
                reader.ReadString();

                var friendcode = reader.ReadString();

                if (!AllClients.Contains(joined) && !MessageApi.isFriendcodeADuck(friendcode))
                {
                    AllClients.Add(joined);
                }

                if (HostId == ClientId && ClientId > 0)
                {
                    if (!_hasTriggeredBecomeHost)
                    {
                        _hasTriggeredBecomeHost = true;
                        OnBecomeHost?.Invoke(this, GameCode);
                        votebantick = ticks;
                    }

                    var writer = MessageWriter.Get(SendOption.Reliable);
                    foreach (var clientId in AllClients)
                    {
                        if (clientId == ClientId)
                        {
                            continue;
                        }

                        writer.StartMessage(11);
                        writer.Write(GameCode.Value);
                        writer.WritePacked(clientId);
                        writer.Write(true);
                        writer.EndMessage();
                    }

                    DtlsConnection?.Send(writer);
                    writer.Recycle();
                }
                else if (VoteBanNetId > 0 && PlayerNetId > 0)
                {
                    var writer = MessageWriter.Get(SendOption.Reliable);
                    writer.StartMessage(5);
                    writer.Write(GameCode.Value);
                    writer.StartMessage(2);
                    writer.WritePacked(VoteBanNetId);
                    writer.Write((byte)RpcCalls.AddVote);
                    writer.Write(ClientId);
                    writer.Write(joined);
                    writer.EndMessage();
                    writer.EndMessage();
                    DtlsConnection?.Send(writer);
                    writer.Recycle();
                }

                break;

            case MessageFlags.JoinedGame:
                reader.ReadInt32();
                ClientId = reader.ReadInt32();
                HostId = reader.ReadInt32();

                if (HostId == ClientId && ClientId > 0)
                {
                    if (!_hasTriggeredBecomeHost)
                    {
                        _hasTriggeredBecomeHost = true;
                        OnBecomeHost?.Invoke(this, GameCode);
                        votebantick = ticks;
                    }
                }

                int allclientsCount = reader.ReadPackedInt32();
                for (int i = 0; i < allclientsCount; i++)
                {
                    int id = reader.ReadPackedInt32();
                    reader.ReadString();
                    reader.ReadMessage();
                    reader.ReadPackedUInt32();
                    reader.ReadString();
                    var friendcode2 = reader.ReadString();

                    if (id == ClientId || MessageApi.isFriendcodeADuck(friendcode2))
                    {
                        continue;
                    }
                    AllClients.Add(id);
                }
                Console.WriteLine($"Client {Id} received joined game {GameCode} with client ID {ClientId}, host ID {HostId}, playercount {allclientsCount}");
                if (ticks > 150)
                {
                    ticks -= 150;
                }
                break;


            case MessageFlags.KickPlayer:
                reader.ReadInt32();
                int targetId = reader.ReadPackedInt32();
                bool ban = reader.ReadBoolean();

                if (targetId == ClientId)
                {
                    Console.WriteLine($"Client {Id} received kickplayer ban:{ban} from host {HostId} in {GameCode} {EndPoint}");

                    if (ban)
                    {
                        OnBanReceived();
                    }
                }
                break;

            case MessageFlags.RemovePlayer:
                reader.ReadInt32();
                reader.ReadInt32();
                HostId = reader.ReadInt32();

                if (HostId == ClientId && ClientId > 0)
                {
                    if (!_hasTriggeredBecomeHost)
                    {
                        _hasTriggeredBecomeHost = true;
                        OnBecomeHost?.Invoke(this, GameCode);
                        votebantick = ticks;
                    }

                    var writer = MessageWriter.Get(SendOption.Reliable);
                    foreach (var clientId in AllClients)
                    {
                        if (clientId == ClientId)
                        {
                            continue;
                        }

                        writer.StartMessage(11);
                        writer.Write(GameCode.Value);
                        writer.WritePacked(clientId);
                        writer.Write(true);
                        writer.EndMessage();
                    }

                    DtlsConnection?.Send(writer);
                    writer.Recycle();
                }
                break;

            case MessageFlags.GameData:
            case MessageFlags.GameDataTo:
                reader.ReadInt32();

                if (reader.Tag == MessageFlags.GameDataTo)
                {
                    reader.ReadPackedInt32();
                }

                var subreader = MessageReader.Get(reader);

                try
                {
                    while (subreader.Position < subreader.Length)
                    {
                        MessageReader realreader = subreader.ReadMessageAsNewBuffer();

                        realreader.Position = 0;

                        switch (realreader.Tag)
                        {
                            case 4:
                                {
                                    uint spawnId = realreader.ReadPackedUInt32();

                                    if (spawnId != 4 && spawnId != 12) break;

                                    var ownerId = realreader.ReadPackedInt32();

                                    if (spawnId == 4 && ownerId != ClientId) break;

                                    realreader.ReadByte();
                                    realreader.ReadPackedInt32();

                                    if (spawnId == 4)
                                    {
                                        PlayerNetId = realreader.ReadPackedUInt32();
                                        realreader.ReadMessage();
                                        PlayerPhysicsNetId = realreader.ReadPackedUInt32();

                                        if (PlayerPhysicsNetId - PlayerNetId > 3)
                                        {
                                            PlayerPhysicsNetId = 0;
                                        }

                                        OnPlayerReceived();
                                    }
                                    else if (spawnId == 12)
                                    {
                                        VoteBanNetId = realreader.ReadPackedUInt32();
                                    }

                                    break;
                                }

                            case 2:
                                {
                                    uint netid = realreader.ReadPackedUInt32();

                                    if (netid == PlayerNetId)
                                    {
                                        var call = realreader.ReadByte();

                                        if (call == (byte)RpcCalls.SetName)
                                        {
                                            setname = true;
                                        }
                                        else if (call == (byte)RpcCalls.SetColor)
                                        {
                                            setcolor = true;
                                        }

                                        OnSetNameColor();
                                    }

                                    break;
                                }
                        }

                        realreader.Recycle();
                    }
                }
                finally
                {
                    subreader.Recycle();
                }
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void OnPlayerReceived()
    {
        if (PlayerNetId > 0 && AllClients.Count >= 1)
        {
            var writer = MessageWriter.Get(SendOption.Reliable);

            writer.StartMessage(5);
            writer.Write(GameCode.Value);

            writer.StartMessage(2);
            writer.WritePacked(PlayerNetId);
            writer.Write((byte)RpcCalls.SetHatStr);
            writer.Write(MessageApi.GetRandomHat());
            writer.Write((byte)MessageApi.GetRandomLevel());
            writer.EndMessage();

            writer.StartMessage(2);
            writer.WritePacked(PlayerNetId);
            writer.Write((byte)RpcCalls.SetPetStr);
            writer.Write(MessageApi.GetRandomPet());
            writer.Write((byte)MessageApi.GetRandomLevel());
            writer.EndMessage();

            writer.StartMessage(2);
            writer.WritePacked(PlayerNetId);
            writer.Write((byte)RpcCalls.SetSkinStr);
            writer.Write(MessageApi.GetRandomSkin());
            writer.Write((byte)MessageApi.GetRandomLevel());
            writer.EndMessage();

            writer.StartMessage(2);
            writer.WritePacked(PlayerNetId);
            writer.Write((byte)RpcCalls.SetNamePlateStr);
            writer.Write(MessageApi.GetRandomNamePlate());
            writer.Write((byte)MessageApi.GetRandomLevel());
            writer.EndMessage();

            writer.StartMessage(2);
            writer.WritePacked(PlayerNetId);
            writer.Write((byte)RpcCalls.SetVisorStr);
            writer.Write(MessageApi.GetRandomVisor());
            writer.Write((byte)MessageApi.GetRandomLevel());
            writer.EndMessage();

            writer.StartMessage(2);
            writer.WritePacked(PlayerNetId);
            writer.Write((byte)RpcCalls.SetLevel);
            writer.WritePacked((uint)MessageApi.GetRandomLevel());
            writer.EndMessage();

            writer.EndMessage();
            /*
            Mass Report
            writer.StartMessage(5);
            writer.Write(GameCode.Value);

            foreach (var clientId in AllClients)
            {
                writer.StartMessage(2);
                writer.Write(VoteBanNetId);
                writer.Write((byte)RpcCalls.AddVote);
                writer.Write(ClientId);
                writer.Write(clientId);
                writer.EndMessage();
            }

            writer.EndMessage();
            */

            writer.StartMessage(6);
            writer.Write(GameCode.Value);
            writer.WritePacked(HostId);

            writer.StartMessage(2);
            writer.WritePacked(PlayerNetId);
            writer.Write((byte)RpcCalls.CheckName);
            writer.Write(Name);
            writer.EndMessage();

            writer.StartMessage(2);
            writer.WritePacked(PlayerNetId);
            writer.Write((byte)RpcCalls.CheckColor);
            writer.Write(MessageApi.GetRandomColor());
            writer.EndMessage();

            writer.EndMessage();
            DtlsConnection?.Send(writer);
            writer.Recycle();

            Console.WriteLine($"Client {Id} sent playerinfo for all clients");
        }
    }

    private void OnSetNameColor()
    {
        if (VoteBanNetId <= 0 || PlayerNetId <= 0) return;

        if (setname && setcolor && !sendchat)
        {
            sendchat = true;
            var writer = MessageWriter.Get(SendOption.Reliable);

            writer.StartMessage(5);
            writer.Write(GameCode.Value);

            if (PlayerPhysicsNetId > 0)
            {
                while (writer.Length < 800)
                {
                    writer.StartMessage(2);
                    writer.WritePacked(PlayerPhysicsNetId);
                    writer.Write((byte)RpcCalls.Pet);
                    writer.EndMessage();
                }
            }

            if (!MessageApi.isNewEndpoint(EndPoint))
            {
                foreach (var clientId in AllClients)
                {
                    writer.StartMessage(2);
                    writer.WritePacked(VoteBanNetId);
                    writer.Write((byte)RpcCalls.AddVote);
                    writer.Write(ClientId);
                    writer.Write(clientId);
                    writer.EndMessage();
                }
            }

            writer.EndMessage();
            DtlsConnection?.Send(writer);
            writer.Recycle();

            writer = MessageWriter.Get(SendOption.Reliable);

            writer.StartMessage(5);
            writer.Write(GameCode.Value);

            writer.StartMessage(2);
            writer.WritePacked(PlayerNetId);
            writer.Write((byte)RpcCalls.SendChat);
            writer.Write(MessageApi.GetDuckString());
            writer.EndMessage();

            while (writer.Length < 900)
            {
                writer.StartMessage(2);
                writer.WritePacked(PlayerNetId);
                writer.Write((byte)RpcCalls.ProtectPlayer);
                writer.EndMessage();
            }

            writer.EndMessage();

            DtlsConnection?.Send(writer);
            writer.Recycle();

            foreach (var clientId in AllClients)
            {
                writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(17);
                writer.Write(GameCode.Value);
                writer.WritePacked(clientId);
                writer.Write(MessageApi.GetRandomByte());
                writer.EndMessage();
                DtlsConnection?.Send(writer);
                writer.Recycle();
            }

            votebantick = ticks;
            Console.WriteLine($"Client {Id} sent vote ban for all clients");
        }
    }

    private void ExitGame()
    {
        DtlsConnection?.Dispose();
        DtlsConnection = null;
    }

    private void OnBanReceived()
    {
        Console.WriteLine($"Client {Id} was banned from {GameCode} {EndPoint}");

        /*
        if (!MessageApi.isNewEndpoint(EndPoint))
        {
            MessageApi.AddNewEndpoint(EndPoint);
        }
        */

        DtlsConnection?.Dispose();
        DtlsConnection = null;
        UpdateState(ClientState.Banned);
    }

    private void OnGameDeleted()
    {
        GameShouldBeRemoved?.Invoke(this, GameCode);

        DtlsConnection?.Dispose();
        DtlsConnection = null;
        UpdateState(ClientState.GameDeleted);
    }

    public static string Getfriendcode(string mmtoken)
    {
        var decodedToken = Encoding.UTF8.GetString(Convert.FromBase64String(mmtoken));
        var tokenParts = decodedToken.Split(' ');
        return tokenParts.Length > 1 ? tokenParts[1] : string.Empty;
    }

    public static string GetPuid(string mmtoken)
    {
        var decodedToken = Encoding.UTF8.GetString(Convert.FromBase64String(mmtoken));
        var tokenParts = decodedToken.Split(' ');
        return tokenParts.Length > 0 ? tokenParts[0] : string.Empty;
    }

    private PlatformSpecificData GeneratePlatformSpecificData(Platforms platform)
    {
        return new PlatformSpecificData(platform, "");
    }
}

public class MessageApi
{
    public static List<EndPoint> newEndpoint = [];
    public static bool isNewEndpoint(EndPoint endpoint) => false;
    public static void AddNewEndpoint(EndPoint endpoint) => newEndpoint.Add(endpoint);
    public static void SendJoinGame(UnityUdpClientConnection connection, GameCode code)
    {
        var writer = MessageWriter.Get(SendOption.Reliable);
        writer.StartMessage(1);
        writer.Write(code.Value);
        writer.Write(false);
        writer.EndMessage();

        connection?.Send(writer);
        writer.Recycle();
    }

    public static void SendSceneChange(UnityUdpClientConnection connection, GameCode code, string sceneName, int clientId)
    {
        var writer = MessageWriter.Get(SendOption.Reliable);
        writer.StartMessage(5);
        writer.Write(code.Value);
        writer.StartMessage(6);
        writer.WritePacked(clientId);
        writer.Write(sceneName);
        writer.EndMessage();
        writer.EndMessage();

        connection?.Send(writer);
        writer.Recycle();
    }

    public static byte GetRandomByte()
    {
        System.Random random = new();
        return (byte)random.Next(0, 4);
    }

    public static string GetRandomSkin()
    {
        var skins = new[]
        {
            "skin_WhiteSuspskin",
            "skin_YellowSuspskin",
            "skin_Winter",
            "skin_Wimpskin",
            "skin_Wall",
            "skin_w21_tree",
            "skin_w21_snowmate",
            "skin_w21_nutcracker",
            "skin_Slothskin",
            "skin_scarfskin",
            "skin_Sanskin",
            "skin_rhm",
            "skin_PusheenGreyskin",
            "skin_Police",
            "skin_hl_gura",
            "skin_D2Osiris",
        };

        System.Random random = new();
        int index = random.Next(skins.Length);
        return skins[index];
    }

    public static string GetRandomHat()
    {
        var skins = new[]
        {
            "hat_cashHat",
            "hat_pk05_Wizardhat",
            "hat_arrowhead",
            "hat_pkHW01_CatEyes",
            "hat_cat_grey",
            "hat_cat_orange",
            "hat_cat_pink",
            "hat_cat_snow",
            "hat_pk05_Cheese",
            "hat_duck",
            "hat_captain",
            "hat_pusheenSitHat",
            "hat_crownBean",
            "hat_pk05_Plant",
            "hat_glowstick",
            "hat_pk02_ToiletPaperHat",
        };

        System.Random random = new();
        int index = random.Next(skins.Length);
        return skins[index];
    }

    public static string GetRandomNamePlate()
    {
        var skins = new[]
        {
            "nameplate_Airship_Hull",
            "nameplate_hw_candy",
            "nameplate_dungeonFloor",
            "nameplate_ballPit",
            "nameplate_flagPride",
            "nameplate_impostor",
        };

        System.Random random = new();
        int index = random.Next(skins.Length);
        return skins[index];
    }

    public static string GetRandomVisor()
    {
        var skins = new[]
        {
            "visor_Crack",
            "visor_lny_dragon",
            "visor_clownnose",
            "visor_pk01_EyesVisor",
            "visor_lny_pig",
            "visor_ToastVisor",
            "visor_EyepatchL",
            "visor_mummy",
            "visor_PizzaVisor",
            "visor_BubbleBumVisor",
            "visor_vr_Vr-Black",
            "visor_vr_Vr-White",
            "visor_Stealthgoggles",
            "visor_pk01_Security1Visor",
            "visor_IceCreamChocolateVisor",
            "visor_Reginald",
        };

        System.Random random = new();
        int index = random.Next(skins.Length);
        return skins[index];
    }

    public static string GetRandomPet()
    {
        var skins = new[]
        {
            "pet_Goose",
            "pet_Bush",
            "pet_Charles",
            "pet_GuiltySpark",
            "pet_Pusheen",
            "pet_Charles_Red",
            "pet_UFO",
            "pet_Crewmate",
        };

        System.Random random = new();
        int index = random.Next(skins.Length);
        return skins[index];
    }

    public static string GetRandomName()
    {
        var names = new[]
        {
            "REDACTED, sorry!",
        };
        System.Random random = new();
        int index = random.Next(names.Length);
        var name = names[index].Replace(".", string.Empty).Replace("_", string.Empty);

        if (name.Length >= 9)
        {
            name = name[..9];
        }

        return name;
    }

    public static int GetRandomLevel()
    {
        System.Random random = new();
        return random.Next(10, 91);
    }

    public static byte GetRandomColor()
    {
        System.Random random = new();
        return (byte)random.Next(0, 18);
    }

    public static string GetDuckString()
    {
        var original = "REDACTED, sorry!";
        var random = new System.Random();
        var sb = new StringBuilder();
        var chars = "#%&-;'\\,？、；";

        foreach (var c in original)
        {
            sb.Append(c);
            if (sb.Length < 95)
            {
                sb.Append(chars[random.Next(chars.Length)]);
            }
        }
        return sb.Length > 95 ? sb.ToString().Substring(0, 95) : sb.ToString();
    }

    public static bool isFriendcodeADuck(string code)
    {
        if (string.IsNullOrEmpty(code)) return false;

        var parts = code.Split('#');
        if (parts.Length != 2) return false;

        var prefix = parts[0];
        var suffix = parts[1];

        return prefix.Length == 4 && prefix.All(char.IsLetter) && suffix.Length > 0;
    }
}

public struct GameListing
{
    public static string AddressToString(uint address)
    {
        return string.Format("{0}.{1}.{2}.{3}",
        [
                (byte)address,
                (byte)(address >> 8),
                (byte)(address >> 16),
                (byte)(address >> 24)
        ]);
    }

    public readonly string IPString => AddressToString(IP);

    [JsonPropertyName("IP")]
    public uint IP { get; set; }
    [JsonPropertyName("Port")]
    public ushort Port { get; set; }
    [JsonPropertyName("GameId")]
    public int GameId { get; set; }
    [JsonPropertyName("PlayerCount")]
    public byte PlayerCount { get; set; }
    [JsonPropertyName("TrueHostName")]
    public string TrueHostName { get; set; }
    [JsonPropertyName("QuickChat")]
    public byte QuickChat { get; set; }
    [JsonPropertyName("MapId")]
    public byte MapId { get; set; }
    [JsonPropertyName("Language")]
    public int Language { get; set; }
}
