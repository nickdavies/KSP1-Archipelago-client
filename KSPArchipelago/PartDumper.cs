using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Security.Policy;

public class KSPPartDumper
{
    public class DumpablePart
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Identical { get; set; }
        public string ModuleInfo { get; set; }
        public PartCategories Category { get; set; }
        public bool IsHidden { get; set; }
        public string TechRequired { get; set; }
        public float DryMass { get; set; }
        public float ResourceMass { get; set; }
        public Dictionary<string, double> Resources { get; set; }
        public List<DumpEngine> Engines { get; set; }
        public List<DumpParachute> Parachute { get; set; }
        public List<DumpWheels> Wheels { get; set; }
        public List<DumpLadder> Ladders { get; set; }
        public List<DumpExperiment> Experiments { get; set; }
        public bool Decoupler { get; set; }
        public bool Solar { get; set; }
        public DumpGenerator Generator { get; set; }
        public bool LaunchClamp { get; set; }
        public bool KerbalEva { get; set; }
        public bool Cargo { get; set; }
    }

    public class DumpEngine
    {
        public float MaxVacThrust { get; set; }
        public float MaxAtmThrust { get; set; }
        public bool ThrottleLocked { get; set; }
    }

    public class DumpGenerator
    {
        public bool AlwaysActive { get; set; }
        public float Efficiency { get; set; }
        public float ResourceThreshold { get; set; }
    }

    public class DumpParachute
    {
        public float FullyDeployedDrag { get; set; }
        public float SemiDeployedDrag { get; set; }
    }

    public class DumpWheels
    {
        public float Radius { get; set; }
        public WheelType WheelType { get; set; }
    }

    public class DumpLadder
    {
        public bool Retractable { get; set; }
        public float ActivationRange { get; set; }
    }

    public class DumpExperiment
    {
        public string Id { get; set; }
        public bool RequiresAtmosphere { get; set; }
        public bool RequiresNoAtmosphere { get; set; }
        public uint SituationMask { get; set; }

        public string RequiredEvaPart { get; set; }
    }

    public static class PartDumper
    {
        public static void DumpToFile(StreamWriter target)
        {
            target.Write(DumpPartsToJson());
            target.Flush();
        }

        public static string DumpPartsToJson()
        {
            List<DumpablePart> parts = new List<DumpablePart>();

            foreach (AvailablePart kspPart in PartLoader.LoadedPartsList)
            {
                Part prefab = kspPart.partPrefab;

                Dictionary<string, double> resources = new Dictionary<string, double>();
                foreach (PartResource resource in prefab.Resources.dict.Values)
                {
                    resources.Add(resource.resourceName, resource.maxAmount);
                }

                DumpablePart part = new DumpablePart
                {
                    Name = kspPart.name,
                    Title = kspPart.title,
                    Description = kspPart.description,
                    Identical = kspPart.identicalParts,
                    ModuleInfo = kspPart.moduleInfo,
                    Category = kspPart.category,
                    IsHidden = kspPart.TechHidden,
                    TechRequired = kspPart.TechRequired,
                    DryMass = prefab.mass,
                    ResourceMass = prefab.resourceMass,
                    Resources = resources,
                    Engines = new List<DumpEngine>(),
                    Parachute = new List<DumpParachute>(),
                    Wheels = new List<DumpWheels>(),
                    Ladders = new List<DumpLadder>(),
                    Experiments = new List<DumpExperiment>(),
                    Decoupler = prefab.isDecoupler(out ModuleDecouple _),
                    Solar = prefab.isSolarPanel(out ModuleDeployableSolarPanel _),
                    LaunchClamp = prefab.isLaunchClamp(),
                    KerbalEva = prefab.isKerbalEVA(),
                    Cargo = prefab.isCargoPart(),
                };

                if (prefab.isEngine(out List<ModuleEngines> kspEngines))
                {
                    foreach (ModuleEngines kspEngine in kspEngines)
                    {
                        DumpEngine engine = new DumpEngine
                        {
                            MaxVacThrust = kspEngine.MaxThrustOutputVac(false),
                            MaxAtmThrust = kspEngine.MaxThrustOutputAtm(),
                            ThrottleLocked = kspEngine.throttleLocked,
                        };
                        part.Engines.Add(engine);
                    }
                }

                if (prefab.isGenerator(out ModuleGenerator kspGen))
                {
                    part.Generator = new DumpGenerator
                    {
                        AlwaysActive = kspGen.isAlwaysActive,
                        Efficiency = kspGen.efficiency,
                        ResourceThreshold = kspGen.resourceThreshold,
                    };
                }

                foreach (ModuleParachute kspChute in prefab.Modules.GetModules<ModuleParachute>())
                {
                    part.Parachute.Add(new DumpParachute
                    {
                        FullyDeployedDrag = kspChute.fullyDeployedDrag,
                        SemiDeployedDrag = kspChute.semiDeployedDrag,
                    });
                }

                foreach (ModuleWheelBase kspMod in prefab.Modules.GetModules<ModuleWheelBase>())
                {
                    part.Wheels.Add(new DumpWheels
                    {
                        Radius = kspMod.radius,
                        WheelType = kspMod.wheelType
                    });
                }

                foreach (RetractableLadder kspMod in prefab.Modules.GetModules<RetractableLadder>())
                {
                    part.Ladders.Add(new DumpLadder
                    {
                        Retractable = true,
                        ActivationRange = kspMod.externalActivationRange,
                    });
                }

                foreach (ModuleScienceExperiment kspMod in prefab.Modules.GetModules<ModuleScienceExperiment>())
                {
                    if (kspMod.experimentID == "ROCScience")
                    {
                        continue;
                    }
                    ScienceExperiment kspExperiment = ResearchAndDevelopment.GetExperiment(kspMod.experimentID);

                    if (kspExperiment != null)
                    {
                        part.Experiments.Add(new DumpExperiment
                        {
                            Id = kspMod.experimentID,
                            RequiresAtmosphere = kspExperiment.requireAtmosphere,
                            RequiresNoAtmosphere = kspExperiment.requireNoAtmosphere,
                            SituationMask = kspExperiment.situationMask,
                            RequiredEvaPart = kspMod.requiresInventoryPart ? kspMod.requiredInventoryPart : null,
                        });
                    }
                    else
                    {
                        Console.WriteLine($"Found null experiment {kspMod.experimentID} for part {kspPart.name}({kspPart.title}) ");
                    }
                }

                parts.Add(part);
            }
            return JsonConvert.SerializeObject(parts);
        }
    }
}
