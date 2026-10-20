namespace BJK.Modules.Posts.Domain;

/// <summary>The kind of vehicle that was driven badly. Stored by name. Required once any vehicle detail is given.</summary>
public enum VehicleType
{
    Car,
    Van,
    Lorry,
    Bus,
    Coach,
    Taxi,
    Motorbike,
    Bicycle,
    EScooter,
    Tractor,
    Other,
}

/// <summary>The vehicle's main colour. Stored by name.</summary>
public enum VehicleColour
{
    Black,
    White,
    Silver,
    Grey,
    Blue,
    Red,
    Green,
    Yellow,
    Orange,
    Brown,
    Beige,
    Purple,
    Gold,
    Other,
}

/// <summary>
/// The makes and models a post can name: chosen from lists, never typed, so nobody can slip a number plate or a
/// name in. Each make also allows <see cref="Other"/>. Names are matched in any case and stored as listed here.
/// </summary>
public static class VehicleCatalogue
{
    public const string Other = "Other";

    public const int NameMaxLength = 40;

    /// <summary>Makes (A to Z, then Other) and their models (most common first, then Other).</summary>
    public static readonly IReadOnlyList<(string Make, IReadOnlyList<string> Models)> Makes = Build(
        ("Audi", ["A1", "A3", "A4", "A5", "A6", "Q2", "Q3", "Q5", "Q7", "TT", "e-tron"]),
        ("BMW", ["1 Series", "2 Series", "3 Series", "4 Series", "5 Series", "X1", "X3", "X5", "i3", "i4", "iX"]),
        ("BYD", ["Atto 3", "Dolphin", "Seal"]),
        ("Citroën", ["C1", "C3", "C4", "C5 Aircross", "Berlingo", "Dispatch", "Relay"]),
        ("Cupra", ["Born", "Formentor", "Leon"]),
        ("DAF", ["LF", "CF", "XF"]),
        ("Dacia", ["Sandero", "Duster", "Jogger"]),
        ("DS", ["DS 3", "DS 4", "DS 7"]),
        ("Ducati", ["Monster", "Panigale", "Multistrada", "Scrambler"]),
        ("Fiat", ["500", "Panda", "Tipo", "Doblo", "Ducato"]),
        ("Ford", ["Fiesta", "Focus", "Puma", "Kuga", "Mondeo", "Mustang Mach-E", "Ranger", "Transit", "Transit Custom", "Transit Connect"]),
        ("Harley-Davidson", ["Sportster", "Softail", "Touring"]),
        ("Honda", ["Jazz", "Civic", "HR-V", "CR-V", "CBR", "CB", "Africa Twin", "PCX"]),
        ("Hyundai", ["i10", "i20", "i30", "Kona", "Tucson", "Ioniq 5"]),
        ("Isuzu", ["D-Max"]),
        ("Iveco", ["Daily", "Eurocargo"]),
        ("Jaguar", ["XE", "XF", "F-Pace", "E-Pace", "I-Pace"]),
        ("Jeep", ["Renegade", "Compass", "Wrangler", "Avenger"]),
        ("Kawasaki", ["Ninja", "Z900", "Versys"]),
        ("Kia", ["Picanto", "Rio", "Ceed", "Niro", "Sportage", "Sorento", "EV6"]),
        ("Land Rover", ["Defender", "Discovery", "Discovery Sport", "Range Rover", "Range Rover Sport", "Range Rover Evoque", "Range Rover Velar"]),
        ("LEVC", ["TX"]),
        ("Lexus", ["UX", "NX", "RX", "IS"]),
        ("MAN", ["TGE", "TGL", "TGM", "TGX"]),
        ("Mazda", ["2", "3", "6", "CX-3", "CX-30", "CX-5", "MX-5"]),
        ("Mercedes-Benz", ["A-Class", "B-Class", "C-Class", "E-Class", "S-Class", "GLA", "GLC", "GLE", "Sprinter", "Vito", "Citan", "Actros", "Atego"]),
        ("MG", ["MG3", "MG4", "MG5", "ZS", "HS"]),
        ("Mini", ["Hatch", "Clubman", "Countryman"]),
        ("Mitsubishi", ["Outlander", "L200", "ASX"]),
        ("Nissan", ["Micra", "Juke", "Qashqai", "X-Trail", "Leaf", "Navara"]),
        ("Peugeot", ["108", "208", "308", "2008", "3008", "5008", "Partner", "Expert", "Boxer"]),
        ("Polestar", ["2", "3", "4"]),
        ("Porsche", ["911", "Cayenne", "Macan", "Taycan"]),
        ("Renault", ["Clio", "Captur", "Megane", "Kadjar", "Zoe", "Trafic", "Master", "Kangoo"]),
        ("Scania", ["P Series", "R Series", "S Series"]),
        ("Seat", ["Ibiza", "Leon", "Arona", "Ateca"]),
        ("Škoda", ["Fabia", "Octavia", "Superb", "Kamiq", "Karoq", "Kodiaq", "Enyaq"]),
        ("Smart", ["ForTwo", "#1"]),
        ("Subaru", ["Impreza", "Forester", "Outback"]),
        ("Suzuki", ["Swift", "Ignis", "Vitara", "S-Cross", "GSX-R", "V-Strom"]),
        ("Tesla", ["Model 3", "Model Y", "Model S", "Model X"]),
        ("Toyota", ["Aygo", "Yaris", "Corolla", "C-HR", "RAV4", "Prius", "Hilux", "Proace"]),
        ("Triumph", ["Street Triple", "Speed Triple", "Bonneville", "Tiger"]),
        ("Vauxhall", ["Corsa", "Astra", "Mokka", "Grandland", "Crossland", "Insignia", "Vivaro", "Movano", "Combo"]),
        ("Volkswagen", ["Up!", "Polo", "Golf", "T-Roc", "T-Cross", "Tiguan", "Passat", "Touareg", "ID.3", "ID.4", "Caddy", "Transporter", "Crafter", "Amarok"]),
        ("Volvo", ["XC40", "XC60", "XC90", "V40", "V60", "V90", "S60", "FH", "FM"]),
        ("Yamaha", ["MT-07", "MT-09", "R1", "Tracer", "NMAX"]));

    private static readonly Dictionary<string, (string Make, Dictionary<string, string> Models)> ByName =
        Makes.ToDictionary(
            m => m.Make,
            m => (m.Make, m.Models.ToDictionary(x => x, x => x, StringComparer.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>The make as listed, or null if it isn't one.</summary>
    public static string? Make(string? make) =>
        make is not null && ByName.TryGetValue(make.Trim(), out var found) ? found.Make : null;

    /// <summary>The model as listed for the make, or null if it isn't one of its models.</summary>
    public static string? Model(string? make, string? model) =>
        make is not null && model is not null && ByName.TryGetValue(make.Trim(), out var found)
            && found.Models.TryGetValue(model.Trim(), out var listed)
            ? listed
            : null;

    private static IReadOnlyList<(string Make, IReadOnlyList<string> Models)> Build(params (string Make, string[] Models)[] makes) =>
        makes
            .Select(m => (m.Make, (IReadOnlyList<string>)m.Models.Append(Other).ToList()))
            .Append((Other, (IReadOnlyList<string>)new List<string> { Other }))
            .ToList();
}

/// <summary>Whether it was light, from the sun's position at the incident's time and place.</summary>
public static class Daylight
{
    /// <summary>
    /// True if the sun was below the horizon (between sunset and sunrise, as the Highway Code's lighting-up times):
    /// solar elevation under −0.833°. Accurate to a few minutes, which is plenty for a filter.
    /// </summary>
    public static bool IsDark(DateTimeOffset at, decimal latitude, decimal longitude) =>
        SolarElevation(at, (double)latitude, (double)longitude) < -0.833;

    /// <summary>The sun's elevation in degrees (a low-precision solar position).</summary>
    public static double SolarElevation(DateTimeOffset at, double latitude, double longitude)
    {
        const double Rad = Math.PI / 180;
        var n = (at.ToUnixTimeMilliseconds() / 86_400_000.0) + 2_440_587.5 - 2_451_545.0;
        var meanLongitude = Normalise(280.460 + (0.9856474 * n));
        var anomaly = Normalise(357.528 + (0.9856003 * n)) * Rad;
        var eclipticLongitude = (meanLongitude + (1.915 * Math.Sin(anomaly)) + (0.020 * Math.Sin(2 * anomaly))) * Rad;
        var obliquity = (23.439 - (0.0000004 * n)) * Rad;
        var rightAscension = Math.Atan2(Math.Cos(obliquity) * Math.Sin(eclipticLongitude), Math.Cos(eclipticLongitude)) / Rad;
        var declination = Math.Asin(Math.Sin(obliquity) * Math.Sin(eclipticLongitude));
        var siderealHours = (18.697374558 + (24.06570982441908 * n)) % 24;
        var hourAngle = Normalise((siderealHours * 15) + longitude - rightAscension) * Rad;
        var lat = latitude * Rad;
        var elevation = Math.Asin((Math.Sin(lat) * Math.Sin(declination)) + (Math.Cos(lat) * Math.Cos(declination) * Math.Cos(hourAngle)));
        return elevation / Rad;
    }

    private static double Normalise(double degrees) => ((degrees % 360) + 360) % 360;
}
