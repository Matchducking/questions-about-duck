using Hazel;
using Hazel.Dtls;
using Hazel.Udp;
using Impostor.Api.Innersloth;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace MatchDucking.UdpConnection
{
    public static class HazelConnection
    {
        // Create and return a DTLS connection using a hardcoded certificate
        public static DtlsUnityConnection CreateDtlsConnection(IPEndPoint endpoint)
        {
            var logger = new ConsoleLogger();
            var connection = new DtlsUnityConnection(logger, endpoint, IPMode.IPv4);

            var cert = new X509Certificate2(
                CryptoHelpers.DecodePEM(
                    "\r\n-----BEGIN CERTIFICATE-----\r\nMIIDbTCCAlWgAwIBAgIUf8xD1G/d5NK1MTjQAYGqd1AmBvcwDQYJKoZIhvcNAQEL\r\nBQAwRTELMAkGA1UEBhMCQVUxEzARBgNVBAgMClNvbWUtU3RhdGUxITAfBgNVBAoM\r\nGEludGVybmV0IFdpZGdpdHMgUHR5IEx0ZDAgFw0yMTAyMDIxNzE4MDFaGA8yMjk0\r\nMTExODE3MTgwMVowRTELMAkGA1UEBhMCQVUxEzARBgNVBAgMClNvbWUtU3RhdGUx\r\nITAfBgNVBAoMGEludGVybmV0IFdpZGdpdHMgUHR5IEx0ZDCCASIwDQYJKoZIhvcN\r\nAQEBBQADggEPADCCAQoCggEBAL7GFDbZdXwPYXeHWRi2GfAXkaLCgxuSADfa1pI2\r\nvJkvgMTK1miSt3jNSg/o6VsjSOSL461nYmGCF6Ho3fMhnefOhKaaWu0VxF0GR1bd\r\ne836YWzhWINQRwmoVD/Wx1NUjLRlTa8g/W3eE5NZFkWI70VOPRJpR9SqjNHwtPbm\r\nKi41PVgJIc3m/7cKOEMrMYNYoc6E9ehwLdJLQ5olJXnMoGjHo2d59hC8KW2V1dY9\r\nsacNPUjbFZRWeQ0eJ7kbn8m3a5EuF34VEC7DFcP4NCWWI7HO5/KYE+mUNn0qxgua\r\nr32qFnoaKZr9dXWRWJSm2XecBgqQmeF/90gdbohNNHGC/iMCAwEAAaNTMFEwHQYD\r\nVR0OBBYEFAJAdUS5AZE3U3SPQoG06Ahq3wBbMB8GA1UdIwQYMBaAFAJAdUS5AZE3\r\nU3SPQoG06Ahq3wBbMA8GA1UdEwEB/wQFMAMBAf8wDQYJKoZIhvcNAQELBQADggEB\r\nALUoaAEuJf4kQ1bYVA2ax2QipkUM8PL9zoNiDjUw6ZlwMFi++XCQm8XDap45aaeZ\r\nMnXGBqIBWElezoH6BNSbdGwci/ZhxXHG/qdHm7zfCTNaLBe2+sZkGic1x6bZPFtK\r\nZUjGy7LmxsXOxqGMgPhAV4JbN1+LTmOkOutfHiXKe4Z1zu09mOo9sWfGCkbIyERX\r\nQQILBYSIkg3hU4R4xMOjvxcDrOZja6fSNyi2sgidTfe5OCKC2ovU7OmsQqzb7mFv\r\ne+7kpIUp6AZNc49n6GWtGeOoL7JUAqMOIO+R++YQN7/dgaGDPuu0PpmgI2gPLNW1\r\nZwHJ755zQQRX528xg9vfykY=\r\n-----END CERTIFICATE-----\r\n"
                )
            );

            connection.SetValidServerCertificates(new X509Certificate2Collection { cert });
            return connection;
        }

        // Set up a standard UDP connection
        public static UnityUdpClientConnection CreateUdpConnection(IPEndPoint endpoint)
        {
            return new UnityUdpClientConnection(new ConsoleLogger(), endpoint, 0);
        }

        // Prepares authentication payload for DTLS using version, platform, token and friend code
        public static byte[] BuildDataForDtlsNonceAuth(string matchmakerToken, string friendcode, GameVersion version, Platforms platform)
        {
            var writer = MessageWriter.Get(0);

            writer.Write(version.Value);
            writer.Write((byte)platform);
            writer.Write(matchmakerToken ?? string.Empty);
            writer.Write(friendcode ?? string.Empty);

            return writer.ToByteArray(false);
        }

        // Gathers all connection data into a byte array to initiate a handshake
        public static byte[] GetConnectionData(string name, string matchmakerToken, uint? authNonce, GameVersion version, PlatformSpecificData platform, string friendcode)
        {
            var writer = new MessageWriter(1000);

            writer.Write(version.Value);
            writer.Write(name);

            if (!string.IsNullOrEmpty(matchmakerToken) && matchmakerToken.Length > 16)
            {
                writer.Write(matchmakerToken);
            }
            else if (authNonce.HasValue)
            {
                writer.Write(authNonce.Value);
            }
            else
            {
                writer.Write(0U);
            }

            writer.Write((uint)GameKeywords.English);
            writer.Write((byte)QuickChatModes.FreeChatOrQuickChat);

            writer.StartMessage((byte)platform.Platform);
            writer.Write(platform.PlatformName);

            switch (platform.Platform)
            {
                case Platforms.Xbox:
                    writer.Write(platform.XboxPlatformId ?? throw new NullReferenceException("XboxPlatformId is required for Xbox"));
                    break;
                case Platforms.Playstation:
                    writer.Write(platform.PsnPlatformId ?? throw new NullReferenceException("PsnPlatformId is required for Playstation"));
                    break;
            }

            writer.EndMessage();

            if (!string.IsNullOrEmpty(matchmakerToken))
            {
                writer.Write(friendcode);
            }
            else
            {
                writer.Write(string.Empty);
                writer.Write(0U);
            }

            return writer.ToByteArray(true);
        }
    }

    public static class CryptoHelpers
    {
        // Converts PEM-encoded certificate string to raw byte array
        public static byte[] DecodePEM(string pemData)
        {
            var bytes = new List<byte>();
            pemData = pemData.Replace("\r", "");

            foreach (var line in pemData.Split('\n'))
            {
                if (!line.StartsWith("-----"))
                {
                    var segment = Convert.FromBase64String(line);
                    bytes.AddRange(segment);
                }
            }

            return bytes.ToArray();
        }
    }

    public class ConsoleLogger : Hazel.ILogger
    {
        public LogLevel FilterLevel { get; set; }

        public ConsoleLogger()
        {
#if DEBUG
            FilterLevel = LogLevel.Debug;
#else
            FilterLevel = LogLevel.Information;
#endif
        }

        public void WriteLog(LogLevel level, string message)
        {
            return;
            if (level < FilterLevel)
                return;

            // Logging output is intentionally disabled. To enable, uncomment below.
            // ConsoleColor originalColor = Console.ForegroundColor;

            switch (level)
            {
                case LogLevel.Error:
                    Console.ForegroundColor = ConsoleColor.Red;
                    break;
                case LogLevel.Warning:
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    break;
                case LogLevel.Information:
                    Console.ForegroundColor = ConsoleColor.White;
                    break;
                case LogLevel.Debug:
                    Console.ForegroundColor = ConsoleColor.Gray;
                    break;
                case LogLevel.Trace:
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    break;
            }

            // Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}");
            // Console.ForegroundColor = originalColor;
        }

        public void WriteLog(LogLevel level, string message, Exception ex)
        {
            WriteLog(level, $"{message} - Exception: {ex.Message}\n{ex.StackTrace}");
        }

        public void WriteVerbose(string msg)
        {
            WriteLog(LogLevel.Trace, msg);
        }

        public void WriteError(string msg)
        {
            WriteLog(LogLevel.Error, msg);
        }

        public void WriteWarning(string msg)
        {
            WriteLog(LogLevel.Warning, msg);
        }

        public void WriteInfo(string msg)
        {
            WriteLog(LogLevel.Information, msg);
        }
    }
}