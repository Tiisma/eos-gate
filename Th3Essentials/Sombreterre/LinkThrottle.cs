using System;
using System.Collections.Generic;
using System.Linq;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// How often one address may start a Discord link. A few attempts in a row are free, so a player whose
/// browser crashed or who closed the tab can simply try again. Past that, the address waits, and each
/// further wait is twice as long as the one before, up to a ceiling. An hour without any attempt, or a
/// link that succeeds, forgets the address. Addresses are only held in memory.
/// </summary>
public class LinkThrottle
{
    private class State
    {
        public readonly List<DateTime> Attempts = new();
        public DateTime BlockedUntil = DateTime.MinValue;
        public int Lockouts;
        public DateTime LastSeen;
    }

    /// <summary>Attempts closer together than this count towards the limit.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>After this long without an attempt, an address starts again from nothing.</summary>
    public static readonly TimeSpan Forget = TimeSpan.FromHours(1);

    private readonly int _attempts;
    private readonly TimeSpan _firstWait;
    private readonly TimeSpan _maxWait;
    private readonly Dictionary<string, State> _states = new();

    public LinkThrottle(int attempts, TimeSpan firstWait, TimeSpan maxWait)
    {
        _attempts = Math.Max(1, attempts);
        _firstWait = firstWait > TimeSpan.Zero ? firstWait : TimeSpan.FromMinutes(5);
        _maxWait = maxWait >= _firstWait ? maxWait : _firstWait;
    }

    /// <summary>True when this address may start a link now; otherwise how long it still has to wait.</summary>
    public bool TryStart(string address, DateTime now, out TimeSpan retryAfter)
    {
        lock (_states)
        {
            Prune(now);
            if (!_states.TryGetValue(address, out var state)) _states[address] = state = new State();
            state.LastSeen = now;

            if (state.BlockedUntil > now)
            {
                retryAfter = state.BlockedUntil - now;
                return false;
            }

            state.Attempts.RemoveAll(t => now - t > Window);
            if (state.Attempts.Count >= _attempts)
            {
                state.Lockouts++;
                var wait = TimeSpan.FromTicks(Math.Min(_maxWait.Ticks, _firstWait.Ticks * (1L << Math.Min(state.Lockouts - 1, 20))));
                state.BlockedUntil = now + wait;
                state.Attempts.Clear();
                retryAfter = wait;
                return false;
            }

            state.Attempts.Add(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>A link went through from this address: it starts again from nothing.</summary>
    public void Reset(string address)
    {
        lock (_states) _states.Remove(address);
    }

    private void Prune(DateTime now)
    {
        foreach (var key in _states.Where(s => now - s.Value.LastSeen > Forget && s.Value.BlockedUntil <= now).Select(s => s.Key).ToList())
            _states.Remove(key);
    }
}
