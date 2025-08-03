using Impostor.Api.Innersloth;
using MatchDucking.InnerSloth;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatchDucking.Http;

public static class Http
{
    // Shared HTTP client for all requests
    private static readonly HttpClient client;

    // Static constructor initializes the client with handler configuration
    static Http()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = System.Net.WebRequest.GetSystemWebProxy(),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };

        client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    private const string EPIC_AUTH = "Basic eHl6YTc4OTFxdHJtb1lMcjg2d2U2RGxmQ0ExUlJzcDg6bkdUaFFhbnp2dGhBMkhQYUFSWGUveHV0enNLeXg1V0p2ZU5rQng0NHRpNA==";

    // Generate a random device model string (used in Epic auth)
    private static string GenerateDeviceModel()
    {
        var chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var rand = new Random();
        var result = new string(Enumerable.Repeat(chars, 32).Select(s => s[rand.Next(s.Length)]).ToArray());
        return result;
    }

    // Generates a unique nonce string (format: 11-10 characters)
    private static string GenerateNonce()
    {
        var chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var rand = new Random();

        var first = new string(Enumerable.Repeat(chars, 11).Select(s => s[rand.Next(s.Length)]).ToArray());
        var second = new string(Enumerable.Repeat(chars, 10).Select(s => s[rand.Next(s.Length)]).ToArray());

        return $"{first}-{second}";
    }

    // Request temporary access token from Epic using random device ID
    public static async Task<(bool success, string result)> GetEpicAccessTokenAsync()
    {
        var url = "https://api.epicgames.dev/auth/v1/accounts/deviceid";
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("deviceModel", GenerateDeviceModel())
        });

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "deflate, gzip");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("Authorization", EPIC_AUTH);
        client.DefaultRequestHeaders.Add("User-Agent", "EOS-SDK/1.15.5-24377445 (GooglePixel/12.0.0.64bit) AmongUs/1.0");

        try
        {
            var response = await client.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
                return (false, $"Request failed. Status: {response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("access_token", out var token))
                return (true, token.GetString() ?? "");

            return (false, "No access_token in response.");
        }
        catch (Exception ex)
        {
            return (false, $"Exception during request: {ex.Message}");
        }
    }

    // Exchange access token for full id_token and product user id
    public static async Task<(bool success, string idToken, string productUserId)> GetEpicIdTokenAsync(string accessToken, string name)
    {
        var url = "https://api.epicgames.dev/auth/v1/oauth/token";

        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "external_auth"),
            new KeyValuePair<string, string>("external_auth_type", "deviceid_access_token"),
            new KeyValuePair<string, string>("display_name", name),
            new KeyValuePair<string, string>("deployment_id", "503cd077a7804777aee5a6eeb5cfe62d"),
            new KeyValuePair<string, string>("external_auth_token", accessToken),
            new KeyValuePair<string, string>("nonce", GenerateNonce())
        ]);

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "deflate, gzip");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("Authorization", EPIC_AUTH);
        client.DefaultRequestHeaders.Add("User-Agent", "EOS-SDK/1.15.5-24377445 (GooglePixel/12.0.0.64bit) AmongUs/1.0");
        client.DefaultRequestHeaders.Add("X-EOS-Version", "1.15.5-24377445");

        try
        {
            var response = await client.PostAsync(url, payload);
            if (!response.IsSuccessStatusCode)
                return (false, $"Request failed. Status: {response.StatusCode}", "");

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("id_token", out var idTokenElement))
                return (false, "No id_token in response.", "");

            var productUserId = doc.RootElement.TryGetProperty("product_user_id", out var puid)
                ? puid.GetString() ?? ""
                : "";

            return (true, idTokenElement.GetString() ?? "", productUserId);
        }
        catch (Exception ex)
        {
            return (false, $"Exception: {ex.Message}", "");
        }
    }

    // Sends player information to matchmaker and retrieves session token
    public static async Task<(bool success, string result)> GetMatchmakerTokenAsync(string idToken, string productUserId, string name, GameVersion version)
    {
        var endpoints = new[]
        {
            "https://matchmaker.among.us/api/user",
            "https://matchmaker-as.among.us/api/user",
            "https://matchmaker-eu.among.us/api/user"
        };

        var random = new Random();
        var url = endpoints[random.Next(endpoints.Length)];

        var content = new StringContent(
            JsonSerializer.Serialize(new
            {
                Puid = productUserId,
                Username = name,
                ClientVersion = version.Value,
                Language = GameKeywords.English
            }),
            Encoding.UTF8,
            "application/json"
        );

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", "UnityPlayer/2020.3.45f1 (UnityWebRequest/1.0, libcurl/7.84.0-DEV)");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "deflate, gzip");
        client.DefaultRequestHeaders.Add("Accept", "text/plain");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {idToken}");
        client.DefaultRequestHeaders.Add("X-Unity-Version", "2020.3.45f1");

        try
        {
            var response = await client.PostAsync(url, content);
            var result = await response.Content.ReadAsStringAsync();

            return response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(result)
                ? (true, result)
                : (false, $"Request failed. Status: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, $"Exception during request: {ex.Message}");
        }
    }

    // Sets player's friend code via Innersloth backend API
    public static async Task<(bool success, string result)> SetFriendCodeAsync(string idToken, string friendCode)
    {
        var url = "https://backend.innersloth.com/api/user/username";

        var content = new StringContent(
            JsonSerializer.Serialize(new
            {
                data = new
                {
                    attributes = new
                    {
                        username = friendCode,
                        recipient_puid = "",
                        recipient_friendcode = ""
                    },
                    type = "change_username"
                }
            }),
            Encoding.UTF8,
            "application/vnd.api+json"
        );

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", "UnityPlayer/2020.3.45f1 (UnityWebRequest/1.0, libcurl/7.84.0-DEV)");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "deflate, gzip");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.api+json");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {idToken}");
        client.DefaultRequestHeaders.Add("X-Unity-Version", "2020.3.45f1");

        try
        {
            var response = await client.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
                return (false, $"Request failed. Status: {response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var attributes = doc.RootElement.GetProperty("data").GetProperty("attributes");
            var username = attributes.GetProperty("username").GetString();
            var discriminator = attributes.GetProperty("discriminator").GetString();

            return (true, $"{username}#{discriminator}");
        }
        catch (Exception ex)
        {
            return (false, $"Exception: {ex.Message}");
        }
    }

    // Fetches active games using a prebuilt filtered query from a regional server
    public static async Task<(bool success, GameListing[] result)> FindFilteredGameAsync(string mmtoken, string server)
    {
        var regionUrls = new Dictionary<string, string>
        {
            { "na", "https://matchmaker.among.us/api/games/filtered?filter=..." },
            { "eu", "https://matchmaker-as.among.us/api/games/filtered?filter=..." },
            { "as", "https://matchmaker-eu.among.us/api/games/filtered?filter=..." }
        };

        if (!regionUrls.TryGetValue(server, out var url))
            return (false, Array.Empty<GameListing>());

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", "UnityPlayer/2020.3.45f1 (UnityWebRequest/1.0, libcurl/7.84.0-DEV)");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "deflate, gzip");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {mmtoken}");
        client.DefaultRequestHeaders.Add("X-Unity-Version", "2020.3.45f1");

        try
        {
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return (false, Array.Empty<GameListing>());

            var json = await response.Content.ReadAsStringAsync();

            var doc = JsonDocument.Parse(json);
            var gamesArray = doc.RootElement.GetProperty("games");

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            var parsed = JsonSerializer.Deserialize<GameListing[]>(gamesArray.GetRawText(), options);
            return (true, parsed ?? Array.Empty<GameListing>());
        }
        catch
        {
            return (false, Array.Empty<GameListing>());
        }
    }

    // Generate a pseudo friend code (e.g., abcd#1234 )
    public static string GenerateFriendCode()
    {
        var rand = new Random();
        const string letters = "abcdefghijklmnopqrstuvwxyz";

        var prefix = new string(Enumerable.Range(0, 4).Select(_ => letters[rand.Next(letters.Length)]).ToArray());
        var number = rand.Next(1000, 9999);

        return $"{prefix}#{number} ";
    }

    // Generate a name via MessageApi call
    public static string GenerateRandomName() => MessageApi.GetRandomName();
}