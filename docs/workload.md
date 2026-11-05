# Workload overview

**Settings ▾ → Workload** (`#/workload`). Only administrators can open it.

- **Show**: all recordings, or only meetings, dictations or Speech. **Done in the**: last 7 days, 30 days, 3 months or year. Both choices are remembered in the browser.
- **Tiles**:
  - recordings waiting on someone now
  - how many are overdue
  - how many are due today
  - how many were approved in the period
  - the usual time from recording to approval
- **People**:
  - What each person has now:
    - **Own to check**: their own recordings waiting for them to check, assign or approve, including ones that could not be converted.
    - **To accept**: assigned to them, not yet accepted.
    - **Being typed**: accepted, not yet returned.
    - **Overdue**: how many of these are past their due date.
  - What happened in the period:
    - recordings assigned to them, returned by them and approved by them
    - **Time to accept**: from being assigned to accepting.
    - **Time typing**: from accepting to returning.
    - **Time to approve**: from the text being ready, or returned, to approving.
  - Times are medians ("the usual time": half were quicker, half slower).
- **Overdue**: every recording past its due date, who has it and how late it is.
- **Waiting longest**: the ten recordings that have been with the same person longest. The time counts from when the recording last reached them by being made, converted, assigned, accepted or returned. Opening, playing or exporting a recording does not reset it.

Unfinished dictations, approved, deleted and older (reopened) versions are not counted.

## For technical staff
Nothing new is recorded. "Now" comes from the recordings. "In the period" and the times are read from the History (`item.assigned`, `item.opened_by_assignee`, `item.returned_to_owner`, `processing.completed`, `item.approved`) each time the page loads. `GET /api/workload?mode=all|meeting|dictate|speech&days=7|30|90|365`, `WorkloadService`.
