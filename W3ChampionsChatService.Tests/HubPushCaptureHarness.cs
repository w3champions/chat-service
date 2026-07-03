using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using W3ChampionsChatService.Chats;

namespace W3ChampionsChatService.Tests;

/// <summary>
/// Reusable fan-out push capture harness — a generalization of
/// <see cref="MuteReconciliationTestHarness"/> that is NOT tied to any single service. It wires a
/// mock <see cref="IHubContext{ChatHub}"/> whose per-connection client proxy records every
/// <c>SendAsync</c>/<c>SendCoreAsync</c> call as an ordered <c>(connectionId, method, payload)</c>
/// tuple, then exposes <see cref="HubContext"/> so a test can inject it into WHATEVER fan-out
/// service is under test (e.g. the C3 timer-driven services in tasks 13, 14, 15).
/// <para>
/// <see cref="MuteReconciliationTestHarness"/> is left untouched — legacy tests keep constructing it
/// directly — this harness is purely additive.
/// </para>
/// </summary>
public sealed class HubPushCaptureHarness
{
    private readonly Mock<IHubContext<ChatHub>> _hubContextMock;

    // Ordered across ALL connections, in the order SendCoreAsync was invoked — lets a test assert
    // cross-connection fan-out ordering, not just per-connection ordering.
    // Guarded by lock (_sends) — this harness is deliberately hit by fan-out under parallel
    // connections in later tasks (13, 14, 15), so every read/write must be synchronized.
    private readonly List<(string ConnectionId, string Method, object Payload)> _sends = new();

    public HubPushCaptureHarness()
    {
        var hubClients = new Mock<IHubClients>();
        hubClients
            .Setup(c => c.Client(It.IsAny<string>()))
            .Returns<string>(connectionId =>
            {
                var proxy = new Mock<ISingleClientProxy>();
                proxy
                    .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                    .Callback<string, object[], CancellationToken>((method, args, _) =>
                    {
                        lock (_sends)
                        {
                            _sends.Add((connectionId, method, args.Length > 0 ? args[0] : null));
                        }
                    })
                    .Returns(Task.CompletedTask);
                return proxy.Object;
            });

        _hubContextMock = new Mock<IHubContext<ChatHub>>();
        _hubContextMock.Setup(h => h.Clients).Returns(hubClients.Object);
    }

    /// <summary>The mock <see cref="IHubContext{ChatHub}"/> to inject into the service under test.</summary>
    public IHubContext<ChatHub> HubContext => _hubContextMock.Object;

    /// <summary>Every <c>(connectionId, method, payload)</c> signal sent, in send order, across ALL connections.</summary>
    public IReadOnlyList<(string ConnectionId, string Method, object Payload)> AllSignals
    {
        get
        {
            lock (_sends)
            {
                return _sends.ToList();
            }
        }
    }

    /// <summary>All (method, payload) signals sent to <paramref name="connectionId"/>, in order.</summary>
    public IReadOnlyList<(string Method, object Payload)> SignalsFor(string connectionId)
    {
        List<(string ConnectionId, string Method, object Payload)> snapshot;
        lock (_sends)
        {
            snapshot = _sends.ToList();
        }

        return snapshot
            .Where(s => s.ConnectionId == connectionId)
            .Select(s => (s.Method, s.Payload))
            .ToList();
    }

    /// <summary>The first payload sent to <paramref name="connectionId"/> for <paramref name="method"/>, or null.</summary>
    public object PayloadFor(string connectionId, string method)
    {
        List<(string ConnectionId, string Method, object Payload)> snapshot;
        lock (_sends)
        {
            snapshot = _sends.ToList();
        }

        return snapshot
            .Where(s => s.ConnectionId == connectionId && s.Method == method)
            .Select(s => s.Payload)
            .FirstOrDefault();
    }

    /// <summary>How many times <paramref name="method"/> was sent to <paramref name="connectionId"/>.</summary>
    public int SignalCount(string connectionId, string method)
    {
        List<(string ConnectionId, string Method, object Payload)> snapshot;
        lock (_sends)
        {
            snapshot = _sends.ToList();
        }

        return snapshot.Count(s => s.ConnectionId == connectionId && s.Method == method);
    }
}
