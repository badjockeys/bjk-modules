namespace BJK.Modules.Profiles.Domain;

/// <summary>How someone mostly uses the road, if they choose to say. Stored by name.</summary>
public enum RoadUser
{
    /// <summary>Drives (car, van, motorbike).</summary>
    Driver,

    /// <summary>Cycles.</summary>
    Cyclist,

    /// <summary>Drives and cycles.</summary>
    Both,
}
