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
        public const string PluginVersion = "1.0.6";
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
        private static Dictionary<int, List<string>> stageItems = new();
        private static Dictionary<int, List<LocationInfo>> stageLocations = new();
        private static Dictionary<int, bool> loggedStages = new();

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
            On.RoR2.ShopTerminalBehavior.SetPickupIndex += SetPrinterIndex;
            On.RoR2.ShopTerminalBehavior.GenerateNewPickupServer += GetMultishopItems;
            On.RoR2.OptionChestBehavior.Roll += LogAllLoot;
            On.RoR2.ShrineChanceBehavior.Start += ChanceShenanigans;
            On.RoR2.RouletteChestController.OnEnable += ListItems;
            SceneDirector.onPostPopulateSceneServer += FinalEvent;

            Log.LogDebug(PluginGUID + " Awake Done");
        }

        private const string _description = "`check_stage [stage_number]`";
        [ConCommand(commandName = "check_stage", flags = ConVarFlags.None, helpText = _description)]
        public static void CheckStage(ConCommandArgs args)
        {
            if (stagesLogged == -1)
            {
                Debug.Log($"check_stage must be used after a run has started");
                return;
            }

            int stageNum = -1;
            if (args.Count == 0)
            {
                Debug.Log($"check_stage was used without an arg, outputting stage {Run.instance.stageClearCount + 1}");
                stageNum = Run.instance.stageClearCount + 1;
            }
            else
            {
                if (!int.TryParse(args.GetArgString(0), out stageNum))
                {
                    Debug.Log($"check_stage must have a number after it. {_description}");
                    return;
                }
            }

            LogByStageNumber(stageNum);
        }

        private void AppendNewRun(On.RoR2.Run.orig_Start orig, Run self)
        {
            orig(self);

            if (saveToFile.Value) File.AppendAllText(path, $"\n\nNew Run Started at {DateTime.Now}");

            stagesLogged = -1;
            stageItems = new Dictionary<int, List<string>>();
            stageLocations = new Dictionary<int, List<LocationInfo>>();
            loggedStages = new Dictionary<int, bool>();
        }

        private void GetMultishopItems(On.RoR2.ShopTerminalBehavior.orig_GenerateNewPickupServer orig, ShopTerminalBehavior self)
        {
            orig(self);

            if (alreadyLoggedObjs.Contains(self.gameObject.GetInstanceID())) return;
            else alreadyLoggedObjs.Add(self.gameObject.GetInstanceID());

            if (self.pickupIndex.value == -1) return; // BAD_PICKUP_INDEX

            Append(self.gameObject.name, self.pickupIndex, self.transform.position.x, self.transform.position.y);
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

        public static void LogByStageNumber(int stage)
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
                foreach (var location in stageLocations[stage])
                {
                    try
                    {
                        Debug.Log(location.GetDesiredColor(stageItems[stage]));
                    }
                    catch (Exception e) { }
                }
            }
        }

        private void LogAllLoot(On.RoR2.OptionChestBehavior.orig_Roll orig, OptionChestBehavior self)
        {
            orig(self);
            int count = 1;
            foreach (var itemPickup in self.generatedDrops)
            {
                Append(self.gameObject.name + $" #{count}", itemPickup, self.gameObject.transform.position.x, self.gameObject.transform.position.y);
                count++;
            }
        }

        private void BeginStage(On.RoR2.Run.orig_BeginStage orig, Run self)
        {
            stageLocations[Run.instance.stageClearCount + 1] = new List<LocationInfo>();
            stageItems[Run.instance.stageClearCount + 1] = new List<string>();
            loggedStages[Run.instance.stageClearCount + 1] = false;

            alreadyLoggedObjs.Clear();
            orig(self);
        }

        private void FinalEvent(SceneDirector director)
        {
            RoR2Application.onNextUpdate += LogSomeData;
        }
        private void ChanceShenanigans(On.RoR2.ShrineChanceBehavior.orig_Start orig, ShrineChanceBehavior self)
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
                    Append(self.gameObject.name + " #" + hits, self.dropTable.GenerateDrop(self.rng), self.transform.position.x, self.transform.position.y);
                    succcs++;
                }
            }
            self.rng.state0 = state0;
            self.rng.state1 = state1; // reset RNG
        }

        private void ListItems(On.RoR2.RouletteChestController.orig_OnEnable orig, RouletteChestController self)
        {
            orig(self);

            if (alreadyLoggedObjs.Contains(self.gameObject.GetInstanceID())) return;
            else alreadyLoggedObjs.Add(self.gameObject.GetInstanceID());

            foreach (var entry in self.entries)
            {
                Append(self.gameObject.name, entry.pickupIndex, self.transform.position.x, self.transform.position.y);
            }
        }

        private void SetPrinterIndex(On.RoR2.ShopTerminalBehavior.orig_SetPickupIndex orig, ShopTerminalBehavior self, PickupIndex newPickupIndex, bool newHidden)
        {
            orig(self, newPickupIndex, newHidden);

            if (alreadyLoggedObjs.Contains(self.gameObject.GetInstanceID())) return;
            else if (newPickupIndex.isValid) alreadyLoggedObjs.Add(self.gameObject.GetInstanceID());

            if (self.pickupIndex == PickupCatalog.FindPickupIndex(ItemIndex.None)) return;

            Append(self.gameObject.name, self.pickupIndex, self.transform.position.x, self.transform.position.y);
        }

        private void LogChestRoll(On.RoR2.ChestBehavior.orig_Roll orig, ChestBehavior self)
        {
            orig(self);

            if (alreadyLoggedObjs.Contains(self.gameObject.GetInstanceID())) return;
            else alreadyLoggedObjs.Add(self.gameObject.GetInstanceID());

            Append(self.gameObject.name, self.dropPickup, self.transform.position.x, self.transform.position.y);
        }

        private void Append(string name, PickupIndex item, float x, float y)
        {
            var loc = new LocationInfo(name, item, x, y);
            locations.Add(loc);
            if (!stageLocations.ContainsKey(Run.instance.stageClearCount + 1))
                stageLocations[Run.instance.stageClearCount + 1] = new List<LocationInfo>();
            stageLocations[Run.instance.stageClearCount + 1].Add(loc);

            string itemString = loc.GetName();
            if (!stageItems.ContainsKey(Run.instance.stageClearCount + 1))
                stageItems[Run.instance.stageClearCount + 1] = new List<string>();
            stageItems[Run.instance.stageClearCount + 1].Add(itemString);

            try
            {
                if (loggedStages.ContainsKey(Run.instance.stageClearCount + 1) && loggedStages[Run.instance.stageClearCount + 1])
                {
                    if (saveToFile.Value) File.AppendAllText(path, "\n" + loc.AsString());
                    if (!logOnlyOnCommand.Value) Debug.Log(loc.GetDesiredColor(stageItems[Run.instance.stageClearCount + 1]));
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
            public PickupIndex item;
            public float x, y;
            public bool isItem;
            private string? nameToken = null;

            public LocationInfo(string obj, PickupIndex item, float x, float y)
            {
                this.objectType = obj;
                this.item = item;
                this.x = x;
                this.y = y;
                isItem = ItemCatalog.GetItemDef(item.itemIndex) is not null;
                nameToken = PickupCatalog.GetPickupDef(item)?.nameToken;
            }

            public string GetDesiredColor(List<string> itemsOnStage)
            {
                string output = AsString();

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

            public string AsString()
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