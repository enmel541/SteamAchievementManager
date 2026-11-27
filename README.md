# Steam Achievement Manager Modern

A modernized, **modified version** of
[gibbed/SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager): a new interface for managing your
Steam achievements, with filters, DLC detection, auto-unlock with undo and an opt-in friends comparison.

> **This is not the original Steam Achievement Manager** and it is not made or endorsed by its author. It is also not
> affiliated with Valve; "Steam" is a trademark of Valve. Changing achievements can be considered cheating by some games,
> their communities or leaderboards - use it at your own risk, on games where you are fine with that.
>
> Looking for the original? Get it from [gibbed's repository](https://github.com/gibbed/SteamAchievementManager).

## Download

**[Download the latest release](https://github.com/enmel541/SteamAchievementManager/releases/latest)**, unzip it and start
`SteamAchievementManager.exe`.

* Windows 10/11 (64-bit), Steam running and signed in
* Microsoft WebView2 runtime (preinstalled on Windows 11)
* No .NET installation needed, the download brings its own
* The app is not code-signed. If Windows SmartScreen warns you, choose *More info* → *Run anyway*.

## More

* [All features, requirements and notes](SAM.Modern/README.md)
* [Architecture](SAM.Modern/docs/ARCHITECTURE.md)
* [What changed in the Steam layer](SAM.Modern/Steam/README.md)

## Repository layout

| Path | Content |
|---|---|
| `SAM.Modern/` | This modified version |
| `SAM.API/`, `SAM.Game/`, `SAM.Picker/`, `SAM.sln` | The original code, unchanged |

## Building

With the .NET 10 SDK:

```powershell
cd SAM.Modern
dotnet publish -c Release -r win-x64 --self-contained true -o dist
```

## Credits and license

The original Steam Achievement Manager is by Rick ([gibbed](https://github.com/gibbed)) and is published under the
[zlib license](LICENSE.txt). The Steam integration of this version is derived from it.

Most (if not all) icons of the original applications are from the [Fugue Icons](https://p.yusukekamiyamane.com/) set.
Third-party parts of SAM.Modern are listed in [THIRD-PARTY-NOTICES.txt](SAM.Modern/THIRD-PARTY-NOTICES.txt).
