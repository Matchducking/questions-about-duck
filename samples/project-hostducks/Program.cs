using Impostor.Api.Games;
using Impostor.Api.Innersloth;
using MatchDucking.InnerSloth;
using System.Collections.Concurrent;
using System.Net;

namespace MatchDucking;

class Program
{
    // Handles account-related data
    private static List<AccountStatus> _accounts = new();
    private static readonly object _accountsLock = new();

    // Maintains the collection of available games
    private static ConcurrentDictionary<int, GameListing> _availableGames = new();
    private static HashSet<int> _bannedGames = new();
    private static readonly object _gamesLock = new();

    // Tracks active client connections
    private static ConcurrentDictionary<int, ClientInfo> _activeClients = new();
    private static int _nextClientId = 1;
    private static readonly object _clientIdLock = new();

    // Manages thread signaling and cancellation
    private static CancellationTokenSource _cts = new();
    private static readonly ManualResetEventSlim _hasEnoughTokens = new(false);
    private static readonly ManualResetEventSlim _hasGames = new(false);
    private static readonly ManualResetEventSlim _hasEnoughMmTokens = new(true);

    // Counts how often each game is used
    private static ConcurrentDictionary<int, GameUsageInfo> _gameUsage = new();

    // Keeps statistics on client status results
    private static int _failedClients = 0;
    private static int _successfulClients = 0;
    private static int _completedClients = 0;
    private static readonly object _statsLock = new();

    // Tracks completed and failed game joins
    private static int _completedGames = 0;
    private static int _failedGames = 0;
    private static readonly ConcurrentDictionary<int, int> _gameJoinFailCounter = new();
    private static readonly object _gameStatsLock = new();

    // Configuration constants for token and client limits  
    private const int MIN_TOKENS_REQUIRED = 5;
    private const int MIN_ID_TOKENS_REQUIRED = 30;
    private const int MAX_CLIENTS_PER_GAME = 5;
    private const int CONCURRENT_CLIENTS_PER_GAME = 3;
    private const int MMTOKEN_RATE_LIMIT_MS = 0;

    // Controls automatic program shutdown to allow restarts by other scripts
    private const bool AUTO_SHUTDOWN = false;
    private const int AUTO_SHUTDOWN_INTERVAL_MINUTES = 30;

    // Server lists organized by region
    private static List<HostServer> _naServers = new();
    private static List<HostServer> _euServers = new();
    private static List<HostServer> _asServers = new();
    private static readonly object _serversLock = new();

    // Represents server data and connection info
    public class HostServer
    {
        private int _activeConnections = 0;
        public IPEndPoint EndPoint { get; set; }
        public bool Bad { get; set; } = false;
        public int ActiveConnections => _activeConnections; // Read-only property
        public DateTime DiscoveredTime { get; set; }

        public HostServer(IPEndPoint endPoint, DateTime discoveredTime)
        {
            EndPoint = endPoint;
            DiscoveredTime = discoveredTime;
        }

        // Safely increments the active connection count
        public int IncrementConnections()
        {
            return Interlocked.Increment(ref _activeConnections);
        }

        // Safely decrements the active connection count
        public int DecrementConnections()
        {
            return Interlocked.Decrement(ref _activeConnections);
        }
    }

    // Structure for deserializing server info from JSON
    private class ServerEntry
    {
        public string ip { get; set; } = string.Empty;
        public int port { get; set; }
        public DateTime discoveredTime { get; set; }
    }

    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting application...");

        var shutdownCts = new CancellationTokenSource();
        if (AUTO_SHUTDOWN)
        {
            shutdownCts.CancelAfter(TimeSpan.FromMinutes(AUTO_SHUTDOWN_INTERVAL_MINUTES));
        }

        try
        {
            // Step 1: Load user accounts into memory
            LoadAccounts();

            // Step 2: Load the list of servers
            LoadServers();

            // Step 3: Launch thread responsible for refreshing tokens
            var tokenRefreshTask = Task.Run(TokenRefreshLoop);

            // Step 4: Start processing MM tokens asynchronously
            var processMmTokensTask = Task.Run(ProcessMmTokensLoop);

            // Keep program running until user quits or auto shutdown triggers
            Console.WriteLine("Running... press Q to quit.");
            while (!shutdownCts.Token.IsCancellationRequested)
            {
                if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q)
                {
                    break;
                }

                await Task.Delay(5000, shutdownCts.Token);

                // Display current system status to console
                PrintStatus();
            }

            // Signal all running tasks to stop
            _cts.Cancel();
            await Task.WhenAll(tokenRefreshTask, processMmTokensTask);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Application stopped automatically.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error occurred: {ex}");
        }
        finally
        {
            _cts.Dispose();
            _hasEnoughTokens.Dispose();
            _hasEnoughMmTokens.Dispose();
            shutdownCts.Dispose();
        }
    }

    private static void PrintStatus()
    {
        Console.WriteLine(new string('-', 50));
        lock (_accountsLock)
        {
            int validMmTokens = _accounts.Count(a => a.HasValidMmToken);
            int validIdTokens = _accounts.Count(a => a.HasValidIdToken);

            Console.WriteLine($"Account Summary: Total {_accounts.Count}, " +
                             $"Valid ID Tokens: {validIdTokens}, " +
                             $"Valid MM Tokens: {validMmTokens}");
        }

        lock (_serversLock)
        {
            int badNaServers = _naServers.Count(s => s.Bad);
            int activeNaServers = _naServers.Count(s => s.ActiveConnections > 0);

            int badEuServers = _euServers.Count(s => s.Bad);
            int activeEuServers = _euServers.Count(s => s.ActiveConnections > 0);

            int badAsServers = _asServers.Count(s => s.Bad);
            int activeAsServers = _asServers.Count(s => s.ActiveConnections > 0);

            Console.WriteLine($"Server Info: NA {_naServers.Count} (Bad: {badNaServers}, Active: {activeNaServers}), " +
                             $"EU {_euServers.Count} (Bad: {badEuServers}, Active: {activeEuServers}), " +
                             $"AS {_asServers.Count} (Bad: {badAsServers}, Active: {activeAsServers})");
        }

        lock (_statsLock)
        {
            Console.WriteLine($"Client Stats: Active {_activeClients.Count}, Success {_successfulClients}, Completed {_completedClients}, Failed {_failedClients}");
        }

        Console.WriteLine(new string('-', 50));
    }

    private static void LoadAccounts()
    {
        var accounts = Utils.LoadAccounts();
        Console.WriteLine($"Loaded {accounts.Count} accounts into the system");

        lock (_accountsLock)
        {
            _accounts.Clear();
            foreach (var account in accounts)
            {
                _accounts.Add(new AccountStatus
                {
                    Account = account,
                    LastMmTokenRefresh = DateTime.MinValue,
                    LastIdTokenRefresh = DateTime.MinValue,
                    InUse = false
                });
            }
        }

        var random = new Random();
        _accounts = [.. _accounts.OrderBy(_ => random.Next())];
    }

    private static void LoadServers()
    {
        try
        {
            string basePath = AppDomain.CurrentDomain.BaseDirectory;

            // Load servers from North America region file
            _naServers = LoadServersFromFile(Path.Combine(basePath, "na.json"));
            Console.WriteLine($"NA servers loaded: {_naServers.Count}");

            // Load servers from Europe region file
            _euServers = LoadServersFromFile(Path.Combine(basePath, "eu.json"));
            Console.WriteLine($"EU servers loaded: {_euServers.Count}");

            // Load servers from Asia region file
            _asServers = LoadServersFromFile(Path.Combine(basePath, "as.json"));
            Console.WriteLine($"AS servers loaded: {_asServers.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading server lists: {ex.Message}");
        }
    }

    private static List<HostServer> LoadServersFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"Server file not found: {filePath}");
            return [];
        }

        try
        {
            string json = File.ReadAllText(filePath);
            var serverEntries = System.Text.Json.JsonSerializer.Deserialize<List<ServerEntry>>(json);

            if (serverEntries == null)
                return [];

            // DTLS +3 HERE
            return serverEntries.Select(entry => new HostServer(
                new IPEndPoint(IPAddress.Parse(entry.ip), entry.port + 3),
                entry.discoveredTime
            )).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"解析服务器文件出错 {filePath}: {ex.Message}");
            return [];
        }
    }

    private static async Task TokenRefreshLoop()
    {
        Console.WriteLine("Token refresh task started");

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await RefreshTokens();

                // Verify if enough valid tokens are available
                bool hasEnoughTokens = false;
                lock (_accountsLock)
                {
                    hasEnoughTokens = _accounts.Count(a => a.HasValidMmToken) >= MIN_TOKENS_REQUIRED;
                }

                if (hasEnoughTokens && !_hasEnoughTokens.IsSet)
                {
                    _hasEnoughTokens.Set();
                    Console.WriteLine($"Minimum token threshold reached ({MIN_TOKENS_REQUIRED})");
                }
                else if (!hasEnoughTokens && _hasEnoughTokens.IsSet)
                {
                    _hasEnoughTokens.Reset();
                    Console.WriteLine("Token count fell below required minimum");
                }

                // Pause for one second before next check
                await Task.Delay(1000, _cts.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during token refresh: {ex.Message}");
                await Task.Delay(5000, _cts.Token);
            }
        }
    }

    private static async Task RefreshTokens()
    {
        DateTime now = DateTime.Now;
        List<AccountStatus> accountsToProcess = new();

        // Count current valid tokens
        int validMmTokens = 0;
        int validIdTokens = 0;
        int longValidMmTokens = 0; // Number of MM tokens valid for more than 2 minutes

        lock (_accountsLock)
        {
            validMmTokens = _accounts.Count(a => a.HasValidMmToken);
            validIdTokens = _accounts.Count(a => a.HasValidIdToken);
            longValidMmTokens = _accounts.Count(a => a.HasLongValidMmToken);
        }

        // Pause refreshing if more than 80 long-validity MM tokens are present
        if (validMmTokens > 45)
        {
            Console.WriteLine($"Long-validity MM Tokens ({longValidMmTokens}) exceed 80, delaying refresh");
            return;
        }

        // Determine if MM token refresh should be prioritized over game searching
        // If MM tokens exceed 60, ignore the ID token to MM token ratio restriction
        bool needPriorityMmRefresh = validMmTokens < 60 && validMmTokens < validIdTokens / 2 && validIdTokens > 0;
        if (needPriorityMmRefresh && _hasEnoughMmTokens.IsSet)
        {
            _hasEnoughMmTokens.Reset();
            Console.WriteLine($"MM tokens ({validMmTokens}) below half of ID tokens ({validIdTokens}) and less than 60, pausing game search to prioritize MM token refresh");
        }
        else if (!needPriorityMmRefresh && !_hasEnoughMmTokens.IsSet)
        {
            _hasEnoughMmTokens.Set();
            Console.WriteLine($"MM tokens ({validMmTokens}) reached at least half of ID tokens ({validIdTokens}) or above 60, resuming game search");
        }

        // Select accounts to refresh tokens for
        lock (_accountsLock)
        {
            if (validIdTokens < MIN_ID_TOKENS_REQUIRED)
            {
                // Prioritize refreshing ID tokens if insufficient
                accountsToProcess = _accounts
                    .Where(a => !a.InUse)
                    .OrderBy(a => string.IsNullOrEmpty(a.Account.UserIdToken)) // Prioritize accounts without ID token
                    .ThenBy(a => a.Account.UserIdTokenExpireAt) // Then those with soon-to-expire ID tokens
                    .Take(10) // 每回处理十个帐号
                    .ToList();

                if (accountsToProcess.Count > 0)
                    Console.WriteLine($"ID tokens ({validIdTokens}) below threshold {MIN_ID_TOKENS_REQUIRED}, prioritizing ID token refresh");
            }
            else if (needPriorityMmRefresh)
            {
                // When MM tokens are low, pick accounts with valid ID token but missing MM token
                accountsToProcess = _accounts
                    .Where(a => !a.InUse && a.HasValidIdToken && !a.HasValidMmToken)
                    .OrderBy(a => a.IdTokenUsed) // Prefer ID tokens not used yet
                    .Take(10) // 每回处理十个帐号
                    .ToList();

                if (accountsToProcess.Count > 0)
                    Console.WriteLine($"Prioritizing MM token refresh for {accountsToProcess.Count} accounts");
            }
            else if (validMmTokens < MIN_TOKENS_REQUIRED)
            {
                // When MM tokens are low but not critical, refresh cautiously prioritizing unused ID tokens
                accountsToProcess = _accounts
                    .Where(a => !a.InUse)
                    .OrderBy(a => string.IsNullOrEmpty(a.Account.UserIdToken)) // Empty ID Token
                    .ThenBy(a => a.Account.UserIdTokenExpireAt)
                    .ThenBy(a => a.IdTokenUsed)
                    .ThenBy(a => a.Account.MMTokenExpireAt)
                    .Take(10) // 每回处理十个帐号
                    .ToList();
            }
            else
            {
                // Routine maintenance: refresh tokens that are about to expire
                accountsToProcess = _accounts
                    .Where(a => !a.InUse && (
                        a.Account.UserIdTokenExpireAt <= now.AddMinutes(10) ||
                        a.Account.MMTokenExpireAt <= now.AddMinutes(1)
                    ))
                    .Take(5)
                    .ToList();
            }
        }

        // Process each account's token refresh
        foreach (var accountStatus in accountsToProcess)
        {
            try
            {
                bool idTokenRefreshed = false;

                // Refresh ID token if missing or expired, or if last refresh was over a minute ago
                if (string.IsNullOrEmpty(accountStatus.Account.UserIdToken) ||
                    accountStatus.Account.UserIdTokenExpireAt <= now ||
                    (now - accountStatus.LastIdTokenRefresh).TotalMinutes >= 1)
                {
                    var (success, idToken, productUserId) = await Http.Http.GetEpicIdTokenAsync(
                        accountStatus.Account.AccessToken,
                        Http.Http.GenerateRandomName());

                    if (success)
                    {
                        lock (_accountsLock)
                        {
                            accountStatus.Account.UserIdToken = idToken;
                            accountStatus.Account.UserIdTokenExpireAt = now.AddMinutes(59);
                            accountStatus.LastIdTokenRefresh = now;
                            accountStatus.IdTokenUsed = false; // Reset usage flag since token is new
                        }
                        idTokenRefreshed = true;

                        // Wait a second before refreshing MM token
                        await Task.Delay(1000, _cts.Token);
                    }
                    else
                    {
                        Console.WriteLine($"Failed to refresh ID token: {idToken}");
                        continue; // Skip this account if ID token refresh fails
                    }
                }

                // Refresh MM token if ID token is valid and MM token is missing or about to expire
                if ((idTokenRefreshed || accountStatus.HasValidIdToken) &&
                    (string.IsNullOrEmpty(accountStatus.Account.MMToken) ||
                    accountStatus.Account.MMTokenExpireAt <= now.AddMinutes(1)))
                {
                    try
                    {
                        var gameVersion = new GameVersion(2024, 8, 11);
                        var (success, mmToken) = await Http.Http.GetMatchmakerTokenAsync(
                            accountStatus.Account.UserIdToken!,
                            accountStatus.Account.Puid,
                            Http.Http.GenerateRandomName(),
                            gameVersion);

                        if (success)
                        {
                            lock (_accountsLock)
                            {
                                accountStatus.Account.MMToken = mmToken;
                                accountStatus.Account.MMTokenExpireAt = now.AddMinutes(4);
                                accountStatus.LastMmTokenRefresh = now;
                                accountStatus.IdTokenUsed = true; // Mark this ID token as used
                            }
                            Console.WriteLine($"MM token refreshed for account: {accountStatus.Account.Puid}");
                        }
                        else
                        {
                            Console.WriteLine($"Failed to refresh MM token: {mmToken}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Exception occurred during MM token refresh: {ex.Message}");
                    }
                }

                // Rate limit between MM token requests
                await Task.Delay(MMTOKEN_RATE_LIMIT_MS, _cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理账号 {accountStatus.Account.Puid} 时发生异常: {ex.Message}");
            }
        }
    }

    private static async Task GetGamesLoop()
    {
        Console.WriteLine("Game retrieval task started, awaiting sufficient valid tokens...");

        // Wait until enough tokens are available
        await WaitForTokens();

        Console.WriteLine("Beginning game retrieval...");

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // Check number of valid MM tokens
                int validMmTokens = 0;
                lock (_accountsLock)
                {
                    validMmTokens = _accounts.Count(a => a.HasValidMmToken);
                }

                // Wait for enough MM tokens unless count exceeds 60
                if (!_hasEnoughMmTokens.IsSet && validMmTokens < 60)
                {
                    Console.WriteLine("Waiting for MM tokens to reach sufficient quantity or exceed 60...");
                    await _hasEnoughMmTokens.WaitHandle.WaitOneAsync(_cts.Token);
                    Console.WriteLine("MM token count sufficient, resuming game retrieval");
                }
                else if (!_hasEnoughMmTokens.IsSet)
                {
                    Console.WriteLine($"MM tokens ({validMmTokens}) over 60, ignoring ratio restrictions, continuing game retrieval");
                }

                // Acquire one available MM token
                string? mmToken = null;
                lock (_accountsLock)
                {
                    var account = _accounts.FirstOrDefault(a => a.HasValidMmToken && !a.InUse);
                    if (account != null)
                    {
                        mmToken = account.Account.MMToken;
                        // Mark account as in-use temporarily until games are fetched
                        account.InUse = true;
                    }
                }

                if (mmToken != null)
                {
                    // Query games from all three regions
                    var servers = new[] { "na", "eu", "as" };
                    foreach (var server in servers)
                    {
                        var (success, games) = await Http.Http.FindFilteredGameAsync(mmToken, server);
                        if (success && games.Length > 0)
                        {
                            // Add new games to available list, skipping banned ones
                            lock (_gamesLock)
                            {
                                foreach (var game in games)
                                {
                                    if (_bannedGames.Contains(game.GameId))
                                        continue;

                                    _availableGames.TryAdd(game.GameId, game);
                                }
                            }

                            Console.WriteLine($"Retrieved {games.Length} games from {server} region");

                            // Signal availability of games if any found
                            if (_availableGames.Count > 0 && !_hasGames.IsSet)
                            {
                                _hasGames.Set();
                                Console.WriteLine("Games available, processing thread may start work");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Failed to get games or no games found in {server} region");
                        }
                    }

                    // Release account usage lock
                    lock (_accountsLock)
                    {
                        var account = _accounts.FirstOrDefault(a => a.Account.MMToken == mmToken);
                        if (account != null)
                        {
                            account.InUse = false;
                        }
                    }
                }
                else
                {
                    Console.WriteLine("No available MM token to fetch games");

                    // Wait until tokens are available
                    await WaitForTokens();
                }

                // Pause 15 seconds before next fetch
                await Task.Delay(15000, _cts.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching games: {ex.Message}");
                await Task.Delay(5000, _cts.Token);
            }
        }
    }

    private static async Task WaitForTokens()
    {
        Console.WriteLine("等待足够的有效令牌...");
        await _hasEnoughTokens.WaitHandle.WaitOneAsync(_cts.Token);
    }

    private static async Task ProcessMmTokensLoop()
    {
        Console.WriteLine("Token handling loop started, waiting for sufficient valid tokens...");

        await WaitForTokens();

        Console.WriteLine("Beginning MMToken processing...");

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                while (_activeClients.Count >= 1000 && !_cts.IsCancellationRequested)
                {
                    Console.WriteLine($"Active clients count {_activeClients.Count} reached limit, waiting until it drops below 800...");
                    await Task.Delay(5000, _cts.Token);

                    if (_activeClients.Count <= 800)
                    {
                        Console.WriteLine($"当前客户端数量为 {_activeClients.Count}，已低于2000，开始继续创建...");
                        break;
                    }
                }

                string? mmToken = null;
                lock (_accountsLock)
                {
                    // Find an account with a valid MM token not currently in use
                    var account = _accounts.FirstOrDefault(a => a.HasValidMmToken && !a.InUse);
                    if (account != null)
                    {
                        mmToken = account.Account.MMToken;
                        account.InUse = true;
                    }
                }

                if (mmToken != null)
                {
                    // Launch 30 clients using the selected MM token
                    await CreateClientsWithToken(mmToken);
                }
                else
                {
                    Console.WriteLine("No available MM token found, waiting...");
                    await WaitForTokens();
                }

                await Task.Delay(1000, _cts.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during MM token processing: {ex.Message}");
                await Task.Delay(5000, _cts.Token);
            }
        }
    }

    private static async Task CreateClientsWithToken(string mmToken)
    {
        var clientCreationTasks = new List<Task>();

        // Pick random servers from each region to spread client load
        var naEndpoints = SelectRandomServers(_naServers, 10);
        var euEndpoints = SelectRandomServers(_euServers, 10);
        var asEndpoints = SelectRandomServers(_asServers, 10);

        var allEndpoints = naEndpoints.Concat(euEndpoints).Concat(asEndpoints).ToList();

        Console.WriteLine($"Starting client creation with MMToken: NA={naEndpoints.Count}, EU={euEndpoints.Count}, AS={asEndpoints.Count}");

        foreach (var server in allEndpoints)
        {
            server.IncrementConnections();

            // Start client and track the task
            clientCreationTasks.Add(CreateAndStartClient(server, mmToken));
        }

        if (clientCreationTasks.Count > 0)
        {
            await Task.WhenAll(clientCreationTasks);
            Console.WriteLine($"Finished creating {clientCreationTasks.Count} clients");
        }
    }

    private static List<HostServer> SelectRandomServers(List<HostServer> servers, int count)
    {
        var availableServers = new List<HostServer>();

        lock (_serversLock)
        {
            // Filter servers with less than 10 active connections
            availableServers = servers
                .Where(s => s.ActiveConnections < 10)
                .ToList();
        }

        if (availableServers.Count <= count)
            return availableServers;

        var random = new Random();
        // Randomly pick the specified number of servers from the filtered list
        return availableServers
            .OrderBy(_ => random.Next())
            .Take(count)
            .ToList();
    }

    private static void CleanupCompletedClients()
    {
        var completedClients = _activeClients
            .Where(kvp => IsClientCompleted(kvp.Value))
            .ToList();

        foreach (var client in completedClients)
        {
            if (_activeClients.TryRemove(client.Key, out var clientInfo))
            {
                // Release the account tied to this client so it can be reused
                lock (_accountsLock)
                {
                    var account = _accounts.FirstOrDefault(a => a.Account.MMToken == clientInfo.MmToken);
                    if (account != null)
                    {
                        account.InUse = false;
                    }
                }

                // Mark server as bad if client failed to connect or join
                if (clientInfo.HostServer != null)
                {
                    if (clientInfo.Client.State == Client.ClientState.Failed ||
                        clientInfo.Client.State == Client.ClientState.JoinFailed)
                    {
                        clientInfo.HostServer.Bad = true;
                    }
                }
            }
        }
    }

    private static bool IsClientCompleted(ClientInfo clientInfo)
    {
        return clientInfo.Client.State == Client.ClientState.Completed ||
               clientInfo.Client.State == Client.ClientState.Banned ||
               clientInfo.Client.State == Client.ClientState.Failed;
    }

    private static void UpdateGameUsage(int gameId)
    {
        if (_gameUsage.TryGetValue(gameId, out var usageInfo))
        {
            usageInfo.ClientsUsed++;

            if (usageInfo.ClientsUsed >= MAX_CLIENTS_PER_GAME)
            {
                _completedGames++;
                _availableGames.TryRemove(gameId, out _);
                _gameUsage.TryRemove(gameId, out _);
                Console.WriteLine($"Game {gameId} reached max usage ({MAX_CLIENTS_PER_GAME}), removed from active list");
            }
        }
    }

    private static string? GetAvailableMMToken()
    {
        lock (_accountsLock)
        {
            var account = _accounts
                .Where(a => a.HasValidMmToken && !a.InUse)
                .OrderBy(a => a.Account.MMTokenExpireAt)
                .FirstOrDefault();

            if (account != null)
            {
                account.InUse = true;
                return account.Account.MMToken;
            }
        }
        return null;
    }

    private static async Task CreateAndStartClient(HostServer server, string mmToken)
    {
        int clientId;
        lock (_clientIdLock)
        {
            clientId = _nextClientId++;
        }

        try
        {
            // Prepare a new game code placeholder
            var gameCode = new GameCode(-1);

            // Initialize a new client instance
            var name = Http.Http.GenerateRandomName();
            var client = new Client(
                clientId,
                name,
                mmToken,
                server.EndPoint,
                new GameVersion(2024, 8, 11),
                Platforms.Switch,
                gameCode,
                0 // vote ban disabled for now
            );

            // Attach event listeners for state changes
            client.StateChanged += Client_StateChanged;

            // On client completion or failure, adjust server connection count
            client.StateChanged += (sender, state) =>
            {
                if (state == Client.ClientState.Completed ||
                    state == Client.ClientState.Failed ||
                    state == Client.ClientState.Banned ||
                    state == Client.ClientState.JoinFailed ||
                    state == Client.ClientState.GameDeleted)
                {
                    server.IncrementConnections();
                }
            };

            // Add client info to active clients tracking
            var clientInfo = new ClientInfo
            {
                Client = client,
                MmToken = mmToken,
                GameId = 0, // not assigned here
                StartTime = DateTime.Now,
                HostServer = server
            };

            _activeClients[clientId] = clientInfo;

            Console.WriteLine($"Client {clientId} created and connecting to server {server.EndPoint}");

            // Run the client's update loop in the background
            _ = Task.Run(async () =>
            {
                try
                {
                    while (_activeClients.ContainsKey(clientId) &&
                           !IsClientCompleted(clientInfo) &&
                           !_cts.IsCancellationRequested)
                    {
                        client.FixedUpdate();
                        await Task.Delay(33);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in client {clientId} update loop: {ex.Message}");
                }
            });

            // Start client connection asynchronously
            var result = await client.StartAsync();
            if (!result)
            {
                Console.WriteLine($"Failed to start client {clientId}");

                // Mark server as bad and update connection count
                server.Bad = true;
                server.IncrementConnections();

                // Release the associated account
                lock (_accountsLock)
                {
                    var account = _accounts.FirstOrDefault(a => a.Account.MMToken == mmToken);
                    if (account != null)
                    {
                        account.InUse = false;
                    }
                }

                _activeClients.TryRemove(clientId, out _);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception while creating client: {ex.Message}");

            // Decrement server connection count on error
            server.IncrementConnections();

            // Free the account for reuse
            lock (_accountsLock)
            {
                var account = _accounts.FirstOrDefault(a => a.Account.MMToken == mmToken);
                if (account != null)
                {
                    account.InUse = false;
                }
            }
        }
    }

    private static void Client_StateChanged(object? sender, Client.ClientState state)
    {
        if (sender is Client client)
        {
            Console.WriteLine($"Client {client.Id} changed state to {state}");

            lock (_statsLock)
            {
                // Update counters based on client status
                if (state == Client.ClientState.Connected)
                {
                    _successfulClients++;
                }
                else if (state == Client.ClientState.Failed)
                {
                    _failedClients++;
                }
                else if (state == Client.ClientState.Completed)
                {
                    _completedClients++;
                }
            }
        }
    }

    private static void UpdateGameJoinFailCounter(int gameId)
    {
        lock (_gameStatsLock)
        {
            int failCount = _gameJoinFailCounter.AddOrUpdate(
                gameId,
                1,
                (_, count) => count + 1
            );

            // If a game has 3 failed join attempts, remove and ban it
            if (failCount >= 3)
            {
                _failedGames++;
                _gameJoinFailCounter.TryRemove(gameId, out _);
                _availableGames.TryRemove(gameId, out _);
                _bannedGames.Add(gameId); // prevent future attempts on this game

                Console.WriteLine($"Game {gameId} has 3 failed joins, marked as failed. Total failed games: {_failedGames}");
            }
        }
    }

    private class AccountStatus
    {
        public Utils.Account Account { get; set; } = null!;
        public DateTime LastIdTokenRefresh { get; set; }
        public DateTime LastMmTokenRefresh { get; set; }
        public bool InUse { get; set; }
        public bool NeedsIdTokenRefresh { get; set; }
        public bool NeedsMmTokenRefresh { get; set; }
        public bool IdTokenUsed { get; set; } = false;

        public bool HasValidIdToken => !string.IsNullOrEmpty(Account.UserIdToken) &&
                                       Account.UserIdTokenExpireAt > DateTime.Now;

        public bool HasValidMmToken => !string.IsNullOrEmpty(Account.MMToken) &&
                                       Account.MMTokenExpireAt > DateTime.Now;

        public bool HasLongValidMmToken => HasValidMmToken &&
                                          Account.MMTokenExpireAt > DateTime.Now.AddMinutes(2);
    }

    private class ClientInfo
    {
        public Client Client { get; set; } = null!;
        public string MmToken { get; set; } = null!;
        public int GameId { get; set; }
        public DateTime StartTime { get; set; }
        public HostServer? HostServer { get; set; } // Reference to the possible server this client is connected to
    }

    private class GameUsageInfo
    {
        public int GameId { get; set; }
        public int ClientsUsed { get; set; }
    }
}

public static class WaitHandleExtensions
{
    public static async Task<bool> WaitOneAsync(this WaitHandle handle, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();

        RegisteredWaitHandle? registeredHandle = null;
        CancellationTokenRegistration tokenRegistration = default;

        try
        {
            tokenRegistration = cancellationToken.Register(() =>
            {
                registeredHandle?.Unregister(null);
                tcs.TrySetCanceled();
            });

            registeredHandle = ThreadPool.RegisterWaitForSingleObject(
                handle,
                (state, timedOut) => { tcs.TrySetResult(!timedOut); },
                null,
                -1,
                true);

            return await tcs.Task;
        }
        finally
        {
            registeredHandle?.Unregister(null);
            tokenRegistration.Dispose();
        }
    }
}
