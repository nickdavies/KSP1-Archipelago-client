from typing import Optional, List, Dict
from enum import Enum
from dataclasses import dataclass

@dataclass
class Biome:
    name: str
    liquid: Optional[bool] = False
    surface_only: Optional[bool] = False


@dataclass
class Body:
    name: str
    biomes: List[Biome]
    atmosphere: Optional[bool] = False


class Situation(Enum):
    Landed = 1
    Splashed = 2
    FlyingLow = 3
    FlyingHigh = 4
    SpaceLow = 5
    SpaceHigh = 6


class RunType(Enum):
    # exactly once per situation
    Global = 1
    # Once per biome but not the surface-only ones.
    RealBiomes = 2
    # Once per landed biome including surface-only ones.
    LandBiomes = 3
    # Once per liquid biome
    LiquidBiomes = 4



@dataclass
class Experiment:
    name: str
    situations: Dict[str, RunType]
    atmosphere_only: Optional[bool] = False


def filter_biomes(run_type: RunType, biomes: List[Biome]):
    if run_type == RunType.Global:
        raise ValueError("RunType.Global is not allowed to be given to filter biomes")

    if run_type == RunType.RealBiomes:
        return [b for b in biomes if not b.surface_only]
    elif run_type == RunType.LandBiomes:
        return [b for b in biomes if not b.liquid]
    elif run_type == RunType.LiquidBiomes:
        return [b for b in biomes if b.liquid]
    else:
        raise ValueError(f"Unknown run_type {run_type}")


### Data

EXPERIMENTS = [
    Experiment("Surface Sample", situations={
        Situation.Landed: RunType.LandBiomes,
        Situation.Splashed: RunType.LiquidBiomes,
    }),
    Experiment("EVA Report", situations={
        Situation.Landed: RunType.LandBiomes,
        Situation.Splashed: RunType.LiquidBiomes,
        Situation.FlyingLow: RunType.RealBiomes,
        Situation.FlyingHigh: RunType.Global,
        Situation.SpaceLow: RunType.RealBiomes,
        Situation.SpaceHigh: RunType.Global,
    }),
    Experiment("EVA experiments", situations={
        Situation.Landed: RunType.LandBiomes,
        Situation.SpaceLow: RunType.Global,
        Situation.SpaceHigh: RunType.Global,
    }),
    Experiment("Asteroid Sample", situations={
        Situation.Landed: RunType.LandBiomes,
        Situation.Splashed: RunType.LiquidBiomes,
        Situation.FlyingLow: RunType.RealBiomes,
        Situation.FlyingHigh: RunType.Global,
        Situation.SpaceLow: RunType.Global,
        Situation.SpaceHigh: RunType.Global,
    }),
    Experiment("Comet Sample", situations={
        Situation.Landed: RunType.LandBiomes,
        Situation.Splashed: RunType.LiquidBiomes,
        Situation.FlyingLow: RunType.RealBiomes,
        Situation.FlyingHigh: RunType.Global,
        Situation.SpaceLow: RunType.Global,
        Situation.SpaceHigh: RunType.Global,
    }),
    Experiment("Crew Report", situations={
        Situation.Landed: RunType.LandBiomes,
        Situation.Splashed: RunType.LiquidBiomes,
        Situation.FlyingLow: RunType.RealBiomes,
        Situation.FlyingHigh: RunType.Global,
        Situation.SpaceLow: RunType.Global,
        Situation.SpaceHigh: RunType.Global,
    }),
    Experiment("Mystery Goo Observation", situations={
        Situation.Landed: RunType.LandBiomes,
        Situation.Splashed: RunType.LiquidBiomes,
        Situation.FlyingLow: RunType.Global,
        Situation.FlyingHigh: RunType.Global,
        Situation.SpaceLow: RunType.Global,
        Situation.SpaceHigh: RunType.Global,
    }),
    Experiment("Materials Study", situations={
        Situation.Landed: RunType.LandBiomes,
        Situation.Splashed: RunType.LiquidBiomes,
        Situation.FlyingLow: RunType.Global,
        Situation.FlyingHigh: RunType.Global,
        Situation.SpaceLow: RunType.Global,
        Situation.SpaceHigh: RunType.Global,
    }),
    Experiment("Tempreture Scan", situations={
        Situation.Landed: RunType.LandBiomes,
        Situation.Splashed: RunType.LiquidBiomes,
        Situation.FlyingLow: RunType.RealBiomes,
        Situation.FlyingHigh: RunType.Global,
        Situation.SpaceLow: RunType.Global,
        Situation.SpaceHigh: RunType.Global,
    }),
    Experiment("Atmospheric Pressure Scan", situations={
        Situation.Landed: RunType.LandBiomes,
        Situation.Splashed: RunType.LiquidBiomes,
        Situation.FlyingLow: RunType.Global,
        Situation.FlyingHigh: RunType.Global,
        Situation.SpaceLow: RunType.Global,
        Situation.SpaceHigh: RunType.Global,
    }),
    Experiment("Gravity Scan", situations={
        Situation.Landed: RunType.LandBiomes,
        Situation.Splashed: RunType.LiquidBiomes,
        Situation.SpaceLow: RunType.Global,
        Situation.SpaceHigh: RunType.Global,
    }),
    Experiment("Seismic Scan", situations={
        Situation.Landed: RunType.LandBiomes,
    }),
    Experiment("Atmosphere Analysis", atmosphere_only=True, situations={
        Situation.Landed: RunType.LandBiomes,
        Situation.FlyingLow: RunType.RealBiomes,
        Situation.FlyingHigh: RunType.RealBiomes,
    }),
    Experiment("Infrared Telescope", situations={
        Situation.SpaceHigh: RunType.Global,
    }),
    Experiment("Magnetometer Boom", situations={
        Situation.SpaceLow: RunType.Global,
        Situation.SpaceHigh: RunType.Global,
    }),
]

BODIES = [
    Body("Kerbol", atmosphere=True, biomes=[]),
    Body("Moho", biomes=[
        Biome("North Pole"),
        Biome("Northern Sinkhole Ridge"),
        Biome("Northern Sinkhole"),
        Biome("Highlands"),
        Biome("Midlands"),
        Biome("Minor Craters"),
        Biome("Central Lowlands"),
        Biome("Western Lowlands"),
        Biome("South Western Lowlands"),
        Biome("South Eastern Lowlands"),
        Biome("Canyon"),
        Biome("South Pole"),
    ]),
    Body("Eve", atmosphere=True, biomes=[
        Biome("Poles"),
        Biome("Lowlands"),
        Biome("Midlands"),
        Biome("Highlands"),
        Biome("Foothills"),
        Biome("Peaks"),
        Biome("Impact Ejecta"),
        Biome("Olympus"), # Unsure if this is ocean?
        Biome("Explodium Sea", liquid = True),
        Biome("Shallows", liquid = True),
        Biome("Crater Lake", liquid = True),
        Biome("Western Sea", liquid = True),
        Biome("Eastern Sea", liquid = True),
    ]),
    Body("Gilly", biomes=[
        Biome("Lowlands"),
        Biome("Midlands"), 
        Biome("Highlands"),
    ]),
    Body("Kerbin", atmosphere=True, biomes=[
        Biome("Ice Caps"),
        Biome("Northern Ice Shelf"),
        Biome("Southern Ice Shelf"),
        Biome("Tundra"),
        Biome("Highlands"),
        Biome("Mountains"),
        Biome("Grasslands"),
        Biome("Deserts"),
        Biome("Badlands"),
        Biome("Shores"), # TODO: assert this is not liquid
        Biome("Water", liquid=True),
        # KSC and other surface-only biomes
        Biome("Inland Kerbal Space Center", surface_only=True),
        Biome("Baikerbanur LaunchPad", surface_only=True),
        Biome("Island Airfield", surface_only=True),
        Biome("Woomerang Launch Site", surface_only=True),
        Biome("KSC", surface_only=True),
        Biome("Administration", surface_only=True),
        Biome("Astronaut Complex", surface_only=True),
        Biome("Crawlerway", surface_only=True),
        Biome("Flag Pole", surface_only=True),
        Biome("LaunchPad", surface_only=True),
        Biome("Mission Control", surface_only=True),
        Biome("R&D", surface_only=True),
        Biome("R&D Central Building", surface_only=True),
        Biome("R&D Corner Lab", surface_only=True),
        Biome("R&D Main Building", surface_only=True),
        Biome("R&D Observatory", surface_only=True),
        Biome("R&D Side Lab", surface_only=True),
        Biome("R&D Small Lab", surface_only=True),
        Biome("R&D Tanks", surface_only=True),
        Biome("R&D Wind Tunnel", surface_only=True),
        Biome("Runway", surface_only=True),
        Biome("SPH", surface_only=True),
        Biome("SPH Main Building", surface_only=True),
        Biome("SPH Round Tank", surface_only=True),
        Biome("SPH Tanks", surface_only=True),
        Biome("SPH Water Tower", surface_only=True),
        Biome("Tracking Station", surface_only=True),
        Biome("Tracking Station Dish East", surface_only=True),
        Biome("Tracking Station Dish North", surface_only=True),
        Biome("Tracking Station Dish South", surface_only=True),
        Biome("Tracking Station Hub", surface_only=True),
        Biome("VAB", surface_only=True),
        Biome("VAB Main Building", surface_only=True),
        Biome("VAB Pod Memorial", surface_only=True),
        Biome("VAB Round Tank", surface_only=True),
        Biome("VAB South Complex", surface_only=True),
    ]),
    Body("Mun", biomes=[
        Biome("Poles"),
        Biome("Polar Lowlands"),
        Biome("Highlands"),
        Biome("Midlands"),
        Biome("Lowlands"),
        Biome("Midland Craters"),
        Biome("Highland Craters"),
        Biome("Canyons"),
        Biome("East Farside Crater"),
        Biome("Farside Crater"),
        Biome("Polar Crater"),
        Biome("Southwest Crater"),
        Biome("Northwest Crater"),
        Biome("East Crater"),
        Biome("Twin Craters"),
        Biome("Farside Basin"),
        Biome("Northeast Basin"),
    ]),
    Body("Minmus", biomes=[
        Biome("Poles"),
        Biome("Lowlands"),
        Biome("Midlands"),
        Biome("Highlands"),
        Biome("Slopes"),
        Biome("Flats"),
        Biome("Lesser Flats"),
        Biome("Great Flats"),
        Biome("Greater Flats"),
    ]),
    Body("Duna", atmosphere=True, biomes=[
        Biome("Poles"),
        Biome("Polar Highlands"),
        Biome("Polar Craters"),
        Biome("Highlands"),
        Biome("Midlands"),
        Biome("Lowlands"),
        Biome("Craters"),
        Biome("Midland Sea"),
        Biome("Northeast Basin"),
        Biome("Southern Basin"),
        Biome("Northern Shelf"),
        Biome("Midland Canyon"),
        Biome("Eastern Canyon"),
        Biome("Western Canyon"),
    ]),
    Body("Ike", biomes=[
        Biome("Polar Lowlands"),
        Biome("Midlands"),
        Biome("Lowlands"),
        Biome("Eastern Mountain Ridge"),
        Biome("Western Mountain Ridge"),
        Biome("Central Mountain Range"),
        Biome("South Eastern Mountain Range"),
        Biome("South Pole"),
    ]),
    Body("Dres", biomes=[
        Biome("Poles"),
        Biome("Highlands"),
        Biome("Midlands"),
        Biome("Ridges"),
        Biome("Lowlands"),
        Biome("Impact Craters"),
        Biome("Impact Ejecta"),
        Biome("Canyons"),
    ]),
    Body("Jool", atmosphere=True, biomes=[]),
    Body("Laythe", biomes=[
        Biome("Poles"),
        Biome("Shores"),
        Biome("Dunes"),
        Biome("Crescent Bay", liquid=True),
        Biome("The Sagen Sea", liquid=True),
        Biome("Crater Island"),
        Biome("Shallows", liquid=True),
        Biome("Crater Bay", liquid=True),
        Biome("Degrasse Sea", liquid=True),
        Biome("Peaks"),
    ]),
    Body("Vall", biomes=[
        Biome("Poles"),
        Biome("Highlands"),
        Biome("Midlands"),
        Biome("Lowlands"),
        Biome("Mountains"),
        Biome("Northeast Basin"),
        Biome("Northwest Basin"),
        Biome("Southern Basin"),
        Biome("Southern Valleys"),

    ]),
    Body("Tylo", biomes=[
        Biome("Highlands"),
        Biome("Midlands"),
        Biome("Lowlands"),
        Biome("Mara"),
        Biome("Minor Craters"),
        Biome("Gagarin Crater"),
        Biome("Grissom Crater"),
        Biome("Galileio Crater"),
        Biome("Tycho Crater"),
    ]),
    Body("Bop", biomes=[
        Biome("Poles"),
        Biome("Slopes"),
        Biome("Peaks"),
        Biome("Valley"),
        Biome("Ridges"),
    ]),
    Body("Pol", biomes=[
        Biome("Poles"),
        Biome("Highlands"),
        Biome("Midlands"),
        Biome("Lowlands"),
    ]),
    Body("Eeloo", biomes=[
        Biome("Poles"),
        Biome("Northern Glaciers"),
        Biome("Midlands"),
        Biome("Lowlands"),
        Biome("Ice Canyons"),
        Biome("Highlands"),
        Biome("Craters"),
        Biome("Fragipan"),
        Biome("Babbage Patch"),
        Biome("Southern Glaciers"),
        Biome("Mu Glacier"),
    ]),
]

options = []
for body in BODIES:
    for experiment in EXPERIMENTS:
        for situation, run_type in experiment.situations.items():
            if situation in [Situation.FlyingLow, Situation.FlyingHigh] and not body.atmosphere:
                continue

            if run_type == RunType.Global:
                options.append(".".join([body.name, experiment.name, str(situation), "global"]))
            else:
                for biome in filter_biomes(run_type, body.biomes):
                    options.append(".".join([body.name, experiment.name, str(situation), biome.name]))

for option in options:
    print(option)
print(len(options))
