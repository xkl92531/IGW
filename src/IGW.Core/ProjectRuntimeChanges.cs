using IGW.Abstractions;

namespace IGW.Core;

[Flags]
public enum NorthboundComponents
{
    None = 0, Alarms = 1, Computed = 2, Scheduler = 4, History = 8, OpcUa = 16, Mqtt = 32, Mes = 64,
    All = Alarms | Computed | Scheduler | History | OpcUa | Mqtt | Mes,
}

public sealed record ProjectRuntimeChanges(bool Southbound, NorthboundComponents Northbound)
{
    public static ProjectRuntimeChanges Between(IgwProject before, IgwProject after)
    {
        bool south = JsonHash.OfRuntime(before.Templates) != JsonHash.OfRuntime(after.Templates)
                     || JsonHash.OfRuntimeChannels(before.Channels) != JsonHash.OfRuntimeChannels(after.Channels);
        // A changed point definition/handle source requires rebuilding its consumers.
        if (south || JsonHash.Of(before.ComputedTags) != JsonHash.Of(after.ComputedTags))
            return new(south, NorthboundComponents.All);
        NorthboundComponents components = NorthboundComponents.None;
        if (JsonHash.Of(before.MesUpload) != JsonHash.Of(after.MesUpload)) components |= NorthboundComponents.Mes;
        if (JsonHash.Of(before.OpcUa) != JsonHash.Of(after.OpcUa)) components |= NorthboundComponents.OpcUa;
        if (JsonHash.Of(before.History) != JsonHash.Of(after.History)) components |= NorthboundComponents.History;
        if (JsonHash.Of(before.MqttPublish) != JsonHash.Of(after.MqttPublish)) components |= NorthboundComponents.Mqtt;
        if (JsonHash.Of(before.Schedules) != JsonHash.Of(after.Schedules)) components |= NorthboundComponents.Scheduler;
        return new(false, components);
    }
}
