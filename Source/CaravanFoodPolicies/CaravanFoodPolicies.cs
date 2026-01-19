using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace CaravanFoodPolicies
{
    [StaticConstructorOnStartup]
    public static class CaravanFoodPolicies
    {
        static CaravanFoodPolicies()
        {
            CFPLog.Message("StartUp");
            var harmony = new Harmony("Aeroux.CaravanFoodPolicies");
            harmony.PatchAll();
        }

        // --- Patches ---

        [HarmonyPatch(typeof(Pawn_FoodRestrictionTracker), nameof(Pawn_FoodRestrictionTracker.CurrentFoodPolicy), MethodType.Setter)]
        static class Pawn_FoodRestrictionTracker_CurrentFoodPolicy_Patch
        {
            static void Postfix(Pawn_FoodRestrictionTracker __instance)
            {
                Pawn pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
                if (pawn == null) return;
                if (!pawn.IsCaravanMember()) return;

                Caravan caravan = pawn.GetCaravan();
                if (caravan == null) return;

                caravan.RecacheInventory();
            }
        }

        [HarmonyPatch(typeof(Dialog_FormCaravan), "DaysWorthOfFood", MethodType.Getter)]
        static class Dialog_FormCaravan_DaysWorthOfFood_Patch
        {
            [HarmonyBefore(new string[] { "SmashPhil.VehicleFramework" })]
            static void Prefix(List<TransferableOneWay> ___transferables, bool ___daysWorthOfFoodDirty, out Dictionary<Pawn, FoodPolicy> __state)
            {
                __state = null;
                if (!___daysWorthOfFoodDirty) return;

                __state = PolicyUtils.ApplyCaravanPolicies(___transferables);
            }

            static void Finalizer(Dictionary<Pawn, FoodPolicy> __state)
            {
                PolicyUtils.RestorePolicies(__state);
            }
        }

        [HarmonyPatch(typeof(Dialog_FormCaravan), "SelectApproximateBestTravelSupplies")]
        static class Dialog_FormCaravan_SelectApproximateBestTravelSupplies_Patch
        {
            [HarmonyBefore(new string[] { "SmashPhil.VehicleFramework" })]
            static void Prefix(List<TransferableOneWay> ___transferables, out Dictionary<Pawn, FoodPolicy> __state)
            {
                __state = PolicyUtils.ApplyCaravanPolicies(___transferables);
            }

            static void Finalizer(Dictionary<Pawn, FoodPolicy> __state)
            {
                PolicyUtils.RestorePolicies(__state);
            }
        }

        [HarmonyPatch(typeof(CaravanMaker), nameof(CaravanMaker.MakeCaravan))]
        static class CaravanMaker_MakeCaravan_Patch
        {
            static void Postfix(ref Caravan __result)
            {
                if (Find.CurrentMap?.ParentFaction == null || !Find.CurrentMap.ParentFaction.IsPlayer) return;

                PolicyUtils.ApplyPolicies(
                    __result.pawns,
                    PolicyUtils.GetStoredCaravanPolicy,
                    CFPLog.Departure
                );
            }
        }

        [HarmonyPatch(typeof(CaravanArrivalAction_Enter), nameof(CaravanArrivalAction_Enter.Arrived))]
        static class CaravanArrivalAction_Enter_Patch
        {
            static void Prefix(Caravan caravan, ref MapParent ___mapParent)
            {
                if (___mapParent?.Map == null || !___mapParent.Map.IsPlayerHome) return;

                PolicyUtils.ApplyPolicies(
                    caravan.pawns,
                    PolicyUtils.GetStoredHomePolicy,
                    CFPLog.Arrival
                );
            }
        }
    }

    internal static class CFPLog
    {
        private const string Prefix = "[CaravanFoodPolicies]";

        public static void Missing(Pawn p)
        {
            Warning($"Could not update food policy for '{p.NameShortColored}'. " +
                    $"Their current food policy is '{p.foodRestriction.CurrentFoodPolicy.label}'.");
        }

        public static void Departure(Pawn p)
        {
            Message($"'{p.NameShortColored}' departed in a caravan. " +
                    $"Their food policy has been updated to '{p.foodRestriction.CurrentFoodPolicy.label}'.");
        }

        public static void Arrival(Pawn p)
        {
            Message($"'{p.NameShortColored}' returned home. " +
                    $"Their food policy has been reset back to '{p.foodRestriction.CurrentFoodPolicy.label}'.");
        }

        public static void Message(string message)
        {
            Log.Message(Format(message));
        }

        public static void Warning(string message)
        {
            Log.Warning(Format(message));
        }

        public static void Error(string message)
        {
            Log.Error(Format(message));
        }

        public static void ErrorOnce(string message, int key)
        {
            Log.ErrorOnce(Format(message), key);
        }

        public static void Exception(Exception exception, string contextMessage = null)
        {
            if (exception == null)
            {
                Warning("Exception(null) called.");
                return;
            }

            if (string.IsNullOrEmpty(contextMessage))
            {
                Log.Error(Format(exception.ToString()));
            }
            else
            {
                Log.Error(Format(contextMessage + Environment.NewLine + exception));
            }
        }

        private static string Format(string message)
        {
            if (string.IsNullOrEmpty(message)) return Prefix;
            return $"{Prefix} {message}";
        }
    }

    // --- Logic Utilities ---

    public static class PolicyUtils
    {
        private static CaravanFoodPoliciesData Data => Find.World.GetComponent<CaravanFoodPoliciesData>();

        public static FoodPolicy GetStoredCaravanPolicy(Pawn pawn)
        {
            return TryGetSavedPolicy(Data?.RetainedCaravanDataIds, pawn) ?? DefaultPolicy;
        }

        public static FoodPolicy GetStoredHomePolicy(Pawn pawn)
        {
            var policy = TryGetSavedPolicy(Data?.RetainedHomeDataIds, pawn);
            if (policy != null) return policy;

            // Initialization Fallback: Save and return the current policy
            var current = pawn.foodRestriction?.CurrentFoodPolicy ?? DefaultPolicy;
            SetSavedPolicy(Data?.RetainedHomeDataIds, pawn, current);
            return current;
        }

        public static void SetStoredCaravanPolicy(Pawn pawn, FoodPolicy policy)
        {
            SetSavedPolicy(Data?.RetainedCaravanDataIds, pawn, policy);
        }

        public static void SetStoredHomePolicy(Pawn pawn, FoodPolicy policy)
        {
            SetSavedPolicy(Data?.RetainedHomeDataIds, pawn, policy);
        }

        private static FoodPolicy TryGetSavedPolicy(Dictionary<string, int> source, Pawn pawn)
        {
            if (source == null) return null;
            if (!source.TryGetValue(pawn.GetUniqueLoadID(), out var id)) return null;
            return GetPolicyById(id);
        }

        private static void SetSavedPolicy(Dictionary<string, int> target, Pawn pawn, FoodPolicy policy)
        {
            if (target == null || policy == null) return;
            target[pawn.GetUniqueLoadID()] = policy.id;
        }

        private static FoodPolicy GetPolicyById(int id)
        {
            return Current.Game.foodRestrictionDatabase.AllFoodRestrictions
                .FirstOrFallback(x => x.id == id, null);
        }

        public static FoodPolicy GetPolicyByLabel(string label)
        {
            return Current.Game.foodRestrictionDatabase.AllFoodRestrictions
                .FirstOrFallback(x => x.label == label, null);
        }

        private static FoodPolicy DefaultPolicy => Current.Game.foodRestrictionDatabase.DefaultFoodRestriction();

        public static Dictionary<Pawn, FoodPolicy> ApplyCaravanPolicies(List<TransferableOneWay> transferables)
        {
            var state = new Dictionary<Pawn, FoodPolicy>();
            if (transferables == null) return state;

            foreach (var t in transferables)
            {
                if (t.AnyThing is Pawn pawn &&
                    pawn.RaceProps.Humanlike &&
                    !pawn.IsCaravanMember() &&
                    t.CountToTransfer > 0)
                {
                    state[pawn] = pawn.foodRestriction.CurrentFoodPolicy;

                    var caravanPolicy = GetStoredCaravanPolicy(pawn);
                    if (caravanPolicy != null)
                    {
                        pawn.foodRestriction.CurrentFoodPolicy = caravanPolicy;
                    }
                }
            }
            return state;
        }

        public static void RestorePolicies(Dictionary<Pawn, FoodPolicy> state)
        {
            if (state == null) return;
            foreach (var kvp in state)
            {
                kvp.Key.foodRestriction.CurrentFoodPolicy = kvp.Value;
            }
        }

        public static void ApplyPolicies(ThingOwner<Pawn> pawns, Func<Pawn, FoodPolicy> policyGetter, Action<Pawn> logAction)
        {
            if (pawns == null) return;

            foreach (var pawn in pawns)
            {
                if (!pawn.RaceProps.Humanlike) continue;

                var policyToApply = policyGetter(pawn);
                if (policyToApply == null)
                {
                    CFPLog.Missing(pawn);
                    continue;
                }

                pawn.foodRestriction.CurrentFoodPolicy = policyToApply;
                logAction(pawn);
            }
        }
    }

    // --- Data Component ---

    public class CaravanFoodPoliciesData : WorldComponent
    {
        private const int LatestVersion = 1;
        private int version = LatestVersion;

        // Changed to store IDs (int) instead of Labels (string)
        public Dictionary<string, int> RetainedCaravanDataIds = new Dictionary<string, int>();
        public Dictionary<string, int> RetainedHomeDataIds = new Dictionary<string, int>();

        // Lists for Scribe saving (Values must be int)
        private List<string> CaravanPawnIdList;
        private List<int> CaravanFoodPolicyIdList;
        private List<string> HomePawnIdList;
        private List<int> HomeFoodPolicyIdList;

        public CaravanFoodPoliciesData(World world) : base(world) { }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref version, "version", 0);

            // 1. Main Save/Load: Use NEW labels ("...Ids") so we don't accidentally try to parse old string data as ints.
            Scribe_Collections.Look(ref RetainedCaravanDataIds, "RetainedCaravanDataIds", LookMode.Value, LookMode.Value, ref CaravanPawnIdList, ref CaravanFoodPolicyIdList);
            Scribe_Collections.Look(ref RetainedHomeDataIds, "RetainedHomeDataIds", LookMode.Value, LookMode.Value, ref HomePawnIdList, ref HomeFoodPolicyIdList);

            // 2. Run Migrations on Load
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                RunMigrations();
            }
        }

        private void RunMigrations()
        {
            int startingVersion = version;

            // Define migrations: (TargetVersion, Action)
            var migrations = new List<(int targetVersion, Action action)>
            {
                (1, MigrateV1_PolicyLabelsToIds)
            };

            foreach (var migration in migrations)
            {
                if (version < migration.targetVersion)
                {
                    try
                    {
                        migration.action();
                    }
                    catch (Exception ex)
                    {
                        CFPLog.Exception(ex, $"Failed to run migration for version {migration.targetVersion}");
                    }
                }
            }

            // Update version to latest after migrations
            version = LatestVersion;
            if (startingVersion < LatestVersion)
            {
                CFPLog.Message($"Upgraded CaravanFoodPoliciesData from v{startingVersion} to v{LatestVersion}");
            }
        }

        private void MigrateV1_PolicyLabelsToIds()
        {
            // Initialize temporary buffers
            Dictionary<string, string> legacyCaravanData = new Dictionary<string, string>();
            Dictionary<string, string> legacyHomeData = new Dictionary<string, string>();

            // Initialize working lists for Scribe
            List<string> tempKeys = new List<string>();
            List<string> tempValues = new List<string>();

            // Load the OLD string data
            // Because we are inside 'if (LoadingVars)', this reads the data if it exists.
            Scribe_Collections.Look(ref legacyCaravanData, "RetainedCaravanData", LookMode.Value, LookMode.Value, ref tempKeys, ref tempValues);
            Scribe_Collections.Look(ref legacyHomeData, "RetainedHomeData", LookMode.Value, LookMode.Value, ref tempKeys, ref tempValues);

            // 1. Migrate Caravan Data
            if (legacyCaravanData != null && legacyCaravanData.Count > 0)
            {
                if (RetainedCaravanDataIds == null) RetainedCaravanDataIds = new Dictionary<string, int>();

                foreach (var kvp in legacyCaravanData)
                {
                    // Convert Label (string) -> Policy (ID)
                    var policy = PolicyUtils.GetPolicyByLabel(kvp.Value);
                    if (policy != null)
                    {
                        RetainedCaravanDataIds[kvp.Key] = policy.id;
                    }
                }

                CFPLog.Message($"Migrated {legacyCaravanData.Count} caravan policies to IDs.");
            }

            // 2. Migrate Home Data
            if (legacyHomeData != null && legacyHomeData.Count > 0)
            {
                if (RetainedHomeDataIds == null) RetainedHomeDataIds = new Dictionary<string, int>();

                foreach (var kvp in legacyHomeData)
                {
                    var policy = PolicyUtils.GetPolicyByLabel(kvp.Value);
                    if (policy != null)
                    {
                        RetainedHomeDataIds[kvp.Key] = policy.id;
                    }
                }

                CFPLog.Message($"Migrated {legacyHomeData.Count} home policies to IDs.");
            }
        }
    }
}

namespace RimWorld
{
    using CaravanFoodPolicies; // Import our utils

    // Base class for shared logic
    public abstract class PawnColumnWorker_FoodPolicyBase : PawnColumnWorker
    {
        protected const int TopAreaHeight = 65;
        protected const int ManageFoodPoliciesButtonHeight = 32;

        public override void DoHeader(Rect rect, PawnTable table)
        {
            base.DoHeader(rect, table);
            MouseoverSounds.DoRegion(rect);
        }

        public override void DoCell(Rect rect, Pawn pawn, PawnTable table)
        {
            if (pawn.foodRestriction == null) return;

            var policy = GetPolicy(pawn);
            Rect dropdownRect = new Rect(rect.x, rect.y + 2f, rect.width, rect.height - 4f);

            // Highlight if there is a mismatch
            if (HasMismatch(pawn))
            {
                GUI.color = new Color(1f, 0.6f, 0.6f); // Red tint

                // Handle right-click to fix mismatch
                if (Mouse.IsOver(rect) && Event.current.type == EventType.MouseDown && Event.current.button == 1)
                {
                    ResolveMismatch(pawn);
                    SoundDefOf.Click.PlayOneShotOnCamera();
                    Event.current.Use();
                }
            }
            else if (HasMatch(pawn))
            {
                GUI.color = new Color(0.6f, 1f, 0.6f); // Green tint
            }

            Widgets.Dropdown(
                dropdownRect,
                pawn,
                _ => policy,
                Button_GenerateMenu,
                policy.label.Truncate(dropdownRect.width),
                dragLabel: policy.label,
                paintable: true
            );

            GUI.color = Color.white; // Reset
        }

        protected abstract FoodPolicy GetPolicy(Pawn pawn);
        protected abstract void SetPolicy(Pawn pawn, FoodPolicy policy);
        protected virtual bool HasMatch(Pawn pawn) => false;
        protected virtual bool HasMismatch(Pawn pawn) => false;
        protected abstract void ResolveMismatch(Pawn pawn);


        // Helpers for mismatch logic
        protected bool IsHomeMismatch(Pawn pawn)
        {
            var homePolicy = PolicyUtils.GetStoredHomePolicy(pawn);
            return homePolicy != null && (pawn.Map?.IsPlayerHome == true) && pawn.foodRestriction.CurrentFoodPolicy != homePolicy;
        }

        protected bool IsHomeMatch(Pawn pawn)
        {
            var homePolicy = PolicyUtils.GetStoredHomePolicy(pawn);
            return homePolicy != null && (pawn.Map?.IsPlayerHome == true) && pawn.foodRestriction.CurrentFoodPolicy == homePolicy;
        }

        protected bool IsCaravanMismatch(Pawn pawn)
        {
            var caravanPolicy = PolicyUtils.GetStoredCaravanPolicy(pawn);
            return caravanPolicy != null && pawn.IsCaravanMember() && pawn.foodRestriction.CurrentFoodPolicy != caravanPolicy;
        }

        protected bool IsCaravanMatch(Pawn pawn)
        {
            var caravanPolicy = PolicyUtils.GetStoredCaravanPolicy(pawn);
            return caravanPolicy != null && pawn.IsCaravanMember() && pawn.foodRestriction.CurrentFoodPolicy == caravanPolicy;
        }

        protected virtual IEnumerable<Widgets.DropdownMenuElement<FoodPolicy>> Button_GenerateMenu(Pawn pawn)
        {
            foreach (var policy in Current.Game.foodRestrictionDatabase.AllFoodRestrictions)
            {
                yield return new Widgets.DropdownMenuElement<FoodPolicy>()
                {
                    option = new FloatMenuOption(policy.label, () => SetPolicy(pawn, policy)),
                    payload = policy
                };
            }

            yield return new Widgets.DropdownMenuElement<FoodPolicy>()
            {
                option = new FloatMenuOption("Edit...", () =>
                {
                    Find.WindowStack.Add(new Dialog_ManageFoodPolicies(null));
                }),
                payload = null
            };
        }

        public override int GetMinWidth(PawnTable table) => Mathf.Max(base.GetMinWidth(table), 100);
        public override int GetOptimalWidth(PawnTable table) => Mathf.Clamp(150, GetMinWidth(table), GetMaxWidth(table));

        public override int Compare(Pawn a, Pawn b)
        {
            return GetValueToCompare(a).CompareTo(GetValueToCompare(b));
        }

        protected virtual int GetValueToCompare(Pawn pawn)
        {
            if (pawn.foodRestriction?.CurrentFoodPolicy == null) return int.MinValue;
            return GetPolicy(pawn).id;
        }
    }

    public class PawnColumnWorker_CaravanFoodPolicy : PawnColumnWorker_FoodPolicyBase
    {
        protected override FoodPolicy GetPolicy(Pawn pawn) => PolicyUtils.GetStoredCaravanPolicy(pawn);
        protected override void SetPolicy(Pawn pawn, FoodPolicy policy) => PolicyUtils.SetStoredCaravanPolicy(pawn, policy);

        public override int GetMinHeaderHeight(PawnTable table) => Mathf.Max(base.GetMinHeaderHeight(table), TopAreaHeight);

        protected override bool HasMatch(Pawn pawn) => IsCaravanMatch(pawn);
        protected override bool HasMismatch(Pawn pawn) => IsCaravanMismatch(pawn);
        protected override void ResolveMismatch(Pawn pawn)
        {
            // Set Current to the Caravan policy
            pawn.foodRestriction.CurrentFoodPolicy = GetPolicy(pawn);
        }
    }

    public class PawnColumnWorker_HomeFoodPolicy : PawnColumnWorker_FoodPolicyBase
    {
        protected override FoodPolicy GetPolicy(Pawn pawn) => PolicyUtils.GetStoredHomePolicy(pawn) ?? pawn.foodRestriction.CurrentFoodPolicy;
        protected override void SetPolicy(Pawn pawn, FoodPolicy policy) => PolicyUtils.SetStoredHomePolicy(pawn, policy);

        protected override bool HasMatch(Pawn pawn) => IsHomeMatch(pawn);
        protected override bool HasMismatch(Pawn pawn) => IsHomeMismatch(pawn);
        protected override void ResolveMismatch(Pawn pawn)
        {
            // Set Current to the Home policy
            pawn.foodRestriction.CurrentFoodPolicy = GetPolicy(pawn);
        }
    }

    public class PawnColumnWorker_CurrentFoodPolicy : PawnColumnWorker_FoodPolicyBase
    {
        protected override FoodPolicy GetPolicy(Pawn pawn) => pawn.foodRestriction.CurrentFoodPolicy;
        protected override void SetPolicy(Pawn pawn, FoodPolicy policy)
        {
            pawn.foodRestriction.CurrentFoodPolicy = policy;
        }
        public override int GetMinHeaderHeight(PawnTable table) => Mathf.Max(base.GetMinHeaderHeight(table), TopAreaHeight);

        public override void DoHeader(Rect rect, PawnTable table)
        {
            base.DoHeader(rect, table);
            
            var buttonRect = new Rect(rect.x, rect.y + (rect.height - TopAreaHeight), rect.width * 3f, ManageFoodPoliciesButtonHeight);
            if (Widgets.ButtonText(buttonRect, "Manage food policies"))
            {
                Find.WindowStack.Add(new Dialog_ManageFoodPolicies(null));
            }
        }

        protected override bool HasMatch(Pawn pawn)
        {
            if (pawn.Map != null && pawn.Map.IsPlayerHome)
            {
                return IsHomeMatch(pawn);
            }
            if (pawn.IsCaravanMember())
            {
                return IsCaravanMatch(pawn);
            }
            return false;
        }

        protected override bool HasMismatch(Pawn pawn)
        {
            if (pawn.Map != null && pawn.Map.IsPlayerHome)
            {
                return IsHomeMismatch(pawn);
            }
            if (pawn.IsCaravanMember())
            {
                return IsCaravanMismatch(pawn);
            }
            return false;
        }

        protected override void ResolveMismatch(Pawn pawn)
        {
            // Push Current policy to the mismatched stored policy
            if (IsHomeMismatch(pawn))
            {
                PolicyUtils.SetStoredHomePolicy(pawn, pawn.foodRestriction.CurrentFoodPolicy);
            }
            else if (IsCaravanMismatch(pawn))
            {
                PolicyUtils.SetStoredCaravanPolicy(pawn, pawn.foodRestriction.CurrentFoodPolicy);
            }
        }
    }
}