using System.Text.Json;

namespace MatchDucking;

public static class Utils
{
    public static List<Account> LoadAccounts()
    {
        try
        {
            string filePath = Path.Combine(AppContext.BaseDirectory, "accounts.json");
            string jsonString = File.ReadAllText(filePath);
            var accounts = JsonSerializer.Deserialize<List<Account>>(jsonString);

            if (accounts == null)
            {
                throw new Exception("Failed to parse accounts.json");
            }

            return accounts;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading accounts: {ex.Message}");
            Environment.Exit(1);
            return null; // This line will never be reached, but is required to satisfy the return type
        }
    }

    public class Account
    {
        public required string Puid { get; init; }
        public required string AccessToken { get; init; }
        public required string FriendCode { get; init; }
        public string? UserIdToken { get; set; }
        public DateTime UserIdTokenExpireAt { get; set; }
        public string? MMToken { get; set; }
        public DateTime MMTokenExpireAt { get; set; }
    }
}
