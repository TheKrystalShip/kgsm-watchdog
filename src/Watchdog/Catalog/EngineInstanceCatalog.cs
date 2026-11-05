using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Engine;
using TheKrystalShip.KGSM.Engine.Instances;
using TheKrystalShip.KGSM.Engine.Operations;
using TheKrystalShip.KGSM.Engine.Reading;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Catalog;

/// <summary>The catalog over the engine core's in-process reads.</summary>
internal sealed class EngineInstanceCatalog(EngineReader engine, ILogger<EngineInstanceCatalog> logger) : IInstanceCatalog
{
    public InstanceSpec? Find(string name)
    {
        InstanceDescription? description = Describe(name);
        if (description is null) return null;

        InstanceSpec? spec = InstanceSpec.From(description);
        if (spec is null)
            logger.LogWarning("instance {Instance} has no readable config: {Problem}", name, description.ConfigProblem);
        return spec;
    }

    public IReadOnlyList<InstanceSpec>? All()
    {
        try
        {
            return [.. engine.Instances().Select(InstanceSpec.From).OfType<InstanceSpec>()];
        }
        catch (EngineException ex)
        {
            logger.LogWarning(ex, "the instance registry cannot be read");
            return null;
        }
    }

    public int? BlueprintMinRamMb(string blueprint) =>
        engine.ReadBlueprint(blueprint)?.Blueprint?.Metadata.MinRamMb;

    public TransferLockState LockState(string name)
    {
        InstanceDescription? description = Describe(name);
        if (description is null) return TransferLockState.Unreadable;

        return TransferLock.Holder(description.Layout) is null ? TransferLockState.Free : TransferLockState.Locked;
    }

    /// <summary>The instance with exactly this id: a display name that happens to match is not the instance asked for.</summary>
    private InstanceDescription? Describe(string name)
    {
        try
        {
            InstanceDescription? description = engine.FindInstance(name);
            return description is not null && string.Equals(description.Id, name, StringComparison.Ordinal) ? description : null;
        }
        catch (EngineException ex)
        {
            logger.LogWarning(ex, "instance {Instance} cannot be read", name);
            return null;
        }
    }
}
