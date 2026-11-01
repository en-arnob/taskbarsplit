# TaskbarSplit

Gives every window its own taskbar icon instead of stacking windows of the same app
on one icon. There are no labels, no Explorer injection and no patched system files.
The tool is a 20 KB tray app that uses about 4 MB of RAM.

## How it works

The Windows taskbar groups buttons by each window's AppUserModelID. TaskbarSplit watches
for new top-level windows and gives every extra window of an app its own unique ID through
the documented `SHGetPropertyStoreForWindow` API. The first window of an app keeps its real
ID, so it still sits on the pinned icon. Pausing or exiting the tool restores the original IDs.

Keep the taskbar setting **Combine taskbar buttons and hide labels** on **Always**.
That gives one icon per window and no labels.

## Build

Run `build.cmd`. It uses the C# compiler that ships with Windows, so nothing else is needed.

## Use

Run `TaskbarSplit.exe`. Right-click its tray icon for these options:

- **Split windows** pauses or resumes splitting. Double-clicking the tray icon does the same.
- **Start with Windows** adds or removes a Run entry for your user account.
- **Edit settings** opens `TaskbarSplit.ini`. Use **Reload settings** after you save it.
- **Exit** restores normal grouping and quits.

To quit from a script, run:

```
TaskbarSplit.exe --exit
```

## Settings (`TaskbarSplit.ini`)

| Key | Meaning |
| --- | --- |
| `Mode` | `all` splits every app except `Exclude`. `include` splits only the apps in `Include`. |
| `Include` / `Exclude` | Comma-separated exe names, for example `Code.exe, chrome.exe`. |
| `KeepFirstOnPin` | `true` keeps the first window on the pinned icon. `false` splits every window off, so the pinned icon stays a plain launcher. |

## Limits

- Split icons cannot be pinned. Pin an app from its first window or from Start instead.
- A split icon's jump list does not show the app's recent files.
- Windows of apps running as administrator are left alone unless TaskbarSplit also runs as administrator.
- Store app frame windows are skipped by default, because changing their ID can confuse the shell.
