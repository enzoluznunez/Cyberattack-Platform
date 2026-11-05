# Setting up a build

How to build the app from this repository and run it on a Meta Quest 3. Paths are relative to
the repository root.

## Prerequisites

- A Meta Quest 3 with a USB-C cable
- A Google Gemini API key on a paid tier (only the assistant needs it)
- The Google Cloud API key for the breach database, from the project owner
- Internet access on the headset

## Prepare the Meta Quest 3

1. Pair the headset with the Meta Horizon app on your phone.
2. Create or join an organization at developer.meta.com and verify your account.
3. In the Horizon app, open your headset's settings, turn on **Developer Mode**, and
   restart the headset.
4. Plug the headset into your computer, put it on, and accept **Allow USB debugging**.
   Check *Always allow from this computer*.
5. Run **Space Setup**. The app uses passthrough and reads your room's scene data.

## Install Unity

1. Install Unity Hub and sign in or create an account.
2. Install Unity **6000.4.0f1**.
3. During installation, check **Android Build Support** and both of its sub-modules,
   **OpenJDK** and **Android SDK & NDK Tools**.

## Open the project

1. Clone the repository, or download the ZIP and unpack it.
2. In Unity Hub choose **Add → Add project from disk**, select the folder, and open it
   with 6000.4.0f1.
3. Open `Assets/Scenes/Main.unity`.

## Connect to the breach database

Every sheet is drawn live from a database on Google Cloud, in the project
`cyberattack-platform`:

```
headset --(Google Cloud API key)--> API Gateway --> Cloud Run --> BigQuery
```

The app already knows the gateway's address; it only needs the key.

1. Get the key: in the Google Cloud console, **APIs & Services → Credentials → Cloud API
   Key → Show key**, or ask the project owner. It is restricted to this one API, so it
   unlocks the breach database and nothing else.
2. Put it on one line, and nothing else, in `Assets/StreamingAssets/cloud.key`. Git
   ignores this path, so the key stays on your machine.

To point the Editor at an API running on your own computer instead, put its address on
one line in `Assets/StreamingAssets/cloud.url` (also git-ignored), such as
`http://127.0.0.1:8000`. A headset build refuses plain `http://`, so this is for the
Editor. [`Backend/README.md`](../Backend/README.md) covers running and deploying the API and rebuilding the data.

## Add your Gemini API key

Create a Gemini API key in [Google AI Studio](https://aistudio.google.com/apikey) under
the `cyberattack-platform` project, so its usage bills there. Put it on one line, and
nothing else, in `Assets/StreamingAssets/gemini.key`. Git ignores this path too.

Without it everything works except the assistant, which turns itself off when started.

## Build to the headset

1. Connect the headset by USB and put it on, so it stays awake.
2. Open **File → Build Profiles**, select **Quest Default**, and choose your headset in
   the device list. Platform, architecture and SDK levels are already set in that
   profile; change nothing.
3. Click **Build And Run**. The first build takes 10 to 30 minutes; later builds are much
   quicker.
4. In the headset, accept the microphone prompt. Denying it leaves everything working
   except the assistant.

You are done when the loading line finishes and the map of the United States and its
sheet stand in front of you in passthrough. A **No Map** notice means the database could
not be reached (check the headset's internet connection and `cloud.key`), not that the
build failed.
