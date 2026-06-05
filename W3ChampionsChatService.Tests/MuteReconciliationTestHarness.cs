using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using W3ChampionsChatService.Chats;
using W3ChampionsChatService.Mutes;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Test harness around <see cref="MuteReconciliationService"/>. It wires a mock
/// <see cref="IHubContext{ChatHub}"/> over the supplied <see cref="ConnectionMapping"/> and
/// captures every <c>SendAsync</c> made to a per-connection client proxy, keyed by connectionId,
/// so a test can assert which connection received which signal (and its payload).
/// </summary>
public sealed class MuteReconciliationTestHarness
{
    public MuteReconciliationService Service { get; }

    // connectionId -> list of (method, firstArg) sent to that connection's client proxy.
    private readonly Dictionary<string, List<(string Method, object Payload)>> _sends = new();

    public MuteReconciliationTestHarness(ConnectionMapping connections, IMuteRepository muteRepository = null)
    {
        var hubClients = new Mock<IHubClients>();
        hubClients
            .Setup(c => c.Client(It.IsAny<string>()))
            .Returns<string>(connId =>
            {
                var proxy = new Mock<ISingleClientProxy>();
                proxy
                    .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                    .Callback<string, object[], CancellationToken>((method, args, _) =>
                    {
                        if (!_sends.TryGetValue(connId, out var list))
                        {
                            list = new List<(string, object)>();
                            _sends[connId] = list;
                        }
                        list.Add((method, args.Length > 0 ? args[0] : null));
                    })
                    .Returns(Task.CompletedTask);
                return proxy.Object;
            });

        var hubContext = new Mock<IHubContext<ChatHub>>();
        hubContext.Setup(h => h.Clients).Returns(hubClients.Object);

        // Default to a no-op mock repo so existing call sites that don't exercise ApplyBanAsync compile
        // unchanged; tests that assert persistence pass a real MuteRepository.
        Service = new MuteReconciliationService(
            connections,
            hubContext.Object,
            muteRepository ?? new Mock<IMuteRepository>().Object);
    }

    /// <summary>All (method, payload) signals sent to <paramref name="connectionId"/>, in order.</summary>
    public IReadOnlyList<(string Method, object Payload)> SignalsFor(string connectionId) =>
        _sends.TryGetValue(connectionId, out var list) ? list : Array.Empty<(string, object)>();

    /// <summary>The first payload sent to <paramref name="connectionId"/> for <paramref name="method"/>, or null.</summary>
    public object PayloadFor(string connectionId, string method)
    {
        if (_sends.TryGetValue(connectionId, out var list))
        {
            foreach (var (m, payload) in list)
            {
                if (m == method) return payload;
            }
        }
        return null;
    }

    public int SignalCount(string connectionId, string method)
    {
        var count = 0;
        if (_sends.TryGetValue(connectionId, out var list))
        {
            foreach (var (m, _) in list)
            {
                if (m == method) count++;
            }
        }
        return count;
    }
}
