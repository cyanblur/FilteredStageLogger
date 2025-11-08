using RoR2;
using BepInEx;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using UnityEngine;
using static RoR2.Console;

[assembly: HG.Reflection.SearchableAttribute.OptIn]

namespace FilteredStageLogger
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]

    public class Main : BaseUnityPlugin
    {
        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginAuthor = "cyanblur";
        public const string PluginName = "FilteredStageLogger";
        public const string PluginVersion = "1.0.9";
        public static readonly string path = $"{Assembly.GetExecutingAssembly().Location}/../../../ItemLogs.log";

        public static BepInEx.Configuration.ConfigEntry<LogLevel> logLevel { get; set; }
        public static BepInEx.Configuration.ConfigEntry<bool> saveToFile { get; set; }
        public static BepInEx.Configuration.ConfigEntry<bool> orderByTier { get; set; }
        public static BepInEx.Configuration.ConfigEntry<bool> logOnlyOnCommand { get; set; }
        public static BepInEx.Configuration.ConfigEntry<Color> secondaryItemColor { get; set; }
        public static BepInEx.Configuration.ConfigEntry<string> secondaryItemList { get; set; }
        public static BepInEx.Configuration.ConfigEntry<Color> primaryItemColor { get; set; }
        public static BepInEx.Configuration.ConfigEntry<string> primaryItemList { get; set; }
        public static BepInEx.Configuration.ConfigEntry<bool> useRarityItemColors { get; set; }

        private static List<string> secondaryItemListSplit = new List<string>();
        private static List<string> primaryItemListSplit = new List<string>();
        private static List<LocationInfo> locations = new();
        HashSet<int> alreadyLoggedObjs = new();
        private static int stagesLogged = -1;
        //private static Dictionary<int, Dictionary<string, List<string>>> stageItems = new();
        //private static Dictionary<int, Dictionary<string, List<LocationInfo>>> stageLocations = new();
        private static Dictionary<int, bool> loggedStages = new();
        //private static Dictionary<int, Dictionary<string, List<int>>> openedObjects = new();
        private static Dictionary<int, string> mainStage = new();
        //private static Dictionary<int, Dictionary<string, int>> mountainShrineCount = new();
        private static Dictionary<int, Dictionary<string, StageLogInformation>> stageInformation = new();

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

            useRarityItemColors = Config.Bind<bool>(
                "Item List Settings",
                "Use item rarity colors?",
                true,
                $"If enabled, the item name will show in the color of the tier it belongs to. This takes priority over item list colors, but those colors will still show on the item's location text."
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
            On.RoR2.BossGroup.DropRewards += (o, s) =>
            {
                o(s);
                MarkInteraction(s.gameObject.GetHashCode());
            };
            On.RoR2.ShrineBossBehavior.Start += (o, s) =>
            {
                o(s);
                GetCurrentStageInfo().mountainShrineCount++;
            };
            On.RoR2.ShrineBossBehavior.AddShrineStack += (o, s, i) =>
            {
                o(s, i);
                GetCurrentStageInfo().mountainShrinesHit++;
            };
            On.RoR2.ShopTerminalBehavior.SetNoPickup += (o, s) =>
            {
                o(s);
                GetCurrentStageInfo().closedObjects.Add(s.gameObject.GetHashCode());
            };
            SceneDirector.onPostPopulateSceneServer += FinalEvent;

            Log.LogDebug(PluginGUID + " Awake Done");
        }

        private const string _description = "`check_stage [stage_number] [stage_name] [adaptive] [missing]`";
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
            bool onlyMissing = false;
            List<string> finalArgs = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                var arg = args.GetArgString(i);
                if (arg == "adaptive")
                {
                    logAdaptive = true;
                }
                else if(arg == "missing" || arg == "missed" || arg == "miss")
                {
                    onlyMissing = true;
                }
                else
                {
                    finalArgs.Add(arg);
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
            LogByStageNumber(stageNum, stageName, logAdaptive, onlyMissing);
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
                file += "\n" + loc.AsString(ContainerState.Untouched, true);
            }
            if (!logOnlyOnCommand.Value) LogByStageNumber(stagesLogged + 1);

            if (saveToFile.Value) File.AppendAllText(path, file);
            locations.Clear();
        }

        public static void LogByStageNumber(int stage, string stageName = null, bool logAdaptive = false, bool onlyMissing = false)
        {
            secondaryItemListSplit = secondaryItemList.Value.Split(',').ToList();
            primaryItemListSplit = primaryItemList.Value.Split(',').ToList();
            if (!stageInformation.ContainsKey(stage) || stageInformation[stage] == null || stageInformation[stage].Count == 0)
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
                if (!stageInformation[stage].ContainsKey(stageName))
                {
                    Debug.LogError($"{stageName} not recorded in stage {stage}. Stages found: {string.Join(", ", stageInformation[stage].Keys)}");
                }
                else
                {
                    bool notifyAdaptive = false;
                    foreach (var location in stageInformation[stage][stageName].stageLocations)
                    {
                        if (location.objectType.StartsWith("CasinoChest") && !logAdaptive)
                        {
                            notifyAdaptive = true;
                            continue;
                        }
                        if(onlyMissing)
                        {
                            if (location.GetContainerState(stageInformation[stage][stageName]) != ContainerState.Untouched)
                                continue;
                        }
                        try
                        {
                            Debug.Log(location.GetDesiredColor(stageInformation[stage][stageName]));
                        }
                        catch (Exception e) { }
                    }
                    if (notifyAdaptive)
                    {
                        Debug.Log($"Adaptive chests found but omitted from the log. Add the arg `adaptive` to your command to view.");
                    }
                    if (stageInformation[stage].Keys.Count > 1)
                    {
                        Debug.Log($"Other stages found: {string.Join(", ", stageInformation[stage].Keys.Where(s => s != stageName))}");
                    }
                }
            }
        }

        private void AppendNewRun(On.RoR2.Run.orig_Start orig, Run self)
        {
            orig(self);

            if (saveToFile.Value) File.AppendAllText(path, $"\n\nNew Run Started at {DateTime.Now}");

            stagesLogged = -1;
            stageInformation = new();
            loggedStages = new Dictionary<int, bool>();
        }

        private void BeginStage(On.RoR2.Run.orig_BeginStage orig, Run self)
        {
            InitStageInfo(Run.instance.stageClearCount + 1, SceneCatalog.currentSceneDef.baseSceneName);
            loggedStages[Run.instance.stageClearCount + 1] = false;
            mainStage[Run.instance.stageClearCount + 1] = SceneCatalog.currentSceneDef.baseSceneName;

            alreadyLoggedObjs.Clear();
            orig(self);
        }

        private void MarkInteraction(int id)
        {
            GetCurrentStageInfo().openedObjects.Add(id);
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
                if (self.name == "SuperRoboBallEncounter")
                {
                    ulong state0 = self.rng.state0, state1 = self.rng.state1;
                    Append("AlloyWorshipUnit", self.gameObject.GetHashCode(), self.dropTable.GenerateDrop(self.rng), self.gameObject.transform.position.x, self.gameObject.transform.position.y);
                    self.rng.state0 = state0;
                    self.rng.state1 = state1; // reset RNG
                }
                else if (self.name.Contains("Teleporter"))
                {
                    ulong state0 = self.rng.state0, state1 = self.rng.state1;
                    PickupIndex tpGreen = self.dropTable.GenerateDrop(self.rng);
                    List<PickupDropTable> dropTable = new List<PickupDropTable>();
                    dropTable.Add(self.dropTable);
                    bool flag2 = dropTable != null && dropTable.Count > 0;
                    for (int i = 0; i < GetCurrentStageInfo().mountainShrineCount + 1; i++)
                    {
                        string source = "Teleporter";
                        if (i > 0)
                        {
                            source = $"Mountain #{i}";
                            self.dropTable.GenerateDropPreReplacement(self.rng);
                        }
                        if (self.rng.nextNormalizedFloat <= self.bossDropChance)
                        {
                            if (flag2)
                            {
                                PickupDropTable pickupDropTable = self.rng.NextElementUniform(dropTable);
                                if (pickupDropTable != null)
                                {
                                    _ = pickupDropTable.GenerateDrop(self.rng);
                                }
                            }
                            else
                            {
                                _ = self.rng.NextElementUniform(self.bossDrops);
                            }
                            Append(source, self.gameObject.GetHashCode(), PickupCatalog.FindScrapIndexForItemTier(ItemTier.Boss), self.gameObject.transform.position.x, self.gameObject.transform.position.y);
                        }
                        else
                        {
                            Append(source, self.gameObject.GetHashCode(), tpGreen, self.gameObject.transform.position.x, self.gameObject.transform.position.y);
                        }
                    }
                    self.rng.state0 = state0;
                    self.rng.state1 = state1; // reset RNG
                }
            };
        }



        private void FinalEvent(SceneDirector director)
        {
            RoR2Application.onNextUpdate += LogSomeData;
        }

        private void InitStageInfo(int stageNumber, string stageName)
        {
            if (!stageInformation.ContainsKey(stageNumber))
                stageInformation[stageNumber] = new();
            if (!stageInformation[stageNumber].ContainsKey(stageName))
                stageInformation[stageNumber][stageName] = new();
        }

        private StageLogInformation GetCurrentStageInfo()
        {
            InitStageInfo(Run.instance.stageClearCount + 1, SceneCatalog.currentSceneDef.baseSceneName);
            return stageInformation[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName];
        }

        private void Append(string name, int id, PickupIndex item, float x, float y)
        {
            if (name.StartsWith("ScavBackpack")) // not tracked
                return;
            var loc = new LocationInfo(name, id, item, x, y);
            locations.Add(loc);
            var currentStage = stageInformation[Run.instance.stageClearCount + 1][SceneCatalog.currentSceneDef.baseSceneName];
            currentStage.stageLocations.Add(loc);

            string itemString = loc.GetName();
            if (!name.StartsWith("CasinoChest"))
            {
                currentStage.stageItems.Add(itemString);
            }

            try
            {
                if (loggedStages.ContainsKey(Run.instance.stageClearCount + 1) && loggedStages[Run.instance.stageClearCount + 1])
                {
                    if (saveToFile.Value) File.AppendAllText(path, "\n" + loc.AsString());
                    if (!logOnlyOnCommand.Value) Debug.Log(loc.GetDesiredColor(currentStage));
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

            public string GetDesiredColor(StageLogInformation stageInfo)
            {
                string output = AsString(GetContainerState(stageInfo));

                if (ItemInList(primaryItemListSplit, stageInfo.stageItems))
                {
                    return $"<color=#{ColorUtility.ToHtmlStringRGBA(primaryItemColor.Value)}>{output}</color>";
                }
                else if (ItemInList(secondaryItemListSplit, stageInfo.stageItems))
                {
                    return $"<color=#{ColorUtility.ToHtmlStringRGBA(secondaryItemColor.Value)}>{output}</color>";
                }
                else
                {
                    return output;
                }
            }

            public ContainerState GetContainerState(StageLogInformation stageInfo)
            {
                if (stageInfo.openedObjects != null && stageInfo.openedObjects.Contains(objectId))
                {
                    if (objectType.Contains("ShrineChance"))
                    {
                        var shrineHits = stageInfo.openedObjects.Where(o => o == objectId).Count();
                        int pos = -1;
                        int.TryParse(objectType.Split('#').Last(), out pos);
                        if (pos != -1 && shrineHits >= pos)
                            return ContainerState.Opened;
                    }
                    else if (objectType.Contains("CasinoChest"))
                    {
                        var casinoRolls = stageInfo.openedObjects.Where(o => o == objectId).Count();
                        int pos = -1;
                        int.TryParse(objectType.Split('#').Last(), out pos);
                        if (casinoRolls == 1)
                        {   // the id is entered the first time the chest is interacted with
                            if (pos == 30) // if only 1 entry it ran to the end untouched
                            {
                                return ContainerState.Opened;
                            }
                        }
                        else if (casinoRolls == pos + 1)
                        {
                            return ContainerState.Opened;
                        }

                        return ContainerState.CasinoUnpicked;
                    }
                    else if (objectType.Contains("Mountain"))
                    {
                        int pos = -1;
                        int.TryParse(objectType.Split('#').Last(), out pos);
                        if (pos <= stageInfo.mountainShrinesHit)
                        {
                            return ContainerState.Opened;
                        }
                    }
                    else
                    {
                        return ContainerState.Opened;
                    }
                }
                else if (stageInfo.closedObjects != null && stageInfo.closedObjects.Contains(objectId))
                {
                    return ContainerState.Closed;
                }
                else if (objectType.Contains("Duplicator"))
                {
                    return ContainerState.Printer;
                }
                else if (objectType.Contains("LunarCauldron"))
                {
                    return ContainerState.Cauldron;
                }
                return ContainerState.Untouched;
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

            public string AsString(ContainerState containerState = ContainerState.Untouched, bool textOnly = false)
            {
                string ret = "";
                var itemName = (nameToken is not null) ? $"{Language.GetString(nameToken)}" : "NON_ITEM"; // string format just name :racesR:

                if (itemName == "Item Scrap, Yellow")
                {
                    itemName = "Boss Item";
                }

                if (useRarityItemColors.Value && !textOnly)
                {
                    var pickupDef = PickupCatalog.GetPickupDef(item);
                    if (PickupCatalog.GetPickupDef(item).equipmentIndex == EquipmentIndex.None)
                    {
                        ret = $"<color=#{ColorUtility.ToHtmlStringRGBA(ColorCatalog.GetColor(ItemTierCatalog.GetItemTierDef(ItemCatalog.GetItemDef(pickupDef.itemIndex).tier).colorIndex))}>{itemName}</color>";
                    }
                    else
                    {
                        ret = $"<color=#{ColorUtility.ToHtmlStringRGBA(ColorCatalog.GetColor(EquipmentCatalog.GetEquipmentDef(pickupDef.equipmentIndex).colorIndex))}>{itemName}</color>";
                    }
                }
                else
                {
                    ret = itemName;
                }

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

                if (containerState == ContainerState.Opened)
                {
                    ret += $" <color=#{ColorUtility.ToHtmlStringRGBA(Color.green)}>(opened)</color>";
                }
                else if (containerState == ContainerState.Closed)
                {
                    ret += $" <color=#{ColorUtility.ToHtmlStringRGBA(Color.gray)}>(locked)</color>";
                }
                
                return ret;
            }
        }

        private class StageLogInformation
        {
            public List<string> stageItems;
            public List<LocationInfo> stageLocations;
            public List<int> openedObjects;
            public List<int> closedObjects;
            public int mountainShrineCount;
            public int mountainShrinesHit;

            public StageLogInformation()
            {
                stageItems = new List<string>();
                stageLocations = new List<LocationInfo>();
                openedObjects = new List<int>();
                closedObjects = new List<int>();
                mountainShrineCount = 0;
                mountainShrinesHit = 0;
            }
        }

        public enum LogLevel
        {
            NoLogging = -1,
            OnlyItems = 0,
            ItemsAndSources = 1,
            AllInfo = 2
        }

        public enum ContainerState
        {
            Untouched = -1,
            Opened = 0,
            Closed = 1,
            Printer = 2,
            Cauldron = 3,
            CasinoUnpicked = 4,
        }
    }
}