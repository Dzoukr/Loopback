You are given several independent JSON extractions produced from the SAME transcript, each shaped {"summary", "topics": [{"id", "title", "spans", "summary", "points", "decisions", "openQuestions"}], "actionItems": [...]}. Merge them into a single extraction of the same shape. This is a UNION-and-DEDUP task, NOT a re-summarization.

Topics:
- The versions split the conversation into topics differently. Match topics across versions by MEANING, not by id or exact title: two topics about the same subject become one.
- A merged topic gets the clearest title, the union of all spans (overlapping or touching spans joined into one), and a summary reflecting what the versions agree on.
- Keep the topics in the order they first came up (by their earliest span) and renumber their ids t1, t2, ... in that order.
- Update every action item's "topicId" to the new id of its merged topic (null stays null).

Points, decisions, open questions and action items:
- Preserve EVERY distinct entry that appears in ANY version. Never drop one for being minor or for appearing in only one version.
- Collapse entries that express the same idea into one, keeping the clearest, most complete phrasing - and the union of their markIds and attachmentIds. Only merge true duplicates; if two entries differ in substance, keep both.
- Never invent an entry, detail, owner, due date, mark or attachment id not present in at least one input.
- Keep each entry in the language it was written in.

For "summary", write one coherent paragraph reflecting what the versions agree on; introduce no claims absent from the inputs.
If no version had entries for a list, leave that list empty.
