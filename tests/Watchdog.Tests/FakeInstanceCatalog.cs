using TheKrystalShip.KGSM.Watchdog.Catalog;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// A fake <see cref="IInstanceCatalog"/> answering from a map a test fills in, and counting the lookups so
/// a test can assert exactly-once resolution. Every instance is free of transfer locks unless a test
/// says otherwise.
/// </summary>
internal sealed class FakeInstanceCatalog : IInstanceCatalog
{
    private readonly Dictionary<string, InstanceSpec> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransferLockState> _locks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _blueprintRam = new(StringComparer.Ordinal);

    /// <summary>A catalog holding just this instance.</summary>
    public static FakeInstanceCatalog Of(InstanceSpec instance)
    {
        var catalog = new FakeInstanceCatalog();
        catalog.Add(instance);
        return catalog;
    }

    public int CallCount { get; private set; }

    public void Add(InstanceSpec instance) => _byName[instance.Name] = instance;

    public void Lock(string name, TransferLockState state) => _locks[name] = state;

    public void DeclareBlueprintMemory(string blueprint, int minRamMb) => _blueprintRam[blueprint] = minRamMb;

    public InstanceSpec? Find(string name)
    {
        CallCount++;
        return _byName.GetValueOrDefault(name);
    }

    public IReadOnlyList<InstanceSpec>? All() => [.. _byName.Values];

    public int? BlueprintMinRamMb(string blueprint) => _blueprintRam.TryGetValue(blueprint, out int mb) ? mb : null;

    public TransferLockState LockState(string name) => _locks.GetValueOrDefault(name, TransferLockState.Free);
}
