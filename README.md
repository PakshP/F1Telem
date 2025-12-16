# F1 Telemetry Recorder — F1 25

A desktop application for recording **F1 25** UDP telemetry during **Time Trial** sessions and exporting distance-normalized lap data as structured JSON for analysis and comparison.

This tool is designed to help drivers identify where they gain or lose time by comparing their telemetry (speed, throttle, braking, steering, gears, DRS) against reference laps.

---

## 🚀 Features

- 📡 Live UDP telemetry listener for **F1 25**
- 🏁 Correctly captures **Garage → Flying Lap → Timed Lap**
- 📏 Distance-normalized lap data
- 💾 User-selected save location with **automatic lap saving**
- 📥 Manual download of previous laps at any time
- 🧾 Clean, consistent JSON output
- 📊 Automatically computed lap statistics:
  - Top speed
  - Average speed
  - Average throttle usage
  - Average brake usage
- 🧠 Designed for lap comparison and analysis tooling

---

## 📂 Exported JSON Format

Each completed lap is exported as a single JSON file:

```json
{
  "time_normalized": {
    "0": 0.00,
    "1": 12.34
  },
  "speed": [
    { "x": 0, "y": 123 },
    { "x": 1, "y": 125 }
  ],
  "brake": [
    { "x": 0, "y": 0.000 }
  ],
  "throttle": [
    { "x": 0, "y": 1.000 }
  ],
  "gear": [
    { "x": 0, "y": 3 }
  ],
  "steer": [
    { "x": 0, "y": -0.123 }
  ],
  "drs": [
    { "x": 0, "y": 1 }
  ],
  "lap_stats": {
    "speed_top": 123,
    "speed_average": 123.4,
    "throttle_average": 12,
    "brake_average": 1
  }
}
```
---

## 🛠 Tech Stack

- **C#**
- **WPF (.NET 8)**
- **F1Game.UDP** (NuGet)
- UDP Telemetry (F1 25)

---

## 📦 Installation & Setup

### Requirements
- Windows
- F1 25
- .NET 8 SDK
- Visual Studio 2022 or newer

---

### Clone the Repository

```bash
git clone https://github.com/yourusername/f1-telemetry-recorder.git
cd f1-telemetry-recorder
