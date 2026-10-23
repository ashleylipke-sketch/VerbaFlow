# Meet: the note-taker for online meetings

Meet records online meetings on **Zoom, Google Meet, Microsoft Teams and Webex**. You paste the meeting's join link
on the **Meet** page (now, or booked for when the meeting starts). A participant called **VerbaFlow Notetaker** joins,
records the sound and the video, and posts a message in the meeting chat saying it is recording. When the meeting
ends, the video comes back into VerbaFlow as a Meeting item: transcript, speakers, summary, minutes, approval and
export work as for any meeting, and the video plays on the item page in step with the transcript.

- **Nobody has to press record** in the meeting, and the meeting does not need recording switched on.
- **Meetings set up by other organisations work too.** The note-taker joins from the link like any guest. If the
  meeting has a lobby or "Ask to join", the host has to let it in. On some platforms the host may also be asked to
  allow it to record; if the host refuses, nothing is recorded and Meet says so.
- **One note-taker per meeting.** If a colleague has already booked the note-taker for the same meeting, the next
  person is added to that booking instead of a second note-taker joining. Links that differ only in a passcode or a
  tracking part still count as the same meeting. A weekly meeting that keeps its link is a new meeting each week
  (bookings more than 3 hours apart are different meetings). The first person to book owns the meeting item; the
  others see it in their Meeting list, can read it, and can export it once it is approved. If the first person calls
  it off, the next person becomes the owner; the note-taker is only called off when nobody needs it any more.
- **Speakers get people's names.** The meeting platform tells the note-taker who was speaking when, and those names
  are given to the transcript. People can still be renamed on the item page.

## How it works

The note-taker is provided by **Recall.ai**, a service that runs meeting bots for all four platforms. VerbaFlow asks
Recall.ai every minute how each note-taker is getting on, so nothing needs to reach your computer from outside: it
works on your own computer as well as online. If your computer is off when a meeting ends, the recording is brought
back the next time VerbaFlow runs (Recall.ai keeps it for about 7 days). Once VerbaFlow has its own copy, it asks
Recall.ai to delete theirs.

**Things to know before using it for real**
- The meeting's video and sound pass through Recall.ai (a company in the United States) until VerbaFlow has fetched
  them. Pick the region closest to you when you sign up (Frankfurt, `eu-central-1`, is the closest to South Africa and
  the UK). Your organisation's legal and security people should be happy with this (decision record, item 11).
- Price, as published in October 2026: about **$0.50 per hour of meeting recorded**, charged by the second, with the
  first 5 hours free. Check recall.ai for the current price.
- Booking ahead is better than sending it at the last minute: Recall.ai asks for bookings at least 10 minutes ahead,
  and a note-taker sent at the last minute can occasionally be refused when they are busy.

## Setting it up (once)

Until this is done, Meet uses a **stand-in**: nobody joins any meeting, and a short sample recording comes back
instead, so you can try everything else.

1. Go to **recall.ai** and sign up. Choose the region (`eu-central-1` recommended).
2. In the Recall.ai dashboard, create an **API key**. Copy it. Do not paste it into a chat or an email.
3. On your computer, open **Command Prompt** in the VerbaFlow folder and type (pasting the key yourself where it says so):

   ```
   cd src\VerbaFlow.Api
   dotnet user-secrets set "Meet:Region" "eu-central-1"
   dotnet user-secrets set "Meet:ApiKey" "PASTE-THE-KEY-HERE"
   cd ..\..
   ```

   Use the region you chose in step 1 (one of `us-west-2`, `us-east-1`, `eu-central-1`, `ap-northeast-1`).
4. Start VerbaFlow again with `run.cmd`. The window should say
   `Meet: the note-taker joins meetings through Recall.ai (eu-central-1) as "VerbaFlow Notetaker"`.

Optional: `Meet:NoteTakerName` changes the name the note-taker shows in meetings (for example your company's name).

## Settings

| Setting | What it is |
|---|---|
| `Meet:ApiKey` | Recall.ai API key (secret: user-secrets on your computer, a Container App secret online) |
| `Meet:Region` | Recall.ai region: `us-west-2`, `us-east-1`, `eu-central-1` or `ap-northeast-1` |
| `Meet:NoteTakerName` | Name shown in the meeting. Default `VerbaFlow Notetaker` |
| `Meet:Worker` | `false` stops the every-minute check (for tests) |

## History

- `meet.booked`, `meet.joined_booking`, `meet.left_booking`, `meet.cancelled`, `meet.booking_failed` (product `meet`):
  platform, time, languages. The meeting link is not recorded (it can hold a passcode).
- `meet.state_changed`: joining, waiting to be let in, recording, meeting ended, not recorded (with a plain reason).
- On the item: `item.created` (source "Online meeting (note-taker)", platform, owner, number of colleagues, file
  fingerprint), `meet.recording_fetched`, and on `processing.completed` `speakerSeparation: meeting platform` and
  `speakersNamedByPlatform`.

## What has been tested

Tested with the stand-in only: booking, one note-taker for colleagues booking the same meeting, calling off, the
recording coming back as an item, speaker names from the platform, deleting, and the video streaming to the item page.
How Recall.ai's answers are read was checked against the examples in Recall.ai's documentation.

**Not yet tried with Recall.ai or in a real meeting** on any platform: whether the note-taker gets in, the chat
message, recording permission on Zoom, the speaker names, the video quality, and the video playing in Edge or Chrome
(the test browser here cannot play MP4 video, so only the streaming itself was checked).

## Not built yet

- Booking from the Outlook calendar automatically (every meeting with a Teams, Zoom, Meet or Webex link).
- Separate sound per person (Recall.ai can provide it; it would help speaker separation further).
- Platforms other than the four above (Recall.ai has GoTo Meeting and Slack huddles in beta). For any other platform,
  record the meeting there and use **Import** on the Meeting page.
