# SonarDeck

A Windows tray app that bridges a **SteelSeries Sonar** ChatMix setup to a small **ESP8266** hardware controller.

It keeps a physical ChatMix dial, mute button and LED bargraph on the ESP8266 in sync with Sonar over a serial link, routes the keyboard's **volume keys** to whichever speaker device Sonar is actually using, shows a **Windows 11-style volume flyout**, and lets you re-point Sonar's **Mic / Game / Chat** channels to any physical audio device straight from the tray menu.

> Not affiliated with SteelSeries. Requires SteelSeries GG / Sonar to be installed and running.

---

## Features

**Serial bridge**
- Auto-discovers the ESP8266 (CH340, `VID_1A86` / `PID_7523`) and connects at 115200 baud.
- Reconnects on its own: 2 s health checks, 5 s retry between attempts, plus a manual **Restart connection**.
- Streams Sonar **ChatMix balance** changes to the device in near real time (200 ms polling).
- Streams **mic mute** state to the device.
- On every connect it waits 2 s for the ESP to boot, then pushes the current ChatMix and mute state so the LEDs are never stale.
- Handles commands coming back from the hardware: `CHAT_PLUS`, `CHAT_MINUS`, `MUTE`, `SWITCH_MIC`.

**Volume keys follow Sonar**
- A low-level keyboard hook (`WH_KEYBOARD_LL`) intercepts Volume Up / Down / Mute system-wide.
- The keys drive the **Windows playback device Sonar is sending Game/Chat to**, not whatever Windows considers default.
- Uses the endpoint's own volume step (`VolumeStepUp` / `VolumeStepDown`), so a press moves the volume by exactly as much as Windows itself would.
- Follows Sonar live — change the output device in Sonar, or via this app's tray menu, and the keys follow within the next key press.
- If Sonar has no target (or the feature is switched off), the keys are left entirely alone for Windows to handle as usual.

**Volume flyout**
- A frameless, topmost, click-through, non-activating panel in the style of the Windows 11 volume OSD.
- Shows the level as a percentage, a pill-shaped bar, the real Windows media-key glyph (Segoe MDL2 Assets / Segoe Fluent Icons), and the **name of the device being adjusted**.
- Appears bottom-centre of the monitor the pointer is on and auto-hides after 1.4 s.
- No elevation required.

**Tray menu device pickers**
- Lists the **physical** audio devices Sonar can use (Sonar's own virtual devices are hidden), split into inputs (microphones) and outputs (speakers/headphones) with a separator.
- Clicking an **input** sets Sonar's **Mic** channel redirection.
- Clicking an **output** sets Sonar's **Game _and_ Chat** channel redirections.
- The currently selected device in each group carries a check mark, re-read from Sonar every time the menu opens.

**Always-on background app**
- Runs with no main window; the tray tooltip and top menu item report the live link state.
- Optional **Start with Windows** (per-user logon, `HKCU\...\Run`).
- Single-instance guard: launching a second copy makes the running one flash a notification instead.
- File logging capped at roughly **2 MB** total (1 MB, then one rollover to `.1`).

---

## How it works

```
   ┌──────────────┐   USB serial (CH340, 115200)   ┌────────────────────────┐
   │   ESP8266    │ <────────────────────────────> │        SonarDeck       │
   │  dial / LED  │   ChatMix:0.00 / MUTED:1 /     │      (tray app)        │
   │  / buttons   │   CHAT_PLUS / SWITCH_MIC ...   │                        │
   └──────────────┘                                │  SonarBridge  ──> Sonar│
                                                    │  MediaKeyHook ──> core │
                                                    │  VolumeOverlay (OSD)   │
                                                    └────────────────────────┘
```

- **`SonarBridge`** owns the serial port and a Sonar event session on a background worker. Everything above it runs on the UI thread.
- **`TrayApplicationContext`** owns the tray icon, the menu, the keyboard hook and the overlay. The WinForms message loop must own the hook, which is why the hook lives here rather than on the bridge worker.
- The bridge raises `VolumeTargetsChanged` when the Game/Chat device changes; the tray marshals that onto the UI thread and hands it to **`AudioDeviceVolume`**, which drives the endpoint via NAudio's Core Audio wrapper.

---

## Requirements

- **Windows 10 / 11** (x64).
- **.NET 10** (desktop) to build; the app targets `net10.0-windows`.
- **SteelSeries GG with Sonar** installed and running for chat-mix, mute and redirection features.
- An **ESP8266** (NodeMCU / ESP-12E) flashed with [`SonarDeck.ino`](SonarDeck.ino), on a CH340 USB-serial adapter.

---

## Build and run

```powershell
git clone https://github.com/phrammon/SonarDeck.git
cd SonarDeck
dotnet build SonarDeck.sln -c Release
```

Run it:

```powershell
.\bin\Release\net10.0-windows\SonarDeck.exe
```

There is no window — look for the **SonarDeck** icon in the notification area (it may be under the hidden-icons chevron). Right-click it for the menu.

Because it is a windowless tray app, all diagnostics go to a log file (see below) rather than a console.

---

## Tray menu

| Item | What it does |
| --- | --- |
| *(top line)* | Current link state, e.g. `Connected - COM9`, `Looking for ESP8266`. Disabled. |
| *Input devices* | Physical microphones. Clicking one points Sonar's **Mic** channel at it. A check marks the one in use. |
| *Output devices* | Physical speakers/headphones. Clicking one points Sonar's **Game and Chat** channels at it. A check marks the one in use. |
| **Start with Windows** | Adds/removes the per-user logon entry. Reflects the registry when opened. |
| **Route volume keys to speakers** | Enables/disables the media-key hook. On by default. |
| **Restart connection** | Drops the serial session and reconnects immediately. |
| **Exit** | Shuts the app down cleanly. |

If Sonar isn't reachable, the device sections show `Sonar not available` and the picture is simply greyed out; everything still works once Sonar is running.

---

## Serial protocol

All messages are newline-terminated ASCII (`\n`).

**SonarDeck → ESP8266**

| Message | Meaning |
| --- | --- |
| `ChatMix:<value>` | ChatMix balance, `-1.00` (all game) … `1.00` (all chat). |
| `MUTED:1` / `MUTED:0` | Mic muted / unmuted. |

**ESP8266 → SonarDeck**

| Message | Meaning |
| --- | --- |
| `CHAT_PLUS` / `CHAT_MINUS` | Nudge Sonar's ChatMix balance by ±0.1 (clamped to ±1). |
| `MUTE` | Toggle mic mute in Sonar. |
| `SWITCH_MIC` | Switch the active microphone. |

---

## Files and locations

| Thing | Location |
| --- | --- |
| Log | `sonar-deck.log` next to the executable (1 MB, then rolls to `sonar-deck.log.1`) |
| Autostart entry | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → value `SonarDeck` |
| Tray / exe icon | `SonarDeck.ico`, embedded in the assembly |

The log lives beside the `.exe`. If you install the app somewhere read-only (e.g. `Program Files`), writing it fails silently and the app carries on — but you'll lose diagnostics.

---

## Project layout

| File | Responsibility |
| --- | --- |
| `Program.cs` | Entry point: single-instance guard, wiring, message loop. |
| `SonarBridge.cs` | Serial link + Sonar events; the device/redirection API used by the menu. |
| `TrayApplicationContext.cs` | Tray icon, context menu, device pickers, UI-thread ownership of the hook and overlay. |
| `MediaKeyHook.cs` | `WH_KEYBOARD_LL` hook that captures and swallows the volume keys. |
| `AudioDeviceVolume.cs` | Sets volume/mute on specific Core Audio playback endpoints (NAudio). |
| `VolumeOverlay.cs` | The Windows 11-style on-screen volume indicator. |
| `AutoStart.cs` | "Start with Windows" registry entry. |
| `SingleInstance.cs` | Mutex guard + activation signal between instances. |
| `Log.cs` | Rolling file logger. |
| `SonarDeck.ino` | ESP8266 firmware (dial, mute button, shift-register LED bargraph). |

---

## Known limitations

- An **exclusive-fullscreen** game covers the volume flyout; a windowed/borderless game does not.
- The volume keys move **both** Game and Chat if you have pointed those channels at different devices.
- The hardware `SWITCH_MIC` command picks among microphones **by name**, so it only switches between the specific devices the firmware author used; the tray menu is the more general way to change inputs.
- The firmware prints several debug lines per ChatMix change over serial. Dragging the dial continuously fills the ~2 MB log in well under two hours.
- Sonar must be running for chat-mix, mute and redirection features; the serial link itself works without it.
