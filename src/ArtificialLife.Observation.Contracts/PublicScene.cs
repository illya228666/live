namespace ArtificialLife.Observation;

/// <summary>Public rendering data. Internal components and control capabilities stay in the local observation protocol.</summary>
public sealed record PublicScene(int SchemaVersion, DateTimeOffset ObservedAt, string Status,
    string? SessionId, string Epoch, long Sequence, long Age, long Revision, double Width, double Height,
    PublicEntity[] Entities);

public sealed record PublicEntity(string Id, double X, double Y, string Kind, double? HeatRadius, PublicAppearance[] Appearance);

public sealed record PublicAppearance(string Key, string? Sprite, string Color, int Layer,
    double Rotation, double Scale, double Alpha, bool Visible);
