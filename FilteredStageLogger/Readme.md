# Filtered Stage Logger

A plugin that lists all the items that exist in a stage once you load in, or when prompted from the console. Recycler rolls, sale star, chance doll, and Halcyonite shrines are not tracked.

The mod will output the items in the console, (accessible in game with 'ctrl + alt + ~'), as well as create a ItemLogs.txt file next to the LogOutput.Log file.

It lists items and the source, along with highlighting for sought-after items or item combos (ex. `Bundle of Fireworks & Remote Caffeinator` will highlight both items should they both appear on the stage).

Configs for mod behaviors are available, use Risk_Of_Options for convenience.

This mod is a cheat, it'll make the game easier because you know the items, but by default you are not shown the items unless you use `check_stage #`

# Changelogs

**1.0.10**

- Opened indicators are now colored green
- Item names are now colored by tier. This can be disabled in the config. Items can still be highlighted with this on, but the highlight will only show on the item's location
- Fixed mountain shrine simulation

**1.0.9**

- Teleporter and Mountain shrine loot tracked. If the boss turns out to be a horde of many and the log says Boss Item, be warned it WILL be incorrect.

**1.0.8**

- Description update

**1.0.7**

- Adaptive chests are fixed but not output by default, use the `adaptive` arg at the end of your command to list all the adaptive rewards as well
- Alloy Worship Unit reward is tracked
- Other realms on the same stage are retained. You can input an internal stage name after the stage number to select which stage you want to check. ex. `check_stage 4 meridian`
- Chests that you've opened are tracked. Some multi-option outputs (multishops, shipping request, adaptive chests) mark the individual item, void potentials mark all 3 as opened

**1.0.6**

- Disable console output config by default

**1.0.5**

- Added console command `check_stage #`
- Fixed some bugs with multishops and used chests

**1.0.4**

- Forked from zinq7's ListItemData https://thunderstore.io/package/zinq7/ListItemData/
- Added primary/secondary highlight options

**1.0.3**

-  :)

**1.0.2**

- HumanFans no longer explode the mod 

**1.0.1**

- Allow the option to not save it to a text file 

**1.0.0**

- Mod port from RiskOfResources Gauntlet Mod