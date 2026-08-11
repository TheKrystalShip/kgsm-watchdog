using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Watchdog.PortForwarding;

/// <summary>
/// Set algebra over expanded <c>(port, protocol)</c> pairs, shared by everything that has to reason
/// about which forwards belong to whom. The UPnP paths compare, subtract and re-collapse the same port
/// sets from three places — the stop edge, the reconcile sweep, and the container lifecycle ingester —
/// and a second copy of this arithmetic is exactly how two of them drift apart.
/// <para>
/// Protocol is part of a port's identity and is compared lower-cased throughout, because the router
/// reports <c>UDP</c> while the ecosystem's canonical spelling is <c>udp</c>.
/// </para>
/// </summary>
internal static class PortSets
{
    /// <summary>
    /// The expanded pairs of <paramref name="mappings"/>, protocol-normalized — the shape every
    /// comparison here runs on.
    /// </summary>
    public static HashSet<(int Port, string Protocol)> ExpandNormalized(this IEnumerable<PortMapping> mappings)
        => [.. mappings.Expand().Select(p => (p.Port, p.Protocol.ToLowerInvariant()))];

    /// <summary>Whether <paramref name="set"/> contains this pair, comparing protocol case-insensitively.</summary>
    public static bool Holds(this IReadOnlySet<(int Port, string Protocol)> set, int port, string protocol)
        => set.Contains((port, protocol.ToLowerInvariant()));

    /// <summary>
    /// The pairs of <paramref name="ports"/> that <paramref name="retain"/> does not claim — what a close
    /// may actually release once the ports other instances still want are taken out of it. Pure, so the
    /// subtraction that decides what a stop deletes from the router is tested without shelling
    /// <c>upnpc</c>. Order follows <paramref name="ports"/>.
    /// </summary>
    public static List<(int Port, string Protocol)> Excluding(
        IEnumerable<(int Port, string Protocol)> ports, IReadOnlySet<(int Port, string Protocol)> retain)
        => [.. ports.Where(p => !retain.Holds(p.Port, p.Protocol))];

    /// <summary>
    /// Re-collapse expanded pairs into contiguous <see cref="PortMapping"/> ranges, grouped by protocol —
    /// the inverse of <c>Expand()</c>, so a set spanning a whole configured range is reported as that
    /// range rather than as N single ports. What the rest of the ecosystem carries is the collapsed
    /// shape, so anything crossing back out of this file collapses first.
    /// </summary>
    public static List<PortMapping> Collapse(IEnumerable<(int Port, string Protocol)> ports)
    {
        var result = new List<PortMapping>();
        foreach (var group in ports.GroupBy(p => p.Protocol, StringComparer.OrdinalIgnoreCase))
        {
            int[] sorted = [.. group.Select(p => p.Port).Distinct().Order()];
            for (int i = 0; i < sorted.Length;)
            {
                int start = sorted[i];
                int end = start;
                while (i + 1 < sorted.Length && sorted[i + 1] == end + 1)
                {
                    end = sorted[++i];
                }

                i++;
                result.Add(new PortMapping { Start = start, End = end, Protocol = group.Key });
            }
        }

        return result;
    }
}
