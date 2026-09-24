# Disclaimers
## AI Disclaimer - I am not a developer
I am not a professional developer. This application was made with **heavy** assistance of Claude Opus 5.5 and Sonnet 5. I did testing on my own device but I cannot validate that it will work on other devices. If you encounter an error please let me know and I'll look into it. Please include your LogOutput.log file from \Grey Hack\BepInEx\LogOutput.log

## Multiplayer
I have not yet tested this with multiplayer or confirmed it works. Please only use this in Singleplayer. If this is validate in Multiplayer then this section will be removed.

# GHMultiMonitor

GHMultiMonitor is a BepInEx plugin for Grey Hack that extends the in-game desktop across multiple monitors. 
This plugin allows you to drag any in-game window off the edge of your main screen to other monitors. It allows you to choose what monitors to use at any time by pressing F9. Enabling a monitor is instant, but disabling a monitor requires you to restart the game.

It also adds Windows-style window snapping, remembers where you put each app, and shows your desktop wallpaper on your extra monitors.

## Requirements

- Grey Hack (Windows), version 0.9.7 (Nightly)
- BepInEx 5.4.x (x64): https://github.com/BepInEx/BepInEx/releases
- Two or more monitors for the multi-monitor features (snapping also works with a single monitor)

## Installation

1. Install BepInEx 5 into your Grey Hack folder, launch the game once, then quit.
2. Extract this zip into your Grey Hack folder. `GHMultiMonitor.dll` should end up in `BepInEx\plugins\GHMultiMonitor\`.
3. Launch the game. A menu will ask which extra monitors to use.

## Usage

- **Drag windows** past the edge of your screen to move them to another monitor.
- **Snap windows** by dragging them against a screen edge: left or right for half the screen, the top to maximize, or a corner for a quarter. A preview shows where the window will go. Dragging a snapped window away gives it back its original size.
- **F9** opens the settings menu. From there you can choose which monitors to use, turn snapping on or off and adjust it, and manage saved window positions. While the menu is open, each monitor in use shows its number, and the snap areas are highlighted.
- **F8** writes troubleshooting info to `BepInEx\LogOutput.log`.


## Settings

Settings are stored in `BepInEx\config\com.bobby.greyhack.multimonitor.cfg` and can be edited there with any text editor. Close the game before editing. Most of these are set for you by the F9 menu.

- `EnabledDisplays`: which extra monitors to use. Leave it empty to be asked again on the next launch.
- `KeepMainFullscreen`: set to `false` if you don't want the plugin switching the game to borderless fullscreen.

**Snapping**
- `EnableSnapping`: turns window snapping on or off.
- `Delay`: how long the cursor has to rest at an edge before the snap preview appears. This stops snapping from triggering when you're just moving a window to another monitor.
- `EdgeSize`: how close to the edge of the screen the cursor has to be, in pixels.
- `CornerSize`: how much of each edge counts as a corner.

## Uninstalling

Delete the `BepInEx\plugins\GHMultiMonitor` folder. To also remove your settings, delete the two `com.bobby.greyhack.multimonitor` files in `BepInEx\config`. To remove BepInEx entirely, also delete the `BepInEx` folder, `winhttp.dll`, and `doorstop_config.ini`.
