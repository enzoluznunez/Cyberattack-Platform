# Cyberattack Platform

A mixed-reality data visualization for Meta Quest 3, built in Unity, for exploring
cyberattacks on publicly traded US companies.

https://github.com/user-attachments/assets/667093ee-7573-4255-ae9b-bf8f87c79065

In passthrough, a map of the United States and a 3D bar chart of breach data (the **sheet**)
stand in front of you. The map shows where breaches happened; the sheet shows how those
companies compared financially when they were breached.

You need a headset to try it: hand input and passthrough are the interface. Setting up a
build needs Unity once; using the app needs only the headset.

**Status:** complete (v1.0).

## Quick start

To build the app you need a Meta Quest 3 with a USB-C cable, internet access on the headset,
and the Google Cloud API key for the breach database, from the project owner. The voice
assistant also needs a Google Gemini API key on a paid tier.

1. [Prepare the headset](docs/setup.md#prepare-the-meta-quest-3): turn on **Developer Mode**,
   allow USB debugging and run **Space Setup**.
2. Install Unity **6000.4.0f1** with **Android Build Support** and both of its sub-modules,
   **OpenJDK** and **Android SDK & NDK Tools**.
3. Clone the repository, add it in Unity Hub, open it with 6000.4.0f1, and open
   `Assets/Scenes/Main.unity`.
4. Put the Google Cloud API key on one line in `Assets/StreamingAssets/cloud.key`, and the
   Gemini key on one line in `Assets/StreamingAssets/gemini.key`. Git ignores both. Without
   the Gemini key, everything works except the assistant.
5. Connect the headset by USB and put it on. Open **File → Build Profiles**, select
   **Quest Default**, choose your headset, and click **Build And Run**. The first build takes
   10 to 30 minutes.

You are done when the loading line finishes and the map of the United States and its sheet
stand in front of you in passthrough. See [Setting up a build](docs/setup.md) for every step.

## Features

- A map of the United States, its six regions and their metros, with pins sized by breach count
- A 3D bar chart (the sheet) of each place's breaches across six company financial figures
- Hand-tracked tools to move, rotate, scale, profile, sort and filter the sheet, with undo
- A Gemini voice assistant that can drive the same tools and answer questions about the data
- A Google Cloud data API, tested and deployed automatically on every push

## Tech stack

| Part | Built with |
|---|---|
| Headset app | Unity 6 (6000.4.0f1), C#, URP, Meta XR Core SDK, Interaction SDK, MR Utility Kit, OpenXR |
| Voice assistant | Gemini Live API (`gemini-3.1-flash-live-preview`) through the Google Gen AI .NET SDK |
| Data API | Python 3.13, FastAPI, BigQuery, Docker on Cloud Run, behind API Gateway |
| Data pipeline | pandas, Cloud Storage, BigQuery |
| CI/CD | Cloud Build (full suite and deploy), GitHub Actions (offline tests) |

## Documentation

- [Using the app](docs/usage.md): what you will see, and how to use the map, the tools and the
  voice assistant
- [Setting up a build](docs/setup.md): every step from a new headset to a running build
- [FAQ](docs/faq.md): questions about the data and the app, troubleshooting, cost and privacy
- [Architecture](docs/architecture.md): how the parts connect, the repository layout, and how
  API changes ship
- [Backend](Backend/README.md): the data pipeline and API, and how to run, test and deploy them

## License

Released under the MIT License. See [LICENSE](LICENSE).
