using RoR2;
using BepInEx;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using UnityEngine;
using static RoR2.Console;
using UnityEngine.SceneManagement;

[assembly: HG.Reflection.SearchableAttribute.OptIn]

namespace FilteredStageLogger
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]

    public class Main : BaseUnityPlugin
    {
        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginAuthor = "cyanblur";
        public const string PluginName = "FilteredStageLogger";
        public const string PluginVersion = "1.0.7";
        public static readonly string path = $"{Assembly.GetExecutingAssembly().Location}/../../../ItemLogs.log";

        public static BepInEx.Configuration.ConfigEntry<LogLevel> logLevel { get; set; }
        public static BepInEx.Configuration.ConfigEntry<bool> saveToFile { get; set; }
        public static BepInEx.Configuration.ConfigEntry<bool> orderByTier { get; set; }
        public static BepInEx.Configuration.ConfigEntry<bool> logOnlyOnCommand { get; set; }
        public static BepInEx.Configuration.ConfigEntry<Color> secondaryItemColor { get; set; }
        public static BepInEx.Configuration.ConfigEntry<string> secondaryItemList { get; set; }
        public static BepInEx.Configuration.ConfigEntry<Color> primaryItemColor { get; set; }
        public static BepInEx.Configuration.ConfigEntry<string> primaryItemList { get; set; }
        private static List<string> secondaryItemListSplit = new List<string>();
        private static List<string> primaryItemListSplit = new List<string>();
        private static List<LocationInfo> locations = new();
        HashSet<int> alreadyLoggedObjs = new();
        private static int stagesLogged = -1;
        private static Dictionary<int, Dictionary<string, List<string>>> stageItems = new();
        private static Dictionary<int, Dictionary<string, List<LocationInfo>>> stageLocations = new();
        private static Dictionary<int, bool> loggedStages = new();
        private static Dictionary<int, Dictionary<string, List<int>>> openedObjects = new();
        private static Dictionary<int, string> mainStage = new();

        public void Awake()
        {
            Log.Init(Logger);

            logLevel = Config.Bind<LogLevel>(
                "Functionality",
                "Log Level",
                LogLevel.ItemsAndSources,
                "How much information the game will provide to the console. JSON doesn't work rn"
            );

            saveToFile = Config.Bind<bool>(
                "Functionality",
                "Save to File?",
                true,
                "Whether or not to also save the data to a log file"
            );

            orderByTier = Config.Bind<bool>(
                "Functionality",
                "Order by rarity?",
                false,
                "Whether or not items are ordered by spawn order (distance from 0,0,0) or grouped by tiers"
            );

            logOnlyOnCommand = Config.Bind<bool>(
                "Functionality",
                "Only log on command?",
                true,
                $"If enabled, will only output items when the debug command {_description} is used"
            );

            secondaryItemColor = Config.Bind<Color>(
                "Item List Settings",
                "Secondary Color",
                Color.green,
                "Hex color of secondary items to log."
            );

            secondaryItemList = Config.Bind<string>(
                "Item List Settings",
                "Secondary Item List",
                "",
                "List of items to give secondary highlighting, comma separated. Use & to combine items."
            );

            primaryItemColor = Config.Bind<Color>(
                "Item List Settings",
                "Primary Color",
                Color.red,
                "Hex color of primary items to log."
            );

            primaryItemList = Config.Bind<string>(
                "Item List Settings",
                "Primary Item List",
                "",
                "List of items to mark as primary, comma separated. Use & to combine items."
            );

            File.WriteAllText(path, "Start Instance of new Game"); // "clear" text file

            On.RoR2.Run.Start += AppendNewRun;
            On.RoR2.Run.BeginStage += BeginStage;
            On.RoR2.ChestBehavior.Roll += LogChestRoll;
            On.RoR2.ChestBehavior.Open += ChestOpened;
            On.RoR2.ShopTerminalBehavior.SetPickupIndex += SetPrinterIndex;
            On.RoR2.ShopTerminalBehavior.GenerateNewPickupServer += GetMultishopItems;
            On.RoR2.ShopTerminalBehavior.DropPickup += (o, s) =>
            {
                o(s);
                MarkInteraction(s.gameObject.GetHashCode());
            };
            On.RoR2.OptionChestBehavior.Roll += LogOptionLoot;
            On.RoR2.OptionChestBehavior.Open += (o, s) =>
            {
                o(s);
                MarkInteraction(s.gameObject.GetHashCode());
            };
            On.RoR2.MultiShopController.OnPurchase += OpenedOption;
            On.RoR2.ShrineChanceBehavior.Start += LogChanceShrineLoot;
            On.RoR2.ShrineChanceBehavior.AddShrineStack += ChanceHit;
            On.RoR2.RouletteChestController.OnEnable += LogAdaptiveLoot;
            On.RoR2.RouletteChestController.HandleInteractionServer += AdaptiveOpened;
            On.RoR2.BossGroup.Awake += BossAwake;
            SceneDirector.onPostPopulateSceneServer += FinalEvent;

            Log.LogDebug(PluginGUID + " Awake Done");
        }

        private const string _description = "`check_stage [stage_number] [stage_name] [adaptive]`";
        [ConCommand(commandName = "check_stage", flags = ConVarFlags.None, helpText = _description)]
        public static void CheckStage(ConCommandArgs args)
        {
            if (stagesLogged == -1)
            {
                Debug.Log($"check_stage must be used after a run has started");
                return;
            }

            int stageNum = -1;
            string stageName = null;

            bool logAdaptive = false;
            List<string> finalArgs = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                if (args.GetArgString(i) == "adaptive")
                {
                    logAdaptive = true;
                }
                else
                {
                    finalArgs.Add(args.GetArgString(i));
                }
            }

            if (finalArgs.Count == 0)
            {
                Debug.Log($"check_stage was used without a stage number, outputting current stage ({Run.instance.stageClearCount + 1})");
                stageNum = Run.instance.stageClearCount + 1;
            }
            else
            {
                if (!int.TryParse(finalArgs[0], out stageNum))
                {
                    Debug.Log($"check_stage must have a number after it. A stage name can be used afterwards to log hidden realms. {_description}");
                    return;
                }
                try
                {
                    stageName = finalArgs[1];
                }
                catch { }
            }
            LogByStageNumber(stageNum, stageName, logAdaptive);
        }

        public static void LogSomeData()
        {
            if (orderByTier.Value) locations.Sort((x, y) => {
                if (!x.isItem || !y.isItem)
                {
                    return 0;
                }
                else
                {
                    return ItemCatalog.GetItemDef(PickupCatalog.GetPickupDef(x.item).itemIndex).tier.CompareTo(ItemCatalog.GetItemDef(PickupCatalog.GetPickupDef(y.item).itemIndex).tier);
                }

            });

            string file = "";

            if (Run.instance is not null)
            {
                if (Run.instance.stageClearCount > stagesLogged)
                {
                    file += "\n\n\nStage " + (Run.instance.stageClearCount + 1);
                    stagesLogged = Run.instance.stageClearCount;
                }
            }

            foreach (var loc in locations)
            {
                file += "\n" + loc.AsString();
            }
            if (!logOnlyOnCommand.Value) LogByStageNumber(stagesLogged + 1);

            if (saveToFile.Value) File.AppendAllText(path, file);
            locations.Clear();
        }

        public static void LogByStageNumber(int stage, string stageName = null, bool logAdaptive = false)
        {
            secondaryItemListSplit = secondaryItemList.Value.Split(',').ToList();
            primaryItemListSplit = primaryItemList.Value.Split(',').ToList();
            if (!stageLocations.ContainsKey(stage) || stageLocations[stage] == null || stageLocations[stage].Count == 0)
            {
                Debug.LogError($"Stage {stage} not recorded.");
            }
            else
            {
                loggedStages[stage] = true;
                if (stageName == null)
                {
                    stageName = mainStage[stage];
                }
                if (!stageLocations[stage].ContainsKey(stageName))
                {
                    Debug.LogError($"{stageName} not recorded in stage {stage}. Stages found: {string.Join(", ", stageLocations[stage].Keys)}");
                }
                else
                {
                    bool notifyAdaptive = false;
                    foreach (var location in stageLocations[stage][stageName])
                    {
                        if (location.objectType.StartsWith("CasinoChest") && !logAdaptive)
                        {
                            notifyAdaptive = true;
                            continue;
                        }
                        try
                        {
                            Debug.Log(location.GetDesiredColor(stageItems[stage][stageName], openedObjects[stage][stageName]));
                        }
                        catch (Exception e) { }
                    }
                    if (notifyAdaptive)
                    {
                        Debug.Log($"Adaptive chests found but omitted from the log. Add the arg `adaptive` to your command to view.");
                    }
                    if (stageLocations[stage].Keys.Count > 1)
                    {
                        Debug.Log($"Other stages found: {string.Join(", ", stageLocations[stage].Keys.Where(s => s != stageName))}");
                    }
                }
            }
        }

        private void AppendNewRun(On.RoR2.Run.orig_Start orig, Run self)
        {
            orig(self);

            if (saveToFile.Value) File.AppendAllText(path, $"\n\nNew Run Started at {DateTime.Now}");

            stagesLogged = -1;
            stageItems = new Dictionary<int, Dictionary<string, List<string>>>();
            stageLocations = new Dictionary<int, Dictionary<string, List<LocationInfo>>>();
            loggedStages = new Dictionary<int, bool>();
        }

        private void BeginStage(On.RoR2.Run.orig_BeginStage orig, Run self)
        {
            if (!stageLocations.ContainsKey(Run.instance.stageClearCount + 1))
                stageLocations[Run.instance.stageClearCount + 1] = new();
            stageLocations[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName] = new();
            if (!stageItems.ContainsKey(Run.instance.stageClearCount + 1))
                stageItems[Run.instance.stageClearCount + 1] = new();
            stageItems[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName] = new();
            if (!openedObjects.ContainsKey(Run.instance.stageClearCount + 1))
                openedObjects[Run.instance.stageClearCount + 1] = new();
            openedObjects[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName] = new();
            loggedStages[Run.instance.stageClearCount + 1] = false;
            mainStage[Run.instance.stageClearCount + 1] = SceneCatalog.currentSceneDef.baseSceneName;

            alreadyLoggedObjs.Clear();
            orig(self);
        }

        private static void MarkInteraction(int id)
        {
            if (!openedObjects.ContainsKey(Run.instance.stageClearCount + 1))
                openedObjects[Run.instance.stageClearCount + 1] = new();
            if (!openedObjects[Run.instance.stageClearCount + 1].ContainsKey(SceneCatalog.currentSceneDef.baseSceneName))
                openedObjects[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName] = new List<int>();
            openedObjects[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName].Add(id);
        }

        private void GetMultishopItems(On.RoR2.ShopTerminalBehavior.orig_GenerateNewPickupServer orig, ShopTerminalBehavior self)
        {
            orig(self);

            if (alreadyLoggedObjs.Contains(self.gameObject.GetHashCode())) return;
            else alreadyLoggedObjs.Add(self.gameObject.GetHashCode());

            if (self.pickupIndex.value == -1) return; // BAD_PICKUP_INDEX

            Append(self.gameObject.name, self.gameObject.GetHashCode(), self.pickupIndex, self.transform.position.x, self.transform.position.y);
        }

        private void LogOptionLoot(On.RoR2.OptionChestBehavior.orig_Roll orig, OptionChestBehavior self)
        {
            orig(self);
            int count = 1;
            foreach (var itemPickup in self.generatedDrops)
            {
                Append(self.gameObject.name + $" #{count}", self.gameObject.GetHashCode(), itemPickup, self.gameObject.transform.position.x, self.gameObject.transform.position.y);
                count++;
            }
        }
        
        private void OpenedOption(On.RoR2.MultiShopController.orig_OnPurchase orig, MultiShopController self, Interactor interactor, PurchaseInteraction pi)
        {
            orig(self, interactor, pi);

            MarkInteraction(self.gameObject.GetHashCode());
        }

        private void LogChanceShrineLoot(On.RoR2.ShrineChanceBehavior.orig_Start orig, ShrineChanceBehavior self)
        {
            const int SHRINE_MAX = 2;
            orig(self);

            ulong state0 = self.rng.state0, state1 = self.rng.state1;
            int hits = 0, succcs = 0;
            while (succcs < SHRINE_MAX)
            {
                hits++;
                if (self.rng.nextNormalizedFloat > self.failureChance)
                {
                    Append(self.gameObject.name + " #" + hits, self.gameObject.GetHashCode(), self.dropTable.GenerateDrop(self.rng), self.transform.position.x, self.transform.position.y);
                    succcs++;
                }
            }
            self.rng.state0 = state0;
            self.rng.state1 = state1; // reset RNG
        }

        private void ChanceHit(On.RoR2.ShrineChanceBehavior.orig_AddShrineStack orig, ShrineChanceBehavior self, Interactor activator)
        {
            orig(self, activator);

            MarkInteraction(self.gameObject.GetHashCode());
        }

        private void LogAdaptiveLoot(On.RoR2.RouletteChestController.orig_OnEnable orig, RouletteChestController self)
        {
            orig(self);

            if (alreadyLoggedObjs.Contains(self.gameObject.GetHashCode())) return;
            else alreadyLoggedObjs.Add(self.gameObject.GetHashCode());

            RoR2Application.onNextUpdate += () =>
            {
                const int CASINO_MAX = 30;
                ulong state0 = self.rng.state0, state1 = self.rng.state1;
                int entries = 0;
                while (entries < CASINO_MAX)
                {
                    entries++;
                    Append(self.gameObject.name + " #" + entries, self.gameObject.GetHashCode(), self.dropTable.GenerateDrop(self.rng), self.transform.position.x, self.transform.position.y);
                }
                self.rng.state0 = state0;
                self.rng.state1 = state1; // reset RNG
            };
        }

        private void AdaptiveOpened(On.RoR2.RouletteChestController.orig_HandleInteractionServer orig, RouletteChestController self, Interactor activator)
        {
            orig(self, activator);

            if (self.previousEntryIndexClient > -1)
            {
                for (int i = 0; i < self.previousEntryIndexClient + 1; i++)
                {
                    MarkInteraction(self.gameObject.GetHashCode());
                }
            }
            else
            {
                MarkInteraction(self.gameObject.GetHashCode());
            }
        }

        private void SetPrinterIndex(On.RoR2.ShopTerminalBehavior.orig_SetPickupIndex orig, ShopTerminalBehavior self, PickupIndex newPickupIndex, bool newHidden)
        {
            orig(self, newPickupIndex, newHidden);

            if (alreadyLoggedObjs.Contains(self.gameObject.GetHashCode())) return;
            else if (newPickupIndex.isValid) alreadyLoggedObjs.Add(self.gameObject.GetHashCode());

            if (self.pickupIndex == PickupCatalog.FindPickupIndex(ItemIndex.None)) return;

            Append(self.gameObject.name, self.gameObject.GetHashCode(), self.pickupIndex, self.transform.position.x, self.transform.position.y);
        }

        private void LogChestRoll(On.RoR2.ChestBehavior.orig_Roll orig, ChestBehavior self)
        {
            orig(self);

            if (alreadyLoggedObjs.Contains(self.gameObject.GetHashCode())) return;
            else alreadyLoggedObjs.Add(self.gameObject.GetHashCode());

            Append(self.gameObject.name, self.gameObject.GetHashCode(), self.dropPickup, self.transform.position.x, self.transform.position.y);
        }

        private void ChestOpened(On.RoR2.ChestBehavior.orig_Open orig, ChestBehavior self)
        {
            orig(self);

            MarkInteraction(self.gameObject.GetHashCode());
        }

        private void BossAwake(On.RoR2.BossGroup.orig_Awake orig, BossGroup self)
        {
            orig(self);
            RoR2Application.onNextUpdate += () =>
            {
                if (self.name == "SuperRoboBallEncounter" && SceneManager.GetActiveScene().name == "shipgraveyard")
                {
                    ulong state0 = self.rng.state0, state1 = self.rng.state1;
                    Append("AlloyWorshipUnit", self.gameObject.GetHashCode(), self.dropTable.GenerateDrop(self.rng), self.gameObject.transform.position.x, self.gameObject.transform.position.y);
                    self.rng.state0 = state0;
                    self.rng.state1 = state1; // reset RNG
                }
            };
        }

        private void FinalEvent(SceneDirector director)
        {
            RoR2Application.onNextUpdate += LogSomeData;
        }

        private void Append(string name, int id, PickupIndex item, float x, float y)
        {
            if (name == "ScavBackpack") // not tracked
                return;
            var loc = new LocationInfo(name, id, item, x, y);
            locations.Add(loc);
            if (!stageLocations.ContainsKey(Run.instance.stageClearCount + 1))
                stageLocations[Run.instance.stageClearCount + 1] = new();
            if (!stageLocations[Run.instance.stageClearCount + 1].ContainsKey(SceneCatalog.currentSceneDef.baseSceneName))
                stageLocations[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName] = new List<LocationInfo>();
            stageLocations[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName].Add(loc);

            string itemString = loc.GetName();
            if (!stageItems.ContainsKey(Run.instance.stageClearCount + 1))
                stageItems[Run.instance.stageClearCount + 1] = new();
            if (!stageItems[Run.instance.stageClearCount + 1].ContainsKey(SceneCatalog.currentSceneDef.baseSceneName))
                stageItems[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName] = new List<string>();
            if (!name.StartsWith("CasinoChest"))
            {
                stageItems[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName].Add(itemString);
            }

            try
            {
                if (loggedStages.ContainsKey(Run.instance.stageClearCount + 1) && loggedStages[Run.instance.stageClearCount + 1])
                {
                    if (saveToFile.Value) File.AppendAllText(path, "\n" + loc.AsString());
                    if (!logOnlyOnCommand.Value) Debug.Log(loc.GetDesiredColor(stageItems[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName], openedObjects[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName]));
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private class LocationInfo
        {
            public string objectType;
            public int objectId;
            public PickupIndex item;
            public float x, y;
            public bool isItem;
            private string? nameToken = null;

            public LocationInfo(string obj, int id, PickupIndex item, float x, float y)
            {
                this.objectType = obj;
                this.objectId = id;
                this.item = item;
                this.x = x;
                this.y = y;
                isItem = ItemCatalog.GetItemDef(item.itemIndex) is not null;
                nameToken = PickupCatalog.GetPickupDef(item)?.nameToken;
            }

            public string GetDesiredColor(List<string> itemsOnStage, List<int> openedObjectIds)
            {
                string output = AsString(ObjectOpened(openedObjectIds));

                if (ItemInList(primaryItemListSplit, itemsOnStage))
                {
                    return $"<color=#{ColorUtility.ToHtmlStringRGBA(primaryItemColor.Value)}>{output}</color>";
                }
                else if (ItemInList(secondaryItemListSplit, itemsOnStage))
                {
                    return $"<color=#{ColorUtility.ToHtmlStringRGBA(secondaryItemColor.Value)}>{output}</color>";
                }
                else
                {
                    return output;
                }
            }

            private bool ObjectOpened(List<int> openedObjectIds)
            {
                if(openedObjectIds != null && openedObjectIds.Contains(objectId))
                {
                    if (objectType.Contains("ShrineChance"))
                    {
                        var shrineHits = openedObjectIds.Where(o => o == objectId).Count();
                        int pos = -1;
                        int.TryParse(objectType.Split('#').Last(), out pos);
                        return (pos != -1 && shrineHits >= pos);
                    }
                    else if (objectType.Contains("CasinoChest"))
                    {
                        var casinoRolls = openedObjectIds.Where(o => o == objectId).Count();
                        int pos = -1;
                        int.TryParse(objectType.Split('#').Last(), out pos);
                        if (casinoRolls == 1)   // the id is entered the first time the chest is interacted with
                            return (pos == 30); // if only 1 entry it ran to the end untouched
                        else
                            return (casinoRolls == pos + 1);
                    }
                    else
                    {
                        return true;
                    }
                }
                return false;
            }

            public string GetName()
            {
                return Language.GetString(nameToken).ToLower().Trim();
            }

            private bool ItemInList(List<string> itemList, List<string> itemsOnStage)
            {
                foreach (var item in itemList)
                {
                    if (item.Contains("&"))
                    {
                        bool allInListPresent = true;
                        var joinSplit = item.ToLower().Trim().Split('&').ToList();
                        if (joinSplit.Contains(GetName()))
                        {
                            foreach (var splitItem in joinSplit)
                            {
                                if (!itemsOnStage.Contains(splitItem.ToLower().Trim()))
                                    allInListPresent = false;
                            }
                            if (allInListPresent)
                                return true;
                        }
                    }
                    else
                    {
                        if (GetName() == item.ToLower().Trim())
                            return true;
                    }
                }
                return false;
            }

            public string AsString(bool opened = false)
            {
                string ret = (nameToken is not null) ? $"{Language.GetString(nameToken)}" : "NON_ITEM"; // string format just name :racesR:

                if (logLevel.Value == LogLevel.OnlyItems)
                {
                    return ret;
                }

                if (logLevel.Value >= LogLevel.ItemsAndSources)
                {
                    ret += $"           in {objectType.Replace("(Clone)", "")}";
                }

                if (logLevel.Value >= LogLevel.AllInfo)
                {
                    ret += $"           at ({x}, {y})";
                }

                if (opened)
                {
                    ret += " (opened)";
                }

                return ret;
            }
        }

        public enum LogLevel
        {
            NoLogging = -1,
            OnlyItems = 0,
            ItemsAndSources = 1,
            AllInfo = 2
        }
    }
}