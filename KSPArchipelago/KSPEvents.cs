using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public class KSPEvents
{
    public interface IKSPEvent
    {
        string APLocation();
    }

    public class KSPScienceEvent : IKSPEvent
    {
        private readonly string experiment;
        private readonly string body;
        private readonly ExperimentSituations situation;

        public KSPScienceEvent(string experiment, string body, ExperimentSituations situation)
        {
            this.experiment = experiment;
            this.body = body;
            this.situation = situation;
        }

        public string APLocation()
        {
            return $"{body}:{experiment}:{situation}";
        }
    }

    public class KSPEventFactory
    {
        private readonly Dictionary<string, bool> validExperiments;
        private readonly Dictionary<string, bool> validBodies;

        public KSPEventFactory(Dictionary<string, bool> validExperiments, Dictionary<string, bool> validBodies)
        {
            this.validExperiments = validExperiments;
            this.validBodies = validBodies;
        }

        private bool ExtractIfValid(string[] candidates, Dictionary<string, bool> valid, string whole, string ty)
        {
            if (candidates.Length != 2)
            {
                Console.WriteLine($"Warning: Got unknown {ty} format '{whole}'");
                return false;
            }

            if (!valid.TryGetValue(candidates[0], out bool shouldCare))
            {
                Console.WriteLine($"Warning: Got unknown {ty} value '{candidates[0]}' in '{whole}");
                return false;
            }
            if (!shouldCare)
            {
                return false;
            }
            return true;
        }

        public IKSPEvent FromScienceSubject(ScienceSubject subject)
        {
            string[] parts = subject.id.Split('@');
            if (!ExtractIfValid(parts, validExperiments, subject.id, "science experiment"))
            {
                return null;
            }
            string[] split = new Regex(@"(?<!^)(?=[A-Z])").Split(parts[1], 1);
            if (!ExtractIfValid(split, validBodies, subject.id, "body"))
            {
                return null;
            }

            foreach (ExperimentSituations sit in (ExperimentSituations[])Enum.GetValues(typeof(ExperimentSituations)))
            {
                if (subject.IsFromSituation(sit))
                {
                    return new KSPScienceEvent(parts[0], split[0], sit);
                }
            }
            Console.WriteLine($"Warning: got unknown situation+biome value '{split[1]} in {subject.id}");
            return null;
        }
    }
}
