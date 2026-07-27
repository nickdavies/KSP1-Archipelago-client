using System;
using System.Collections.Generic;
using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Completes when a recovered (not transmitted) surface SAMPLE for the
    /// specified body appears on a recovered vessel. Mirrors MissionTracker's
    /// surface-sample detection which inspects ScienceData subjectIDs of
    /// ModuleScienceExperiment/ModuleScienceContainer entries.
    /// </summary>
    public class RecoveredSurfaceSampleParameter : ContractParameter
    {
        private const string SurfaceSamplePrefix = "surfaceSample@";

        public string BodyName = "";

        public RecoveredSurfaceSampleParameter() { }

        public RecoveredSurfaceSampleParameter(string bodyName)
        {
            BodyName = bodyName ?? "";
        }

        protected override string GetTitle()
        {
            return string.IsNullOrEmpty(BodyName)
                ? "Recover a surface sample"
                : $"Recover a surface sample from {BodyName}";
        }

        protected override string GetHashString()
            => "RecoveredSurfaceSample|" + BodyName;

        protected override void OnRegister()
        {
            base.OnRegister();
            GameEvents.onVesselRecovered.Add(new EventData<ProtoVessel, bool>.OnEvent(OnVesselRecovered));
        }

        protected override void OnUnregister()
        {
            GameEvents.onVesselRecovered.Remove(new EventData<ProtoVessel, bool>.OnEvent(OnVesselRecovered));
            base.OnUnregister();
        }

        private void OnVesselRecovered(ProtoVessel vessel, bool quick)
        {
            if (Root == null || Root.ContractState != Contract.State.Active) return;
            if (state == ParameterState.Complete) return;
            if (vessel == null) return;

            foreach (ProtoPartSnapshot part in vessel.protoPartSnapshots)
            {
                foreach (ProtoPartModuleSnapshot module in part.modules)
                {
                    if (module.moduleName != "ModuleScienceExperiment" &&
                        module.moduleName != "ModuleScienceContainer")
                        continue;

                    foreach (ConfigNode dataNode in module.moduleValues.GetNodes("ScienceData"))
                    {
                        string subjectId = dataNode.GetValue("subjectID") ?? "";
                        string body = ExtractSampleBody(subjectId);
                        if (body != null && string.Equals(body, BodyName, StringComparison.Ordinal))
                        {
                            SetComplete();
                            return;
                        }
                    }
                }
            }
        }

        // Extracts body name from a surfaceSample subject id.
        private static string ExtractSampleBody(string subjectId)
        {
            if (string.IsNullOrEmpty(subjectId)) return null;
            if (!subjectId.StartsWith(SurfaceSamplePrefix, StringComparison.Ordinal))
                return null;
            int srfIdx = subjectId.IndexOf("Srf", SurfaceSamplePrefix.Length, StringComparison.Ordinal);
            if (srfIdx <= SurfaceSamplePrefix.Length) return null;
            return subjectId.Substring(SurfaceSamplePrefix.Length, srfIdx - SurfaceSamplePrefix.Length);
        }

        protected override void OnLoad(ConfigNode node)
        {
            BodyName = node.GetValue("body") ?? "";
        }

        protected override void OnSave(ConfigNode node)
        {
            if (!string.IsNullOrEmpty(BodyName))
                node.AddValue("body", BodyName);
        }
    }
}
