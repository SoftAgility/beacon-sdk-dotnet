using SoftAgility.Beacon.Internal.Compat;

namespace SoftAgility.Beacon;

/// <summary>
/// Fluent builder for declaring the complete set of events an application tracks.
/// Used during SDK configuration to register all event (category, name) pairs.
/// The registered events can be exported as a JSON manifest via
/// <see cref="IBeaconTracker.ExportEventManifest(string)"/>.
/// </summary>
public sealed class EventDefinitionBuilder
{
    private readonly HashSet<(string Category, string Name)> _events = [];

    public EventDefinitionBuilder Define(string category, string name)
    {
        Guard.NotNullOrWhiteSpace(category);
        Guard.NotNullOrWhiteSpace(name);
        _events.Add((category, name));
        return this;
    }

    internal IReadOnlyList<(string Category, string Name)> Build()
        => _events.OrderBy(e => e.Category).ThenBy(e => e.Name).ToList();
}
