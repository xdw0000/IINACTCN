![icon](https://github.com/xdw0000/IINACTCN/blob/main/images/icon.ico?raw=true)

# IINACT

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin to run the [FFXIV_ACT_Plugin](https://github.com/ravahn/FFXIV_ACT_Plugin) in an [ACT](https://advancedcombattracker.com/)-like enviroment with a heavily modified port of [Overlay Plugin](https://github.com/OverlayPlugin/OverlayPlugin) for modern .NET.

The data source here is only based on [Unscrambler](https://github.com/perchbirdd/Unscrambler) and does not require any extra injection with [Deucalion](https://github.com/ff14wed/deucalion) or network capture with elevated privileges.

This will **not** render overlays by itself, use something like [Browsingway](https://github.com/Styr1x/Browsingway), [Next UI](https://github.com/kaminaris/Next-UI) or [hudkit](https://github.com/valarnin/hudkit) (Linux only) to display Overlays.


## Why

- ACT is too inconvenient IMHO for just wanting to have the game data parsed and served via a WebSocket server
- Drastically more efficent than ACT, in part to .NET 10, in part to a more sane log line processing (disk I/O is not blocking LogLineEvents and happening on a separate lower priority thread)
- Due to the above and running fully inside the game process CPU usage will be orders of magnitude (not exaggerating here) lower when running under Wine compared to network-based capture
- Uses an ultra fast and low latency WebSocket server based on [NetCoreServer](https://github.com/chronoxor/NetCoreServer)
- Doesn't use legacy technology that hurts Linux and macOS users
- Follows the Unix philosophy of just doing one thing and doing it well   

## Installing

This plugin is distributed as a custom Dalamud plugin.

In-game: `/xlsettings` → Experimental → Custom Plugin Repositories
Add the repo URL (see [Releases](https://github.com/xdw0000/IINACTCN/releases) page):
`https://cdn.jsdelivr.net/gh/xdw0000/IINACTCN@main/repo.json`
(fallback: `https://raw.githubusercontent.com/xdw0000/IINACTCN/main/repo.json`)
`/xlplugins` → search "IINACT" → Install

Or for local dev builds:

`dotnet build` (repo root, `IINACT.sln`)
`/xlsettings` → Experimental → Dev Plugin Locations → add the DLL path (`IINACT/bin/Release/win-x64/IINACT.dll`)

## How to build

Just run 
```
git clone --recurse-submodules https://github.com/xdw0000/IINACTCN.git
cd IINACTCN
dotnet build
``` 
on a Linux, macOS or Windows machine with the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). 

You will need to be able to reference Dalamud as well, meaning having an install of [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) (or 卫月 / XIVLauncher CN for the Chinese game client) on Windows or XIV-on-Mac (XOM) on macOS. On Linux `DALAMUD_HOME` needs to be correctly set (for example `$HOME/.xlcore/dalamud/Hooks/dev`).

## FAQ

**Where are my logs?**

- In your Documents folder. For Windows users, `C:\Users\[user]\Documents\IINACT`. For Mac/Linux users, same thing, but relative to your wine prefix.

**Are these logs compatible with FFLogs? Can I use the FFLogs Uploader?**

- Yes! 100% compatible.
