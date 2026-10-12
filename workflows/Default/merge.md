You are given several independent JSON extractions produced from the SAME transcript, each shaped {"summary", "topics": [{"id", "title", "points"}], "decisions", "openQuestions", "actionItems": [{"owner", "text", "due"}], "attachmentsAndNotes": [{"id", "topicId"}]}. Merge them into a single extraction of the same shape - the best, most readable one, not the longest.

Topics:
- The versions split the conversation into topics differently. Match topics across versions by MEANING, not by id or exact title: two topics about the same subject become one.
- Keep the order in which the topics first came up, at most 6, and renumber their ids t1, t2, ... in that order.
- Each topic keeps 1-4 points: the clearest, most informative ones from all versions, duplicates collapsed into one.

Decisions, open questions, action items:
- Keep every distinct entry that appears in a version; collapse entries that express the same thing into one, with the clearest, most complete phrasing. Only merge true duplicates.
- Each thing in exactly ONE place, in this order of preference: action item, decision, open question, key point. Drop a decision that only restates an action item, and a point that only restates a decision or an action item.
- A task moved to done is a status, not a decision - it belongs to a point of its topic.
- Never invent an entry, detail, owner or due date not present in at least one input.

attachmentsAndNotes: exactly one entry per id that appears in any version, with the topicId (of the merged, renumbered topics) most versions chose for it, or null.

"summary": 2-3 sentences reflecting what the versions agree on; no claims absent from the inputs.
Keep each entry in the language it was written in. If no version had entries for a list, leave that list empty.
