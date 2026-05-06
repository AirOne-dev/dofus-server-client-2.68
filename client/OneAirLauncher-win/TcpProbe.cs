// Sonde TCP non-bloquante (équivalent NWConnection macOS).

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OneAirLauncher;

public static class TcpProbe
{
    public static async Task<(bool ok, int ms, string? error)> TestAsync(
        string host, int port, int timeoutMs = 3000)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0)
            return (false, 0, "host/port invalide");

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var start = Environment.TickCount;
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            return (true, Environment.TickCount - start, null);
        }
        catch (OperationCanceledException)
        {
            return (false, 0, "Timeout");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }
}
