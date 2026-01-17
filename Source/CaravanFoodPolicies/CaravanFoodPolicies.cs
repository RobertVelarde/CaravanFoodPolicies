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

                    PolicyUtils.SaveHomePolicy(pawn);
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
            Warning("Could not update food policy for '" + pawn.NameShortColored + "'. Their current food policy is '" + pawn.foodRestriction.CurrentFoodPolicy.label + "'.");
        }

        public static void Departure(Pawn pawn)
        {
            Message("'" + pawn.NameShortColored + "' departed in a caravan. Their food policy has been updated to '" + pawn.foodRestriction.CurrentFoodPolicy.label + "'.");
        }

        public static void Arrival(Pawn pawn)
        {
            Message("'" + pawn.NameShortColored + "' returned home. Their food policy has been reset back to '" + pawn.foodRestriction.CurrentFoodPolicy.label + "'.");
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
            if (data.RetainedCaravanData.TryGetValue(pawn.GetUniqueLoadID(), out var label))
            {
                return GetPolicyByLabel(label) ?? DefaultPolicy;
            }

            // Fallback: Pawn has never been touched by this mod, return Default
            return DefaultPolicy;
        }

        /// <summary>
        /// Gets the stored Home policy for a pawn. Returns null if not found.
        /// </summary>
        public static FoodPolicy GetStoredHomePolicy(Pawn pawn)
        {
            var data = Data;
            if (data == null) return null;

            if (data.RetainedHomeData.TryGetValue(pawn.GetUniqueLoadID(), out var label))
            {
                return GetPolicyByLabel(label);
            }
            return null;
        }

        /// <summary>
        /// Saves the pawn's *current* active policy as their "Home" policy.
        /// </summary>
        public static void SaveHomePolicy(Pawn pawn)
        {
            var data = Data;
            if (data == null || pawn.foodRestriction?.CurrentFoodPolicy == null) return;

            data.RetainedHomeData[pawn.GetUniqueLoadID()] = pawn.foodRestriction.CurrentFoodPolicy.label;
        }

        /// <summary>
        /// Updates the saved "Caravan" preference for a pawn.
        /// </summary>
        public static void SetStoredCaravanPolicy(Pawn pawn, FoodPolicy policy)
        {
            var data = Data;
            if (data == null) return;

            data.RetainedCaravanData[pawn.GetUniqueLoadID()] = policy.label;
        }

        // Helper to find policy object from string label
        private static FoodPolicy GetPolicyByLabel(string label)
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
        public Dictionary<string, string> RetainedCaravanData = new Dictionary<string, string>();
        public Dictionary<string, string> RetainedHomeData = new Dictionary<string, string>();

        // Lists for Scribe saving
        private List<string> CaravanPawnId;
        private List<string> CaravanFoodPolicyLabel;
        private List<string> HomePawnId;
        private List<string> HomeFoodPolicyLabel;

        public CaravanFoodPoliciesData(World world) : base(world) { }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref RetainedCaravanData, "RetainedCaravanData", LookMode.Value, LookMode.Value, ref CaravanPawnId, ref CaravanFoodPolicyLabel);
            Scribe_Collections.Look(ref RetainedHomeData, "RetainedHomeData", LookMode.Value, LookMode.Value, ref HomePawnId, ref HomeFoodPolicyLabel);
        }
    }
}

namespace RimWorld
{
    using CaravanFoodPolicies; // Import our utils

    public class PawnColumnWorker_CaravanFoodPolicy : PawnColumnWorker
    {
        private const int TopAreaHeight = 65;
        public const int ManageFoodPoliciesButtonHeight = 32;

        public override void DoHeader(Rect rect, PawnTable table)
        {
            base.DoHeader(rect, table);
            MouseoverSounds.DoRegion(rect);

            var buttonRect = new Rect(rect.x, rect.y + (rect.height - TopAreaHeight), Mathf.Min(rect.width, 360f), ManageFoodPoliciesButtonHeight);
            if (Widgets.ButtonText(buttonRect, "Manage caravan food policies"))
            {
                Find.WindowStack.Add(new Dialog_ManageFoodPolicies(null));
            }
        }

        public override void DoCell(Rect rect, Pawn pawn, PawnTable table)
        {
            if (pawn.foodRestriction == null) return;

            var currentSavedPolicy = PolicyUtils.GetStoredCaravanPolicy(pawn);
            Rect dropdownRect = new Rect(rect.x, rect.y + 2f, rect.width, rect.height - 4f);

            Widgets.Dropdown(
                dropdownRect,
                pawn,
                _ => currentSavedPolicy,
                Button_GenerateMenu,
                currentSavedPolicy.label.Truncate(dropdownRect.width),
                dragLabel: currentSavedPolicy.label,
                paintable: true
            );
        }

        private IEnumerable<Widgets.DropdownMenuElement<FoodPolicy>> Button_GenerateMenu(Pawn pawn)
        {
            foreach (var policy in Current.Game.foodRestrictionDatabase.AllFoodRestrictions)
            {
                yield return new Widgets.DropdownMenuElement<FoodPolicy>()
                {
                    option = new FloatMenuOption(policy.label, () => PolicyUtils.SetStoredCaravanPolicy(pawn, policy)),
                    payload = policy
                };
            }

            // Add the "Edit..." option at the very bottom
            yield return new Widgets.DropdownMenuElement<FoodPolicy>()
            {
                option = new FloatMenuOption("Edit...", () =>
                {
                    Find.WindowStack.Add(new Dialog_ManageFoodPolicies(null));
                }),
                payload = null
            };
        }

        // Standard overrides
        public override int GetMinWidth(PawnTable table) => Mathf.Max(base.GetMinWidth(table), 194);
        public override int GetOptimalWidth(PawnTable table) => Mathf.Clamp(251, GetMinWidth(table), GetMaxWidth(table));
        public override int GetMinHeaderHeight(PawnTable table) => Mathf.Max(base.GetMinHeaderHeight(table), TopAreaHeight);

        public override int Compare(Pawn a, Pawn b)
        {
            return GetValueToCompare(a).CompareTo(GetValueToCompare(b));
        }

        private int GetValueToCompare(Pawn pawn)
        {
            if (pawn.foodRestriction?.CurrentFoodPolicy == null) return int.MinValue;
            return PolicyUtils.GetStoredCaravanPolicy(pawn).id;
        }
    }
}