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

        [HarmonyPatch(typeof(Dialog_FormCaravan), "DaysWorthOfFood", MethodType.Getter)]
        static class Dialog_FormCaravan_DaysWorthOfFood_Patch
        {
            [HarmonyBefore(new string[] { "SmashPhil.VehicleFramework" })]
            static void Prefix(List<TransferableOneWay> ___transferables, bool ___daysWorthOfFoodDirty, out Dictionary<Pawn, FoodPolicy> __state)
            {
                __state = null;

                // PERFORMANCE OPTIMIZATION:
                // If the dirty flag is false, the game will just return a cached number.
                // We should NOT swap policies in this case, or we waste CPU cycles every frame.
                if (___daysWorthOfFoodDirty)
                {
                    __state = PolicyUtils.ApplyCaravanPolicies(___transferables);
                }
            }

            static void Finalizer(Dictionary<Pawn, FoodPolicy> __state)
            {
                // Restore policies (only runs if we actually swapped them in Prefix)
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

                foreach (var pawn in __result.pawns)
                {
                    if (!pawn.RaceProps.Humanlike) continue;

                    var caravanPolicy = PolicyUtils.GetStoredCaravanPolicy(pawn);
                    if (caravanPolicy == null)
                    {
                        CFPLog.Missing(pawn);
                        continue;
                    }

                    // Update the pawn's food policy
                    pawn.foodRestriction.CurrentFoodPolicy = caravanPolicy;
                    CFPLog.Departure(pawn);
                }
            }
        }

        [HarmonyPatch(typeof(CaravanArrivalAction_Enter), nameof(CaravanArrivalAction_Enter.Arrived))]
        static class CaravanArrivalAction_Enter_Patch
        {
            static void Prefix(Caravan caravan, ref MapParent ___mapParent)
            {
                if (___mapParent?.Map == null || !___mapParent.Map.IsPlayerHome) return;

                foreach (var pawn in caravan.pawns)
                {
                    if (!pawn.RaceProps.Humanlike) continue;

                    var homePolicy = PolicyUtils.GetStoredHomePolicy(pawn);
                    if (homePolicy == null)
                    {
                        CFPLog.Missing(pawn);
                        continue;
                    }

                    // Update the pawn's food policy
                    pawn.foodRestriction.CurrentFoodPolicy = homePolicy;
                    CFPLog.Arrival(pawn);
                }
            }
        }
    }

    internal static class CFPLog
    {
        private const string Prefix = "[CaravanFoodPolicies]";

        public static void Missing(Pawn pawn)
        {
            Warning("Could not update food policy for '" + pawn.NameShortColored + "'. Their current food policy is '" + pawn.foodRestriction.CurrentFoodPolicy.label + "'.")
;
        }

        public static void Departure(Pawn pawn)
        {
            Message("'" + pawn.NameShortColored + "' departed in a caravan. Their food policy has been updated to '" + pawn.foodRestriction.CurrentFoodPolicy.label + "'.")
;
        }

        public static void Arrival(Pawn pawn)
        {
            Message("'" + pawn.NameShortColored + "' returned home. Their food policy has been reset back to '" + pawn.foodRestriction.CurrentFoodPolicy.label + "'.")
;
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
        // Centralized getter for the WorldComponent
        private static CaravanFoodPoliciesData Data => Find.World.GetComponent<CaravanFoodPoliciesData>();

        /// <summary>
        /// Gets the stored Caravan policy for a pawn. 
        /// Returns System Default (Lavish) if no custom policy is saved.
        /// </summary>
        public static FoodPolicy GetStoredCaravanPolicy(Pawn pawn)
        {
            var data = Data;
            if (data == null) return DefaultPolicy;

            // If we have a specific saved label, try to find it
            if (data.RetainedCaravanDataIds.TryGetValue(pawn.GetUniqueLoadID(), out var id))
            {
                return GetPolicyById(id) ?? DefaultPolicy;
            }

            // Fallback: Pawn has never been touched by this mod, return Default
            return DefaultPolicy;
        }

        /// <summary>
        /// Gets the stored Home policy for a pawn. 
        /// Initializes to Current policy if not found.
        /// </summary>
        public static FoodPolicy GetStoredHomePolicy(Pawn pawn)
        {
            var data = Data;
            if (data == null) return DefaultPolicy;

            if (data.RetainedHomeDataIds.TryGetValue(pawn.GetUniqueLoadID(), out var id))
            {
                return GetPolicyById(id) ?? DefaultPolicy;
            }

            // Initialization: Save current policy as Home policy
            var current = pawn.foodRestriction?.CurrentFoodPolicy ?? DefaultPolicy;
            data.RetainedHomeDataIds[pawn.GetUniqueLoadID()] = current.id;
            return current;
        }

        /// <summary>
        /// Saves the pawn's *current* active policy as their "Home" policy.
        /// </summary>
        public static void SaveHomePolicy(Pawn pawn)
        {
            var data = Data;
            if (data == null || pawn.foodRestriction?.CurrentFoodPolicy == null) return;

            // NEW: If the user has manually set a Home policy via the UI (it exists in the dictionary),
            // do NOT overwrite it with the current transient policy.
            if (data.RetainedHomeDataIds.ContainsKey(pawn.GetUniqueLoadID())) return;

            data.RetainedHomeDataIds[pawn.GetUniqueLoadID()] = pawn.foodRestriction.CurrentFoodPolicy.id;
        }

        /// <summary>
        /// Updates the saved "Caravan" preference for a pawn.
        /// </summary>
        public static void SetStoredCaravanPolicy(Pawn pawn, FoodPolicy policy)
        {
            var data = Data;
            if (data == null) return;

            data.RetainedCaravanDataIds[pawn.GetUniqueLoadID()] = policy.id;
        }

        /// <summary>
        /// Updates the saved "Home" preference for a pawn.
        /// </summary>
        public static void SetStoredHomePolicy(Pawn pawn, FoodPolicy policy)
        {
            var data = Data;
            if (data == null) return;

            data.RetainedHomeDataIds[pawn.GetUniqueLoadID()] = policy.id;
        }

        // Helper to find policy object from int id
        private static FoodPolicy GetPolicyById(int id)
        {
            return Current.Game.foodRestrictionDatabase.AllFoodRestrictions
                .FirstOrFallback(x => x.id == id, null);
        }

        // Helper to find policy object from string label (Used for Migration Only)
        public static FoodPolicy GetPolicyByLabel(string label)
        {
            return Current.Game.foodRestrictionDatabase.AllFoodRestrictions
                .FirstOrFallback(x => x.label == label, null);
        }

        private static FoodPolicy DefaultPolicy => Current.Game.foodRestrictionDatabase.DefaultFoodRestriction();

        /// <summary>
        /// Temporarily applies Caravan policies to all pawns in the transfer list.
        /// Returns a dictionary containing their original "Home" policies for restoration.
        /// </summary>
        public static Dictionary<Pawn, FoodPolicy> ApplyCaravanPolicies(List<TransferableOneWay> transferables)
        {
            var state = new Dictionary<Pawn, FoodPolicy>();
            if (transferables == null) return state;

            foreach (var t in transferables)
            {
                // Only affect Humanlike pawns that are actually joining the caravan (Count > 0)
                if (t.AnyThing is Pawn pawn &&
                    pawn.RaceProps.Humanlike &&
                    t.CountToTransfer > 0)
                {
                    // Save current Home policy
                    state[pawn] = pawn.foodRestriction.CurrentFoodPolicy;

                    // Apply Caravan policy
                    var caravanPolicy = GetStoredCaravanPolicy(pawn);
                    if (caravanPolicy != null)
                    {
                        pawn.foodRestriction.CurrentFoodPolicy = caravanPolicy;
                    }
                }
            }
            return state;
        }

        /// <summary>
        /// Restores the original policies saved in the state dictionary.
        /// </summary>
        public static void RestorePolicies(Dictionary<Pawn, FoodPolicy> state)
        {
            if (state == null) return;
            foreach (var kvp in state)
            {
                kvp.Key.foodRestriction.CurrentFoodPolicy = kvp.Value;
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
                CFPLog.Message("Upgraded CaravanFoodPoliciesData from v" + startingVersion + " to v" + LatestVersion);
            }
        }

        private void MigrateV1_PolicyLabelsToIds()
        {
            // Temporary buffers for legacy values
            Dictionary<string, string> legacyCaravanData = null;
            Dictionary<string, string> legacyHomeData = null;
            List<string> tempKeys = null;
            List<string> tempValues = null;

            // Attempt to read the OLD labels ("RetainedCaravanData") into string dictionaries
            Scribe_Collections.Look(ref legacyCaravanData, "RetainedCaravanData", LookMode.Value, LookMode.Value, ref tempKeys, ref tempValues);
            Scribe_Collections.Look(ref legacyHomeData, "RetainedHomeData", LookMode.Value, LookMode.Value, ref tempKeys, ref tempValues);

            // Migrate Caravan Data
            if (legacyCaravanData != null)
            {
                if (RetainedCaravanDataIds == null) RetainedCaravanDataIds = new Dictionary<string, int>();
                foreach (var kvp in legacyCaravanData)
                {
                    var policy = PolicyUtils.GetPolicyByLabel(kvp.Value);
                    if (policy != null) RetainedCaravanDataIds[kvp.Key] = policy.id;
                }
            }

            // Migrate Home Data
            if (legacyHomeData != null)
            {
                if (RetainedHomeDataIds == null) RetainedHomeDataIds = new Dictionary<string, int>();
                foreach (var kvp in legacyHomeData)
                {
                    var policy = PolicyUtils.GetPolicyByLabel(kvp.Value);
                    if (policy != null) RetainedHomeDataIds[kvp.Key] = policy.id;
                }
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
            // If homePolicy is null, no custom home policy is set, so no mismatch.
            return homePolicy != null && (pawn.Map?.IsPlayerHome == true) && pawn.foodRestriction.CurrentFoodPolicy != homePolicy;
        }

        protected bool IsHomeMatch(Pawn pawn)
        {
            var homePolicy = PolicyUtils.GetStoredHomePolicy(pawn);
            // If homePolicy is null, no custom home policy is set, so no mismatch.
            return homePolicy != null && (pawn.Map?.IsPlayerHome == true) && pawn.foodRestriction.CurrentFoodPolicy == homePolicy;
        }

        protected bool IsCaravanMismatch(Pawn pawn)
        {
            var caravanPolicy = PolicyUtils.GetStoredCaravanPolicy(pawn);
            return pawn.IsCaravanMember() && pawn.foodRestriction.CurrentFoodPolicy != caravanPolicy;
        }

        protected bool IsCaravanMatch(Pawn pawn)
        {
            var caravanPolicy = PolicyUtils.GetStoredCaravanPolicy(pawn);
            return pawn.IsCaravanMember() && pawn.foodRestriction.CurrentFoodPolicy == caravanPolicy;
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

    // 1. Column for editing the "Caravan" Policy (Existing)
    public class PawnColumnWorker_CaravanFoodPolicy : PawnColumnWorker_FoodPolicyBase
    {
        protected override FoodPolicy GetPolicy(Pawn pawn) => PolicyUtils.GetStoredCaravanPolicy(pawn);
        protected override void SetPolicy(Pawn pawn, FoodPolicy policy) => PolicyUtils.SetStoredCaravanPolicy(pawn, policy);

        public override int GetMinHeaderHeight(PawnTable table) => Mathf.Max(base.GetMinHeaderHeight(table), TopAreaHeight);

        protected override bool HasMatch(Pawn pawn)
        {
            return IsCaravanMatch(pawn);
        }

        protected override bool HasMismatch(Pawn pawn)
        {
            return IsCaravanMismatch(pawn);
        }

        protected override void ResolveMismatch(Pawn pawn)
        {
            // Set Current to the Caravan policy
            pawn.foodRestriction.CurrentFoodPolicy = GetPolicy(pawn);
        }
    }

    // 2. Column for editing the "Home" Policy (New)
    public class PawnColumnWorker_HomeFoodPolicy : PawnColumnWorker_FoodPolicyBase
    {
        // NOTE: Keeping the fallback removal from previous steps implicitly if user applied it, 
        // but adhering to context provided in prompt which included the fallback logic. 
        // ResolveMismatch uses GetPolicy which uses GetStoredHomePolicy. 
        protected override FoodPolicy GetPolicy(Pawn pawn) => PolicyUtils.GetStoredHomePolicy(pawn) ?? pawn.foodRestriction.CurrentFoodPolicy;
        protected override void SetPolicy(Pawn pawn, FoodPolicy policy) => PolicyUtils.SetStoredHomePolicy(pawn, policy);

        protected override bool HasMatch(Pawn pawn)
        {
            return IsHomeMatch(pawn);
        }

        protected override bool HasMismatch(Pawn pawn)
        {
            return IsHomeMismatch(pawn);
        }

        protected override void ResolveMismatch(Pawn pawn)
        {
            // Set Current to the Home policy
            pawn.foodRestriction.CurrentFoodPolicy = GetPolicy(pawn);
        }
    }

    // 3. Column for "Current" Food Policy (Rename of Vanilla)
    public class PawnColumnWorker_CurrentFoodPolicy : PawnColumnWorker_FoodPolicyBase
    {
        protected override FoodPolicy GetPolicy(Pawn pawn) => pawn.foodRestriction.CurrentFoodPolicy;
        protected override void SetPolicy(Pawn pawn, FoodPolicy policy) => pawn.foodRestriction.CurrentFoodPolicy = policy;

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