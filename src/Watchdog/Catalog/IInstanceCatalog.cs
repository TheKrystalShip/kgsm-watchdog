using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Catalog;

/// <summary>Whether an instance is held by an instance transfer.</summary>
public enum TransferLockState
{
    /// <summary>No transfer holds the instance.</summary>
    Free,

    /// <summary>A transfer holds the instance.</summary>
    Locked,

    /// <summary>The instance is not registered or its lock cannot be read; starting it could run it in two places.</summary>
    Unreadable,
}

/// <summary>
/// The daemon's read-only view of the engine: which instances exist and what is needed to supervise each.
/// Every call reads the engine's files again, so an answer is as fresh as they are. The daemon never
/// changes engine state through it.
/// </summary>
public interface IInstanceCatalog
{
    /// <summary>The instance of that id, or null when it is not registered or its config cannot be read.</summary>
    InstanceSpec? Find(string name);

    /// <summary>Every registered instance whose config can be read; null when the registry itself cannot be read.</summary>
    IReadOnlyList<InstanceSpec>? All();

    /// <summary>The memory a blueprint declares a game needs, MB; null when the blueprint declares none or cannot be read.</summary>
    int? BlueprintMinRamMb(string blueprint);

    /// <summary>Whether an instance transfer holds the instance.</summary>
    TransferLockState LockState(string name);
}
