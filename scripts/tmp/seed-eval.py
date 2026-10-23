#!/usr/bin/env python3
"""TMP (CPU evaluation only, never merged): writes eval-seed.sql, a database shaped like reporting.Incidents and
reporting.Subcategories (as tables, like ChatSqlServerTests), the read-only bjk_chat login, and about 2,500 incidents
over the last two years around the beta area, so the question set has realistic data to query."""
import datetime as dt
import json
import os
import random
import sys

random.seed(2026)
here = os.path.dirname(os.path.abspath(__file__))
subs = json.load(open(os.path.join(here, "subcategories.json")))  # [position, name, label, group, groupLabel]
password = sys.argv[1]
out = sys.argv[2]

# Town, centre, weight, roads. Place names end in the town, as the app's do.
towns = [
    ("Prescot", 53.429, -2.800, 14, ["Warrington Road", "Eccleston Street", "Kemble Street", "Manchester Road", "Atherton Street"]),
    ("Whiston", 53.414, -2.797, 6, ["Lickers Lane", "Dragon Lane", "Old Colliery Road"]),
    ("Huyton", 53.411, -2.842, 9, ["Huyton Lane", "Archway Road", "Bluebell Lane", "Seel Road"]),
    ("Rainhill", 53.414, -2.762, 5, ["Warrington Road", "View Road", "Kendricks Fold"]),
    ("Knowsley", 53.450, -2.850, 4, ["Knowsley Lane", "Coach Road"]),
    ("Kirkby", 53.482, -2.892, 6, ["County Road", "Bewley Drive", "Old Rough Lane"]),
    ("St Helens", 53.453, -2.737, 11, ["Corporation Street", "Westfield Street", "Prescot Road", "Liverpool Road", "Linkway"]),
    ("Liverpool", 53.408, -2.991, 16, ["Lime Street", "Scotland Road", "Queens Drive", "Edge Lane", "Smithdown Road", "Strand Street"]),
    ("Widnes", 53.362, -2.733, 5, ["Ashley Way", "Widnes Road", "Kingsway"]),
    ("Warrington", 53.390, -2.597, 10, ["Winwick Road", "A57 Liverpool Road", "Bridge Street", "Kingsway North"]),
    ("Wigan", 53.545, -2.632, 4, ["Wallgate", "Ormskirk Road"]),
    ("Manchester", 53.481, -2.242, 6, ["Deansgate", "Oxford Road", "Princess Parkway"]),
    ("Leeds", 53.800, -1.549, 2, ["Headrow", "Kirkstall Road"]),
    ("London", 51.507, -0.128, 2, ["Euston Road", "Old Kent Road"]),
]
motorways = [("M62 J7, Rainhill", 53.417, -2.733), ("M57 J2, Prescot", 53.432, -2.861), ("M6 J21a, Warrington", 53.430, -2.570)]

# Some subcategories are much more common, as on a real dashcam site.
weight = {
    "PavementParking": 9, "DoubleYellows": 6, "DisabledBay": 4, "SchoolZigZags": 4, "BlockingDriveway": 4, "AcrossTwoBays": 3,
    "SpeedingInTown": 8, "SpeedingSchool": 3, "PhoneInHand": 7, "Tailgating": 8, "ClosePassCyclist": 6, "PulledOut": 6,
    "RanRedLight": 6, "MiddleLaneHogging": 4, "WrongLaneRoundabout": 4, "NoSignal": 5, "OvertakeSolidWhite": 3,
    "ShoutingOrGestures": 3, "NearMiss": 4, "BlockingYellowBox": 3, "Undertaking": 3, "BrakeTesting": 2,
}
names = [s[1] for s in subs]
weights = [weight.get(n, 1) for n in names]
by_name = {s[1]: s for s in subs}
parking = {s[1] for s in subs if s[3] == "Parking"}

vehicles = ["Car"] * 12 + ["Van"] * 3 + ["SUV"] * 3 + ["Taxi", "Bus", "Lorry", "Motorbike", "Pickup"] + [None] * 4
makes = {
    "Car": [("Ford", ["Fiesta", "Focus", "Puma"]), ("Vauxhall", ["Corsa", "Astra"]), ("BMW", ["1 Series", "3 Series", "X5"]),
            ("Audi", ["A3", "A4", "Q5"]), ("Volkswagen", ["Golf", "Polo"]), ("Toyota", ["Yaris", "Corolla"]),
            ("Mercedes-Benz", ["A-Class", "C-Class"]), ("Nissan", ["Qashqai", "Juke"]), ("Tesla", ["Model 3", "Model Y"])],
    "SUV": [("Range Rover", ["Evoque", "Sport"]), ("Kia", ["Sportage"]), ("Nissan", ["Qashqai"])],
    "Van": [("Ford", ["Transit"]), ("Mercedes-Benz", ["Sprinter"]), ("Vauxhall", ["Vivaro"])],
    "Taxi": [("Toyota", ["Prius"]), ("LEVC", ["TX"])],
    "Bus": [("Alexander Dennis", ["Enviro400"])],
    "Lorry": [("DAF", ["XF"]), ("Scania", ["R-series"])],
    "Motorbike": [("Honda", ["CBR600RR"]), ("Yamaha", ["MT-07"])],
    "Pickup": [("Ford", ["Ranger"]), ("Toyota", ["Hilux"])],
}
colours = ["Black", "White", "Silver", "Grey", "Blue", "Red", "Green", None]

today = dt.date.today()
rows = []
town_weights = [t[3] for t in towns]
for i in range(2500):
    # More recent incidents than old ones (the site is growing).
    days_ago = int(random.triangular(0, 730, 0))
    date = today - dt.timedelta(days=days_ago)
    hour = random.choices(range(24), weights=[1, 1, 1, 1, 1, 2, 4, 8, 12, 7, 5, 5, 6, 5, 6, 9, 12, 12, 9, 6, 4, 3, 2, 1])[0]
    main = random.choices(names, weights=weights)[0]
    if random.random() < 0.04 and main not in parking:
        place, lat, lng = random.choice(motorways)
    else:
        town = random.choices(towns, weights=town_weights)[0]
        place = f"{random.choice(town[4])}, {town[0]}"
        lat = town[1] + random.uniform(-0.02, 0.02)
        lng = town[2] + random.uniform(-0.03, 0.03)
    extra = set()
    if random.random() < 0.3:
        group = by_name[main][3]
        extra.add(random.choice([s[1] for s in subs if s[3] == group and s[1] != main] or [main]))
    if random.random() < 0.1:
        extra.add(random.choices(names, weights=weights)[0])
    extra.discard(main)
    vtype = random.choice(vehicles)
    make = model = None
    if vtype and random.random() < 0.8:
        make, models = random.choice(makes[vtype])
        model = random.choice(models) if random.random() < 0.7 else None
    colour = random.choice(colours) if vtype else None
    dark = 1 if hour < 7 or hour >= 19 or (date.month in (11, 12, 1, 2) and hour >= 16) else 0
    police = 1 if random.random() < 0.18 else 0
    video = 0 if main in parking and random.random() < 0.7 else 1
    reactions, comments = random.randint(0, 60), random.randint(0, 15)
    views = random.randint(10, 900)
    local = random.randint(0, views // 3)
    for sub, is_main in [(main, 1)] + [(e, 0) for e in sorted(extra)]:
        s = by_name[sub]
        rows.append((is_main, s[1], s[2], s[3], s[4], date, hour, round(lat, 2), round(lng, 2), place, vtype, make, model, colour,
                     dark, police, video, reactions, comments, views, local))


def q(v):
    if v is None:
        return "NULL"
    if isinstance(v, (int, float)):
        return str(v)
    return "N'" + str(v).replace("'", "''") + "'"


with open(out, "w") as f:
    f.write(f"""CREATE DATABASE badjockeys;
GO
CREATE LOGIN bjk_chat WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;
GO
USE badjockeys;
GO
CREATE SCHEMA reporting;
GO
CREATE TABLE reporting.Subcategories (Position tinyint NOT NULL, Subcategory nvarchar(40) NOT NULL PRIMARY KEY,
    SubcategoryLabel nvarchar(80) NOT NULL, [Group] nvarchar(30) NOT NULL, GroupLabel nvarchar(60) NOT NULL);
CREATE TABLE reporting.Incidents (IsMainType bit NOT NULL, Subcategory nvarchar(40) NOT NULL,
    SubcategoryLabel nvarchar(80) NOT NULL, [Group] nvarchar(30) NOT NULL, GroupLabel nvarchar(60) NOT NULL,
    IncidentDate date NOT NULL, IncidentYear smallint NOT NULL, IncidentMonth tinyint NOT NULL, Weekday tinyint NOT NULL,
    WeekdayName nvarchar(30) NOT NULL, HourUk tinyint NOT NULL, AreaLatitude decimal(5,2) NOT NULL,
    AreaLongitude decimal(5,2) NOT NULL, PlaceName nvarchar(200) NULL, VehicleType nvarchar(20) NULL,
    VehicleMake nvarchar(100) NULL, VehicleModel nvarchar(100) NULL, VehicleColour nvarchar(20) NULL, IsDark bit NOT NULL,
    ReportedToPolice bit NOT NULL, HasVideo bit NOT NULL, Reactions int NOT NULL, Comments int NOT NULL, Views int NOT NULL,
    LocalViews int NOT NULL);
CREATE USER bjk_chat FOR LOGIN bjk_chat;
GRANT SELECT ON reporting.Incidents TO bjk_chat;
GRANT SELECT ON reporting.Subcategories TO bjk_chat;
GO
""")
    f.write("INSERT INTO reporting.Subcategories VALUES\n" + ",\n".join(f"({s[0]}, {q(s[1])}, {q(s[2])}, {q(s[3])}, {q(s[4])})" for s in subs) + ";\nGO\n")
    weekday_names = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]
    for start in range(0, len(rows), 500):
        values = []
        for r in rows[start:start + 500]:
            (is_main, sub, label, group, glabel, date, hour, lat, lng, place, vtype, make, model, colour,
             dark, police, video, reactions, comments, views, local) = r
            values.append("(" + ", ".join([
                str(is_main), q(sub), q(label), q(group), q(glabel), q(date.isoformat()), str(date.year), str(date.month),
                str(date.isoweekday()), q(weekday_names[date.weekday()]), str(hour), f"{lat:.2f}", f"{lng:.2f}", q(place),
                q(vtype), q(make), q(model), q(colour), str(dark), str(police), str(video), str(reactions), str(comments),
                str(views), str(local)]) + ")")
        f.write("INSERT INTO reporting.Incidents VALUES\n" + ",\n".join(values) + ";\nGO\n")
    f.write("SELECT COUNT(*) AS IncidentRows, SUM(CAST(IsMainType AS int)) AS Incidents FROM reporting.Incidents;\nGO\n")

print(f"{len(rows)} rows written to {out}")
