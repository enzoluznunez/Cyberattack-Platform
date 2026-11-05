# FAQ

Common questions about the app, and fixes for common problems.

## Getting started

### What is this for?

Seeing, side by side, where breaches at publicly traded US companies happened and how
those companies compared financially when they were breached. The map shows where; the
sheet shows the figures.

### Where does the data come from?

Two exports: one of disclosed breaches and one of company fundamentals. They are cleaned
into a database on Google Cloud (see [`Backend/README.md`](../Backend/README.md)). Only breaches at companies
trading under a ticker, with all six figures reported, are kept, so every bar on every
sheet is a real reported figure. The breaches kept run from 2005 to 2023.

### Do I need to know Unity to use it?

No. Setting up a build needs Unity once; using the app needs only the headset.

### Can I try it without a headset?

Not meaningfully. Hand input and passthrough are the interface.

### Does it work sitting down?

Yes. Everything opens at the height of your head when it first appears. If you change
between sitting and standing, take the headset off for five seconds or recenter, and the
layout comes back to eye level.

### Why does it take a few seconds to start?

The loading line reads every sheet and builds everything it needs up front, so that
nothing has to load or be built while you are using the app. Switching between sheets is
smooth because of it.

## The map

### Why are some pins grey?

Each region colours only its ten largest metros, so the colours stay easy to tell apart.
The rest are drawn in greys, darker for more breaches. Their rows on the sheet are grey
too.

### I poked a pin and got a "No Figures" notice.

None of that metro's breaches reported all six figures, so it has no sheet. The sheet
stays as it was.

### The sliders do not respond.

Bring your fingertip to the thumb (the long button on the slider) from in front and press
into it; then drag along the bar. A pinch does not take it.

## The sheet

### What does a bar's height mean?

Where that figure ranks among every US breach. A tall bar is near the top of all US
breaches for that figure, not just the top of this sheet, so the same company looks the
same on every sheet.

### What do the colours mean?

On a region's or a metro's sheet, each row is its metro's colour, as on the map. On the
United States sheet, each row is its region's colour. Within a row, brighter bars rank
higher.

### The row and column titles disappeared.

They hide when the sheet is scaled smaller than its starting size, where they would be
too small to read. Make it larger with the Scale tool and they come back.

### I cannot grab the sheet.

Select Move, Rotate or Scale on the tool panel first. With any other tool, or none, the
sheet stays put so it is not moved by accident.

### I moved the sheet. Where does the next one open?

Each sheet remembers its own moves. One you have moved opens where you left it; one you
have not opens to the right of the map, level with the Region slider.

### Why does the sheet sink and rise when I switch?

The old sheet sinks away and the new one rises in the same place, so you can see the
switch happen.

## The tools

### How does Filter know whether I mean a row or a column?

By height: poking low on the bars means the row, high means the column. Poking just past
the end of a row or column always means that line.

### How long do raised lines stay up?

Until you lower them, even when you change tools. For a raised line's average, minimum,
maximum or sum, ask the assistant.

## The assistant

### It turned itself off.

A notice says why: the microphone is not allowed, it lost its connection, or the
conversation grew too long for it to hold. Poke Assistant to start a fresh conversation.

## Troubleshooting

### The app says "No Map" and nothing loads.

The database could not be reached. Check the headset's Wi-Fi and that
`Assets/StreamingAssets/cloud.key` held the right key when the app was built.

### A "Sheet Unavailable" notice appeared.

That one sheet could not be read, usually a dropped connection. The current sheet stays
up; try again in a moment.

### The loading line never finishes.

It gives up waiting after 45 seconds and opens with whatever loaded, so a slow connection
means some sheets may be missing until the app is restarted.

### The wrist buttons are missing.

They show only while your left hand is tracked. Hold it up in view of the headset.

### Everything went dim or grey.

That is usually the Quest itself: its menu or a notification is open, or you are close to
your play-area boundary. Close the menu or step back toward the middle of your space.

### A panel disappears when I lean very close.

Anything within about 2 cm of your eyes is not drawn. Lean back a little.

## Cost and privacy

### Does it cost anything?

The Gemini API does, for whatever the assistant is used. The breach database (Cloud
Storage, BigQuery, Cloud Run and API Gateway, plus the Cloud Build pipeline that tests and
deploys it) is small enough to sit within Google Cloud's free tiers, and the project has a
monthly budget alert in case it ever does not.

### Does the app store anything about me?

No. It reads breach data, and sends your speech to the assistant only while the assistant
is on.
