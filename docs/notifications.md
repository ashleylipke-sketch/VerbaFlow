# Notifications

VerbaFlow tells people when something needs them.

## The bell
A bell sits at the top of every page, with a red number for alerts not yet read. Click it to see the latest; click an alert to open the recording (it is then marked read). **Mark all as read** clears the number. **See all, and choose what comes by email** opens the Notifications page (`#/notifications`).

## Who is told what
Nobody is told about something they did themselves.
- **Your work**: a recording assigned to you; your recording returned to you, approved, reopened (you asked) or deleted (also by the retention rules); your reopen or delete request turned down.
- **Ready**: your recording has been turned into text, could not be converted, or a note-taker could not record your online meeting.
- **Due dates**: due tomorrow, due today and overdue, told once each to whoever has the recording now (the assignee once assigned, otherwise the owner). Checked every 10 minutes.
- **Approvals** (administrators only): a reopen or delete request, or a retention change, needs another administrator to agree.

## Email
Every alert can also come by email, to the address VerbaFlow knows for you (from Microsoft 365 when you sign in with Microsoft). On the Notifications page, untick a group to stop its emails; it still shows in the bell.

Real email needs Azure Communication Services: `Email:ConnectionString` and `Email:Sender` (see `azure-email-setup.md`). Without them, emails are written as files to `data/outbox` only.

The link in an email uses `Notifications:SiteAddress` (default `http://localhost:5044`). Set it to the online address once VerbaFlow is online.

## For technical staff
Alerts are worked out from the History (audit trail) by a background check every 15 seconds (`Notifications:Worker=false` stops it). On its first run it starts at the end of the History, so nothing earlier is announced. Each alert has a fixed id, so the same event never alerts twice. 200 alerts are kept per person. Changing the email choices is audited as `notifications.settings_changed`.

Not yet tried: real Azure email, real Microsoft users' addresses.
