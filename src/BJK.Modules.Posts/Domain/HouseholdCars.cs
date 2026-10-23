namespace BJK.Modules.Posts.Domain;

/// <summary>
/// One of a member's household cars (owner, 22 Oct 2026): a make and model from <see cref="VehicleCatalogue"/>, a
/// colour and a nickname ("Dave's Golf"; "Make Model" if none is given). <b>No registration is ever stored.</b>
/// Chosen when posting, so the post shows "Filmed from: Dave's Golf". Kept in <c>posts.HouseholdCars</c>; private to
/// its owner except for the nickname on their posts.
/// </summary>
public sealed class HouseholdCar
{
    public const int NicknameMaxLength = 30;

    /// <summary>How many cars a member can keep.</summary>
    public const int MaxPerUser = 5;

    // For EF Core.
    private HouseholdCar()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Make { get; private set; } = string.Empty;

    public string? Model { get; private set; }

    public VehicleColour? Colour { get; private set; }

    public string Nickname { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static HouseholdCar Create(Guid userId, string make, string? model, VehicleColour? colour, string? nickname, DateTimeOffset now)
    {
        var car = new HouseholdCar { Id = Guid.CreateVersion7(now), UserId = userId, CreatedAt = now };
        car.Change(make, model, colour, nickname, now);
        return car;
    }

    /// <summary>Changes it. The make and model must already be the catalogue's spelling.</summary>
    public void Change(string make, string? model, VehicleColour? colour, string? nickname, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(make);
        Make = make;
        Model = string.IsNullOrWhiteSpace(model) ? null : model;
        Colour = colour;
        Nickname = DefaultNickname(make, Model, nickname);
        UpdatedAt = now;
    }

    /// <summary>The nickname given, or "Make Model" (cut to <see cref="NicknameMaxLength"/>).</summary>
    public static string DefaultNickname(string make, string? model, string? nickname)
    {
        var name = string.IsNullOrWhiteSpace(nickname) ? $"{make} {model}".Trim() : nickname.Trim();
        return name.Length <= NicknameMaxLength ? name : name[..NicknameMaxLength].TrimEnd();
    }
}
