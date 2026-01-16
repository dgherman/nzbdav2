using System;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Tools;

/// <summary>
/// Standalone connectivity tester for Usenet providers.
/// Tests connection stability, authentication, and throughput independent of the connection pool.
/// </summary>
public class UsenetConnectivityTester
{
    public static async Task RunAsync(string[] args)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  USENET CONNECTIVITY TESTER");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        // Parse arguments
        var argIndex = args.ToList().IndexOf("--test-usenet");
        string? host = null;
        int port = 563; // Default SSL port
        string? username = null;
        string? password = null;
        bool useSsl = true;
        int connectionCount = 5;
        int testDurationSeconds = 30;
        bool testIdle = false;
        bool testRapid = false;
        bool testSegment = false;
        string? segmentId = null;

        for (int i = argIndex + 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--host=")) host = arg.Substring(7);
            else if (arg.StartsWith("--port=")) port = int.Parse(arg.Substring(7));
            else if (arg.StartsWith("--user=")) username = arg.Substring(7);
            else if (arg.StartsWith("--pass=")) password = arg.Substring(7);
            else if (arg == "--no-ssl") useSsl = false;
            else if (arg.StartsWith("--connections=")) connectionCount = int.Parse(arg.Substring(14));
            else if (arg.StartsWith("--duration=")) testDurationSeconds = int.Parse(arg.Substring(11));
            else if (arg == "--test-idle") testIdle = true;
            else if (arg == "--test-rapid") testRapid = true;
            else if (arg == "--test-segment") testSegment = true;
            else if (arg.StartsWith("--segment=")) segmentId = arg.Substring(10);
            else if (arg == "--from-config")
            {
                // Load from config
                var configManager = new ConfigManager();
                await configManager.LoadConfig().ConfigureAwait(false);
                var providerConfig = configManager.GetUsenetProviderConfig();
                if (providerConfig.Providers.Count > 0)
                {
                    var provider = providerConfig.Providers[0];
                    host = provider.Host;
                    port = provider.Port;
                    username = provider.User;
                    password = provider.Pass;
                    useSsl = provider.UseSsl;
                    Console.WriteLine($"  Loaded config for provider: {host}");
                }
            }
            else if (arg.StartsWith("--provider="))
            {
                var providerIndex = int.Parse(arg.Substring(11));
                var configManager = new ConfigManager();
                await configManager.LoadConfig().ConfigureAwait(false);
                var providerConfig = configManager.GetUsenetProviderConfig();
                if (providerIndex < providerConfig.Providers.Count)
                {
                    var provider = providerConfig.Providers[providerIndex];
                    host = provider.Host;
                    port = provider.Port;
                    username = provider.User;
                    password = provider.Pass;
                    useSsl = provider.UseSsl;
                    Console.WriteLine($"  Loaded config for provider #{providerIndex}: {host}");
                }
            }
        }

        if (string.IsNullOrEmpty(host))
        {
            PrintUsage();
            return;
        }

        Console.WriteLine($"  Host: {host}:{port}");
        Console.WriteLine($"  SSL: {useSsl}");
        Console.WriteLine($"  Username: {(string.IsNullOrEmpty(username) ? "(none)" : username)}");
        Console.WriteLine($"  Connections: {connectionCount}");
        Console.WriteLine($"  Duration: {testDurationSeconds}s");
        Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

        // Run tests
        await TestBasicConnection(host, port, useSsl, username, password);

        if (testIdle)
        {
            await TestIdleConnectionStability(host, port, useSsl, username, password, testDurationSeconds);
        }

        if (testRapid)
        {
            await TestRapidConnectionCreation(host, port, useSsl, username, password, connectionCount);
        }

        if (testSegment && !string.IsNullOrEmpty(segmentId))
        {
            await TestSegmentFetch(host, port, useSsl, username, password, segmentId);
        }

        // Default: run connection stability test
        if (!testIdle && !testRapid && !testSegment)
        {
            await TestConnectionStability(host, port, useSsl, username, password, connectionCount, testDurationSeconds);
        }

        Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  TESTS COMPLETE");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run -- --test-usenet [options]");
        Console.WriteLine();
        Console.WriteLine("Connection Options:");
        Console.WriteLine("  --host=<hostname>       Usenet server hostname");
        Console.WriteLine("  --port=<port>           Port number (default: 563 for SSL)");
        Console.WriteLine("  --user=<username>       Username for authentication");
        Console.WriteLine("  --pass=<password>       Password for authentication");
        Console.WriteLine("  --no-ssl                Disable SSL/TLS");
        Console.WriteLine("  --from-config           Load first provider from config");
        Console.WriteLine("  --provider=<index>      Load specific provider from config (0-based)");
        Console.WriteLine();
        Console.WriteLine("Test Options:");
        Console.WriteLine("  --connections=<N>       Number of concurrent connections (default: 5)");
        Console.WriteLine("  --duration=<seconds>    Test duration (default: 30)");
        Console.WriteLine("  --test-idle             Test idle connection stability");
        Console.WriteLine("  --test-rapid            Test rapid connection creation");
        Console.WriteLine("  --test-segment          Test segment fetching");
        Console.WriteLine("  --segment=<id>          Segment ID to fetch");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- --test-usenet --from-config");
        Console.WriteLine("  dotnet run -- --test-usenet --provider=1 --test-rapid");
        Console.WriteLine("  dotnet run -- --test-usenet --host=news.example.com --user=me --pass=secret");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
    }

    private static async Task TestBasicConnection(string host, int port, bool useSsl, string? username, string? password)
    {
        Console.WriteLine("--- TEST 1: Basic Connection ---");
        var sw = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 10000;

            Console.Write("  Connecting... ");
            await client.ConnectAsync(host, port).ConfigureAwait(false);
            var connectTime = sw.ElapsedMilliseconds;
            Console.WriteLine($"OK ({connectTime}ms)");

            Stream stream = client.GetStream();

            if (useSsl)
            {
                Console.Write("  SSL Handshake... ");
                var sslStream = new SslStream(stream, false);
                await sslStream.AuthenticateAsClientAsync(host).ConfigureAwait(false);
                var sslTime = sw.ElapsedMilliseconds - connectTime;
                Console.WriteLine($"OK ({sslTime}ms) - {sslStream.SslProtocol}");
                stream = sslStream;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            // Read greeting
            Console.Write("  Reading greeting... ");
            var greeting = await reader.ReadLineAsync().ConfigureAwait(false);
            Console.WriteLine($"OK");
            Console.WriteLine($"    Response: {greeting}");

            // Authenticate if credentials provided
            if (!string.IsNullOrEmpty(username))
            {
                Console.Write("  Authenticating... ");
                await writer.WriteLineAsync($"AUTHINFO USER {username}").ConfigureAwait(false);
                var userResp = await reader.ReadLineAsync().ConfigureAwait(false);

                if (userResp?.StartsWith("381") == true && !string.IsNullOrEmpty(password))
                {
                    await writer.WriteLineAsync($"AUTHINFO PASS {password}").ConfigureAwait(false);
                    var passResp = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (passResp?.StartsWith("281") == true)
                    {
                        Console.WriteLine("OK");
                    }
                    else
                    {
                        Console.WriteLine($"FAILED: {passResp}");
                    }
                }
                else if (userResp?.StartsWith("281") == true)
                {
                    Console.WriteLine("OK (no password required)");
                }
                else
                {
                    Console.WriteLine($"FAILED: {userResp}");
                }
            }

            // Test DATE command (simple command to verify connection works)
            Console.Write("  Testing DATE command... ");
            await writer.WriteLineAsync("DATE").ConfigureAwait(false);
            var dateResp = await reader.ReadLineAsync().ConfigureAwait(false);
            Console.WriteLine($"OK - {dateResp}");

            // Quit gracefully
            await writer.WriteLineAsync("QUIT").ConfigureAwait(false);
            await reader.ReadLineAsync().ConfigureAwait(false);

            sw.Stop();
            Console.WriteLine($"  Total time: {sw.ElapsedMilliseconds}ms");
            Console.WriteLine("  Result: PASS\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED");
            Console.WriteLine($"  Error: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("  Result: FAIL\n");
        }
    }

    private static async Task TestIdleConnectionStability(string host, int port, bool useSsl, string? username, string? password, int durationSeconds)
    {
        Console.WriteLine($"--- TEST 2: Idle Connection Stability ({durationSeconds}s) ---");

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port).ConfigureAwait(false);

            Stream stream = client.GetStream();
            if (useSsl)
            {
                var sslStream = new SslStream(stream, false);
                await sslStream.AuthenticateAsClientAsync(host).ConfigureAwait(false);
                stream = sslStream;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            // Read greeting and authenticate
            await reader.ReadLineAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(username))
            {
                await writer.WriteLineAsync($"AUTHINFO USER {username}").ConfigureAwait(false);
                var resp = await reader.ReadLineAsync().ConfigureAwait(false);
                if (resp?.StartsWith("381") == true && !string.IsNullOrEmpty(password))
                {
                    await writer.WriteLineAsync($"AUTHINFO PASS {password}").ConfigureAwait(false);
                    await reader.ReadLineAsync().ConfigureAwait(false);
                }
            }

            Console.WriteLine($"  Connected. Testing idle stability for {durationSeconds} seconds...");

            var checkIntervalSeconds = Math.Max(5, durationSeconds / 10);
            var checks = 0;
            var failures = 0;
            var latencies = new List<long>();

            for (int elapsed = 0; elapsed < durationSeconds; elapsed += checkIntervalSeconds)
            {
                await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds)).ConfigureAwait(false);
                checks++;

                var sw = Stopwatch.StartNew();
                try
                {
                    await writer.WriteLineAsync("DATE").ConfigureAwait(false);
                    var resp = await reader.ReadLineAsync().ConfigureAwait(false);
                    sw.Stop();

                    if (resp?.StartsWith("111") == true)
                    {
                        latencies.Add(sw.ElapsedMilliseconds);
                        Console.WriteLine($"    [{elapsed + checkIntervalSeconds}s] DATE OK - {sw.ElapsedMilliseconds}ms");
                    }
                    else
                    {
                        failures++;
                        Console.WriteLine($"    [{elapsed + checkIntervalSeconds}s] Unexpected: {resp}");
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine($"    [{elapsed + checkIntervalSeconds}s] ERROR: {ex.GetType().Name}: {ex.Message}");
                }
            }

            await writer.WriteLineAsync("QUIT").ConfigureAwait(false);

            Console.WriteLine($"\n  Checks: {checks}, Failures: {failures}");
            if (latencies.Count > 0)
            {
                Console.WriteLine($"  Latency: avg={latencies.Average():F0}ms, min={latencies.Min()}ms, max={latencies.Max()}ms");
            }
            Console.WriteLine($"  Result: {(failures == 0 ? "PASS" : "FAIL")}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Error: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("  Result: FAIL\n");
        }
    }

    private static async Task TestRapidConnectionCreation(string host, int port, bool useSsl, string? username, string? password, int connectionCount)
    {
        Console.WriteLine($"--- TEST 3: Rapid Connection Creation ({connectionCount} connections) ---");

        var successes = 0;
        var failures = 0;
        var connectTimes = new List<long>();
        var errors = new Dictionary<string, int>();

        var sw = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, connectionCount).Select(async i =>
        {
            var connSw = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;

                await client.ConnectAsync(host, port).ConfigureAwait(false);

                Stream stream = client.GetStream();
                if (useSsl)
                {
                    var sslStream = new SslStream(stream, false);
                    await sslStream.AuthenticateAsClientAsync(host).ConfigureAwait(false);
                    stream = sslStream;
                }

                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var greeting = await reader.ReadLineAsync().ConfigureAwait(false);
                if (!greeting?.StartsWith("2") == true)
                {
                    throw new Exception($"Bad greeting: {greeting}");
                }

                if (!string.IsNullOrEmpty(username))
                {
                    await writer.WriteLineAsync($"AUTHINFO USER {username}").ConfigureAwait(false);
                    var resp = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (resp?.StartsWith("381") == true && !string.IsNullOrEmpty(password))
                    {
                        await writer.WriteLineAsync($"AUTHINFO PASS {password}").ConfigureAwait(false);
                        var passResp = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (!passResp?.StartsWith("281") == true)
                        {
                            throw new Exception($"Auth failed: {passResp}");
                        }
                    }
                }

                // Quick command to verify connection
                await writer.WriteLineAsync("DATE").ConfigureAwait(false);
                await reader.ReadLineAsync().ConfigureAwait(false);

                await writer.WriteLineAsync("QUIT").ConfigureAwait(false);

                connSw.Stop();
                lock (connectTimes)
                {
                    connectTimes.Add(connSw.ElapsedMilliseconds);
                    successes++;
                }

                Console.WriteLine($"    Connection {i + 1}: OK ({connSw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                connSw.Stop();
                lock (errors)
                {
                    failures++;
                    var key = $"{ex.GetType().Name}";
                    if (!errors.ContainsKey(key)) errors[key] = 0;
                    errors[key]++;
                }
                Console.WriteLine($"    Connection {i + 1}: FAILED - {ex.GetType().Name}: {ex.Message}");
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();

        Console.WriteLine($"\n  Total time: {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"  Successes: {successes}, Failures: {failures}");
        if (connectTimes.Count > 0)
        {
            Console.WriteLine($"  Connect time: avg={connectTimes.Average():F0}ms, min={connectTimes.Min()}ms, max={connectTimes.Max()}ms");
        }
        if (errors.Count > 0)
        {
            Console.WriteLine("  Error breakdown:");
            foreach (var kvp in errors)
            {
                Console.WriteLine($"    {kvp.Key}: {kvp.Value}");
            }
        }
        Console.WriteLine($"  Result: {(failures == 0 ? "PASS" : (failures < connectionCount / 2 ? "PARTIAL" : "FAIL"))}\n");
    }

    private static async Task TestConnectionStability(string host, int port, bool useSsl, string? username, string? password, int connectionCount, int durationSeconds)
    {
        Console.WriteLine($"--- TEST 4: Connection Stability ({connectionCount} connections, {durationSeconds}s) ---");

        var connections = new List<(TcpClient Client, StreamReader Reader, StreamWriter Writer)>();
        var successes = 0;
        var failures = 0;

        // Create connections
        Console.WriteLine("  Creating connections...");
        for (int i = 0; i < connectionCount; i++)
        {
            try
            {
                var client = new TcpClient();
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;

                await client.ConnectAsync(host, port).ConfigureAwait(false);

                Stream stream = client.GetStream();
                if (useSsl)
                {
                    var sslStream = new SslStream(stream, false);
                    await sslStream.AuthenticateAsClientAsync(host).ConfigureAwait(false);
                    stream = sslStream;
                }

                var reader = new StreamReader(stream, Encoding.UTF8);
                var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                await reader.ReadLineAsync().ConfigureAwait(false);

                if (!string.IsNullOrEmpty(username))
                {
                    await writer.WriteLineAsync($"AUTHINFO USER {username}").ConfigureAwait(false);
                    var resp = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (resp?.StartsWith("381") == true && !string.IsNullOrEmpty(password))
                    {
                        await writer.WriteLineAsync($"AUTHINFO PASS {password}").ConfigureAwait(false);
                        await reader.ReadLineAsync().ConfigureAwait(false);
                    }
                }

                connections.Add((client, reader, writer));
                Console.WriteLine($"    Connection {i + 1}: OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Connection {i + 1}: FAILED - {ex.Message}");
            }
        }

        if (connections.Count == 0)
        {
            Console.WriteLine("  No connections established. Result: FAIL\n");
            return;
        }

        Console.WriteLine($"  {connections.Count} connections established. Running stability test...\n");

        // Test all connections periodically
        var checkInterval = TimeSpan.FromSeconds(5);
        var totalChecks = durationSeconds / 5;
        var latencies = new List<long>();

        for (int check = 0; check < totalChecks; check++)
        {
            await Task.Delay(checkInterval).ConfigureAwait(false);

            var checkSuccesses = 0;
            var checkFailures = 0;
            var checkLatencies = new List<long>();

            foreach (var (client, reader, writer) in connections.ToList())
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    await writer.WriteLineAsync("DATE").ConfigureAwait(false);
                    var resp = await reader.ReadLineAsync().ConfigureAwait(false);
                    sw.Stop();

                    if (resp?.StartsWith("111") == true)
                    {
                        checkSuccesses++;
                        checkLatencies.Add(sw.ElapsedMilliseconds);
                        latencies.Add(sw.ElapsedMilliseconds);
                    }
                    else
                    {
                        checkFailures++;
                    }
                }
                catch
                {
                    checkFailures++;
                }
            }

            successes += checkSuccesses;
            failures += checkFailures;

            var avgLatency = checkLatencies.Count > 0 ? checkLatencies.Average() : 0;
            Console.WriteLine($"  [{(check + 1) * 5}s] {checkSuccesses}/{connections.Count} OK, avg latency: {avgLatency:F0}ms");
        }

        // Cleanup
        foreach (var (client, reader, writer) in connections)
        {
            try
            {
                await writer.WriteLineAsync("QUIT").ConfigureAwait(false);
                client.Dispose();
            }
            catch { }
        }

        Console.WriteLine($"\n  Total checks: {successes + failures}");
        Console.WriteLine($"  Successes: {successes}, Failures: {failures}");
        if (latencies.Count > 0)
        {
            latencies.Sort();
            var p50 = latencies[latencies.Count / 2];
            var p95 = latencies[(int)(latencies.Count * 0.95)];
            Console.WriteLine($"  Latency: avg={latencies.Average():F0}ms, p50={p50}ms, p95={p95}ms, max={latencies.Max()}ms");
        }
        Console.WriteLine($"  Result: {(failures == 0 ? "PASS" : (failures < (successes + failures) * 0.1 ? "PARTIAL" : "FAIL"))}\n");
    }

    private static async Task TestSegmentFetch(string host, int port, bool useSsl, string? username, string? password, string segmentId)
    {
        Console.WriteLine($"--- TEST 5: Segment Fetch ---");
        Console.WriteLine($"  Segment: {segmentId}");

        try
        {
            using var client = new TcpClient();
            client.ReceiveTimeout = 30000;
            client.SendTimeout = 10000;

            await client.ConnectAsync(host, port).ConfigureAwait(false);

            Stream stream = client.GetStream();
            if (useSsl)
            {
                var sslStream = new SslStream(stream, false);
                await sslStream.AuthenticateAsClientAsync(host).ConfigureAwait(false);
                stream = sslStream;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            await reader.ReadLineAsync().ConfigureAwait(false);

            if (!string.IsNullOrEmpty(username))
            {
                await writer.WriteLineAsync($"AUTHINFO USER {username}").ConfigureAwait(false);
                var resp = await reader.ReadLineAsync().ConfigureAwait(false);
                if (resp?.StartsWith("381") == true && !string.IsNullOrEmpty(password))
                {
                    await writer.WriteLineAsync($"AUTHINFO PASS {password}").ConfigureAwait(false);
                    await reader.ReadLineAsync().ConfigureAwait(false);
                }
            }

            // Fetch article
            var sw = Stopwatch.StartNew();
            await writer.WriteLineAsync($"BODY <{segmentId}>").ConfigureAwait(false);
            var bodyResp = await reader.ReadLineAsync().ConfigureAwait(false);

            if (bodyResp?.StartsWith("222") == true)
            {
                // Read body until "."
                var bodyBytes = 0L;
                var lineCount = 0;
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line == ".") break;
                    bodyBytes += line.Length;
                    lineCount++;
                }
                sw.Stop();

                Console.WriteLine($"  Status: Found");
                Console.WriteLine($"  Lines: {lineCount}");
                Console.WriteLine($"  Size: {bodyBytes / 1024.0:F1} KB");
                Console.WriteLine($"  Time: {sw.ElapsedMilliseconds}ms");
                Console.WriteLine($"  Speed: {bodyBytes / 1024.0 / (sw.ElapsedMilliseconds / 1000.0):F1} KB/s");
                Console.WriteLine("  Result: PASS\n");
            }
            else if (bodyResp?.StartsWith("430") == true)
            {
                Console.WriteLine($"  Status: Not Found (430)");
                Console.WriteLine("  Result: ARTICLE NOT FOUND\n");
            }
            else
            {
                Console.WriteLine($"  Status: Unexpected response: {bodyResp}");
                Console.WriteLine("  Result: FAIL\n");
            }

            await writer.WriteLineAsync("QUIT").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Error: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("  Result: FAIL\n");
        }
    }
}
