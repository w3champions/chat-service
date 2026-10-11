using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Serilog;
using W3ChampionsChatService.Authentication;
using W3ChampionsChatService.Domain;

namespace W3ChampionsChatService.Sessions;

public interface ITicketStore
{
    /// <summary>Mints a ticket bound to the validated identity snapshot. Purges expired tickets.</summary>
    string Mint(W3CUserAuthentication identity, DateTime now);

    /// <summary>
    /// Redeems a ticket for one hub connection. Succeeds only within <see cref="ChatLimits.TicketTtl"/>
    /// of mint (absolute, never extended), for at most <see cref="ChatLimits.TicketMaxRedemptions"/>
    /// presentations, and only while no other connection holds the ticket. A presentation while the
    /// ticket is held revokes it.
    /// </summary>
    bool TryRedeem(string ticket, string connectionId, DateTime now, out W3CUserAuthentication identity);

    /// <summary>Frees the ticket <paramref name="connectionId"/> holds, if any, once that connection has
    /// closed, so a reconnect can redeem it again.</summary>
    void Release(string connectionId);
}

/// <summary>
/// Single-instance in-memory ticket store, by design: tickets are short-lived (ChatLimits.TicketTtl)
/// and node-local, so they never need to survive a restart or be shared across chat-service
/// instances (spec §2 state placement — no Mongo for this). Concurrency idiom mirrors
/// Chats/ConnectionMapping.cs: a private Dictionary guarded by a single lock object, with every
/// public method doing its work inside that lock. website-backend's Sessions/TicketStore.cs mirrors
/// this store and its redemption rules.
///
/// <para>Redemption rules. A ticket lets the launcher reconnect several times inside its TTL without
/// minting again, because it cannot tell whether a failed attempt reached the hub. It still never
/// behaves like a long-lived bearer:</para>
/// <list type="bullet">
/// <item>The TTL runs from mint and is never extended by a redemption.</item>
/// <item>Each presentation counts. After <see cref="ChatLimits.TicketMaxRedemptions"/>
/// presentations the ticket is gone.</item>
/// <item>At most one connection holds a ticket. Presenting a ticket while a connection holds it is
/// treated as a replay: the presentation fails and the ticket is revoked. The holder keeps its
/// connection.</item>
/// <item>An unknown, expired, revoked or exhausted ticket fails the same way, so a client can't
/// tell the cases apart.</item>
/// </list>
/// <para>Unlike a single-use ticket, a released ticket is still live until its TTL ends, so a copy
/// of the hub URL is worth something for that long. That is acceptable only while nothing logs the
/// hub URL's query string: keep proxy access logs off (or query-free) for the hub paths.</para>
/// </summary>
public class TicketStore : ITicketStore
{
    private sealed class Entry(W3CUserAuthentication identity, DateTime issuedAt)
    {
        public W3CUserAuthentication Identity { get; } = identity;
        public DateTime IssuedAt { get; } = issuedAt;
        public int Presentations { get; set; }
        public string HolderConnectionId { get; set; }
    }

    private readonly Dictionary<string, Entry> _tickets = new Dictionary<string, Entry>();

    // Holding connection id → ticket, so the hub can release by connection on disconnect.
    private readonly Dictionary<string, string> _ticketByHolder = new Dictionary<string, string>();

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
            _tickets[ticket] = new Entry(identity, now);
            return ticket;
        }
    }

    public bool TryRedeem(string ticket, string connectionId, DateTime now, out W3CUserAuthentication identity)
    {
        identity = null;
        if (string.IsNullOrEmpty(ticket) || string.IsNullOrEmpty(connectionId))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_tickets.TryGetValue(ticket, out var entry))
            {
                return false;
            }

            entry.Presentations += 1;

            var expired = now > entry.IssuedAt + ChatLimits.TicketTtl;
            var exhausted = entry.Presentations > ChatLimits.TicketMaxRedemptions;
            var held = entry.HolderConnectionId != null;
            if (expired || exhausted || held)
            {
                // Burn: an expired or exhausted ticket can never succeed again, and a presentation
                // while another connection holds the ticket is a replay, so revoke it.
                if (held && !expired && !exhausted)
                {
                    // Usually a reconnect racing the server noticing its old connection closed, but
                    // also what a stolen ticket looks like. Logged (never the ticket) so abuse shows up.
                    Log.Information("Ticket for {BattleTag} presented by {ConnectionId} while held by {HolderConnectionId}; revoked",
                        entry.Identity?.BattleTag, connectionId, entry.HolderConnectionId);
                }
                RemoveNoLock(ticket, entry);
                return false;
            }

            entry.HolderConnectionId = connectionId;
            _ticketByHolder[connectionId] = ticket;
            identity = entry.Identity;
            return true;
        }
    }

    public void Release(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId))
        {
            return;
        }

        lock (_lock)
        {
            if (_ticketByHolder.Remove(connectionId, out var ticket)
                && _tickets.TryGetValue(ticket, out var entry)
                && entry.HolderConnectionId == connectionId)
            {
                entry.HolderConnectionId = null;
            }
        }
    }

    // Caller must already hold _lock.
    private void RemoveNoLock(string ticket, Entry entry)
    {
        _tickets.Remove(ticket);
        if (entry.HolderConnectionId != null
            && _ticketByHolder.TryGetValue(entry.HolderConnectionId, out var held)
            && held == ticket)
        {
            _ticketByHolder.Remove(entry.HolderConnectionId);
        }
    }

    // Caller must already hold _lock.
    private void PurgeExpiredNoLock(DateTime now)
    {
        var expiredTickets = new List<KeyValuePair<string, Entry>>();
        foreach (var kvp in _tickets)
        {
            if (kvp.Value.IssuedAt + ChatLimits.TicketTtl < now)
            {
                expiredTickets.Add(kvp);
            }
        }

        foreach (var expired in expiredTickets)
        {
            RemoveNoLock(expired.Key, expired.Value);
        }
    }
}
