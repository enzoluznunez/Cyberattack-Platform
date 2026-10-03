# Instructions for Getting Started

This is a data visualization project for Meta Quest, built in Unity, for exploring
cyberattacks on publicly listed companies. Sheets of breach data stand in front of you in
passthrough, you reshape them with your hands, and a voice assistant can drive the same
tools when asked — and look up individual breaches.

## Prerequisites

- [ ] A Meta Quest 3 with a USB-C cable
- [ ] A Google Gemini API key on a paid tier
- [ ] The Google Cloud API key for the breach database, from the project owner
- [ ] Internet access on the headset

## Prepare the Meta Quest 3

1. Pair the headset with the Meta Horizon app on your phone.
2. Create or join an organization at developer.meta.com and verify your account.
3. In the Horizon app, open your headset's settings, turn on Developer Mode, and restart the headset.
4. Plug the headset into your computer, put it on, and accept **Allow USB debugging** — check *Always allow from this computer*.
5. Run Space Setup. The app requires passthrough and reads your room's scene data.

## Download and Set Up Unity

1. Install Unity Hub on your computer and sign in or create an account.
2. Install the Unity version **6000.4.0f1**.
3. During installation, check **Android Build Support** and both of its sub-modules, **OpenJDK** and **Android SDK & NDK Tools**.

## Download and Set Up the Project

1. Clone the repository, or download the ZIP and unpack it.
2. In Unity Hub choose **Add → Add project from disk**, select the cloned folder, and open
   it with 6000.4.0f1.
3. Open `Assets/Scenes/File Reader.unity`.

## Configure the Project's Codebase

### Connect to the Breach Database

Every sheet is drawn live from a database in the cloud, all of it on Google Cloud in the
project `cyberattack-platform`:

```
headset --(Google Cloud API key)--> API Gateway --> Cloud Run --> BigQuery
```

The app already knows the gateway's address. It only needs the key.

1. Get the key: in the Google Cloud console, **APIs & Services → Credentials → Cloud API
   Key → Show key**, or ask the project owner for it. It is restricted to this one API, so
   it unlocks the breach database and nothing else.
2. Put it on one line, and nothing else, in `Assets/StreamingAssets/cloud.key`. Git
   ignores this path, so the key stays on your machine and a fresh clone never carries one.

Without it the app opens with nothing listed and says so in a notice.

To point the Editor at an API running on your own computer instead, put its address on one
line in `Assets/StreamingAssets/cloud.url` (also git-ignored), such as
`http://127.0.0.1:8000`. A headset build refuses plain `http://`, so this is for the Editor;
`pipeline/README.md` covers running and deploying the API and rebuilding the data.

### Add Your Gemini API Key

Create a Gemini API key in [Google AI Studio](https://aistudio.google.com/apikey) under the
`cyberattack-platform` project, so its usage bills there. Put it in a file at
`Assets/StreamingAssets/gemini.key`, on one line and nothing else. Git ignores this path, so
your key stays on your machine and a fresh clone never carries one.

Without it the app still runs and every sheet still works; only the assistant fails to start.

## Building to the Meta Quest 3

1. Connect the headset by USB and put it on, so it stays awake.
2. Open **File → Build Profiles**, select **Quest Default**, and choose your headset in the
   device list. Platform, architecture, and SDK levels are already set in that profile —
   change nothing.
3. Click **Build And Run**. The first build takes 10 to 30 minutes; later builds are far
   quicker.
4. In the headset, accept the microphone prompt at launch. Denying it leaves everything
   working except voice.

Done when you are standing in passthrough with Attacks by Year open and the three sheets
listed beside you. An empty list means the database could not be reached — check the
headset's internet connection and `cloud.key` — not that the build failed.

## Changing the Breach Database API

The API under `pipeline/` ships itself. Cloud Build watches this repository and runs
`cloudbuild.yaml` on every push:

```
push to any branch:  run the tests
merge to main:       run the tests --> build the Docker image --> deploy to Cloud Run --> check it answers
```

A failing test stops the build, so nothing untested reaches the headsets, and the live
version is always tagged with the commit it came from. Builds and their logs are in the
Google Cloud console under **Cloud Build → History** (region `us-east1`).
`pipeline/README.md` covers what the pipeline may and may not touch, and the changes that
still go through `deploy.sh` by hand.

## FAQ

**Does it cost anything?** The Gemini API, yes, for whatever the assistant is used. The
breach database — Cloud Storage, BigQuery, Cloud Run and API Gateway — is small enough to
sit within Google Cloud's free tiers, and the project has a monthly budget alert in case it
ever does not.

**Can I try it without a headset?** Not meaningfully — hand input and passthrough are the interface.
