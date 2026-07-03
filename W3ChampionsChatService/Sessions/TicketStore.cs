using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Sessions;

public interface ITicketStore
{
    /// <summary>Mints a one-time ticket bound to the validated identity snapshot. Purges expired tickets.</summary>
    string Mint(W3CUserAuthentication identity, DateTime now);

    /// <summary>Consumes atomically: true exactly once per ticket, and only within ChatLimits.TicketTtl of mint.</summary>
    bool TryConsume(string ticket, DateTime now, out W3CUserAuthentication identity);
}

/// <summary>
/// Single-instance in-memory ticket store, by design: tickets are short-lived (ChatLimits.TicketTtl)
/// and node-local, so they never need to survive a restart or be shared across chat-service
/// instances (spec §2 state placement — no Mongo for this). Concurrency idiom mirrors
/// Chats/ConnectionMapping.cs: a private Dictionary guarded by a single lock object, with every
/// public method doing its work inside that lock.
/// </summary>
public class TicketStore : ITicketStore
{
    private readonly Dictionary<string, (W3CUserAuthentication Identity, DateTime IssuedAt)> _tickets =
        new Dictionary<string, (W3CUserAuthentication Identity, DateTime IssuedAt)>();

    private readonly object _lock = new object();

    // Purge test seam — internals visible to W3ChampionsChatService.Tests (see Chats/ChatHub.cs).
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _tickets.Count;
            }
        }
    }

    public string Mint(W3CUserAuthentication identity, DateTime now)
    {
        lock (_lock)
        {
            PurgeExpiredNoLock(now);

            var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _tickets[ticket] = (identity, now);
            return ticket;
        }
    }

    public bool TryConsume(string ticket, DateTime now, out W3CUserAuthentication identity)
    {
        lock (_lock)
        {
            // Burn on every hit — consuming, expired-or-not, always removes the ticket so it
            // can never be presented again.
            if (!_tickets.Remove(ticket, out var entry))
            {
                identity = null;
                return false;
            }

            if (now > entry.IssuedAt + ChatLimits.TicketTtl)
            {
                identity = null;
                return false;
            }

            identity = entry.Identity;
            return true;
        }
    }

    // Caller must already hold _lock.
    private void PurgeExpiredNoLock(DateTime now)
    {
        var expiredTickets = new List<string>();
        foreach (var kvp in _tickets)
        {
            if (kvp.Value.IssuedAt + ChatLimits.TicketTtl < now)
            {
                expiredTickets.Add(kvp.Key);
            }
        }

        foreach (var expiredTicket in expiredTickets)
        {
            _tickets.Remove(expiredTicket);
        }
    }
}
