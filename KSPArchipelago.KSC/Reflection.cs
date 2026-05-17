// Cached reflection handles for KK internals.
//
// We can't avoid these.  The KK public API (API.PlaceStatic etc) routes
// runtime spawns through StaticInstance.LegacySpawnInstance, which
// queries surfaceHeight per building — fragile on bodies whose PQS
// isn't warmed up.  Generate.py-style cfg loading bypasses that path
// entirely by giving each instance a non-zero RelativePosition and a
// shared GroupCenter (a PQSCity that rides the body surface).  To
// replicate that at runtime we have to construct the GroupCenter and
// StaticInstance ourselves and drive Spawn / Orientate / Activate
// directly — all internal in KK.

using System;
using System.Reflection;
using KerbalKonstructs.Core;

namespace KSPArchipelago.KSC
{
    internal static class Reflection
    {
        private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags StaticNonPublic   = BindingFlags.Static   | BindingFlags.NonPublic;

        private static ConstructorInfo _mapDecalCtor;
        private static ConstructorInfo _staticInstanceCtor;
        private static MethodInfo _groupCenterSpawn;
        private static MethodInfo _staticOrientate;
        private static MethodInfo _staticActivate;
        private static MethodInfo _getModelByName;
        private static FieldInfo _staticModelField;

        public static MapDecalInstance NewMapDecalInstance()
        {
            if (_mapDecalCtor == null)
            {
                _mapDecalCtor = typeof(MapDecalInstance).GetConstructor(
                    InstanceNonPublic, null, Type.EmptyTypes, null);
                if (_mapDecalCtor == null)
                    throw new MissingMethodException("KerbalKonstructs.Core.MapDecalInstance ctor not found.");
            }
            return (MapDecalInstance) _mapDecalCtor.Invoke(null);
        }

        public static StaticInstance NewStaticInstance()
        {
            if (_staticInstanceCtor == null)
            {
                _staticInstanceCtor = typeof(StaticInstance).GetConstructor(
                    InstanceNonPublic, null, Type.EmptyTypes, null);
                if (_staticInstanceCtor == null)
                    throw new MissingMethodException("KerbalKonstructs.Core.StaticInstance ctor not found.");
            }
            return (StaticInstance) _staticInstanceCtor.Invoke(null);
        }

        public static void GroupCenterSpawn(GroupCenter gc)
        {
            if (_groupCenterSpawn == null)
            {
                _groupCenterSpawn = typeof(GroupCenter).GetMethod("Spawn", InstanceNonPublic);
                if (_groupCenterSpawn == null)
                    throw new MissingMethodException("KerbalKonstructs.Core.GroupCenter.Spawn not found.");
            }
            _groupCenterSpawn.Invoke(gc, null);
        }

        public static void StaticOrientate(StaticInstance inst)
        {
            if (_staticOrientate == null)
            {
                _staticOrientate = typeof(StaticInstance).GetMethod("Orientate", InstanceNonPublic);
                if (_staticOrientate == null)
                    throw new MissingMethodException("KerbalKonstructs.Core.StaticInstance.Orientate not found.");
            }
            _staticOrientate.Invoke(inst, null);
        }

        public static void StaticActivate(StaticInstance inst)
        {
            if (_staticActivate == null)
            {
                _staticActivate = typeof(StaticInstance).GetMethod("Activate", InstanceNonPublic);
                if (_staticActivate == null)
                    throw new MissingMethodException("KerbalKonstructs.Core.StaticInstance.Activate not found.");
            }
            _staticActivate.Invoke(inst, null);
        }

        public static StaticModel GetModelByName(string name)
        {
            if (_getModelByName == null)
            {
                _getModelByName = typeof(StaticDatabase).GetMethod("GetModelByName", StaticNonPublic);
                if (_getModelByName == null)
                    throw new MissingMethodException("KerbalKonstructs.Core.StaticDatabase.GetModelByName not found.");
            }
            return (StaticModel) _getModelByName.Invoke(null, new object[] { name });
        }

        public static void SetStaticInstanceModel(StaticInstance inst, StaticModel model)
        {
            if (_staticModelField == null)
            {
                _staticModelField = typeof(StaticInstance).GetField("model", InstanceNonPublic);
                if (_staticModelField == null)
                    throw new MissingMethodException("KerbalKonstructs.Core.StaticInstance.model field not found.");
            }
            _staticModelField.SetValue(inst, model);
        }
    }
}
