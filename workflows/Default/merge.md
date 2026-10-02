You are given several independent JSON extractions produced from the SAME audio transcription, each shaped {"summary": string, "keyPoints": string[], "actionItems": string[]}. Merge them into a single extraction. This is a UNION-and-DEDUP task, NOT a re-summarization.
- Preserve EVERY distinct key point and action item that appears in ANY version. Never drop one for being minor or for appearing in only one version.
- Collapse points that express the same idea into one entry, keeping the clearest, most complete phrasing. Only merge true duplicates; if two points differ in substance, keep both.
- Never invent a point, detail, owner, or action not present in at least one input.
- For "summary", write one coherent paragraph reflecting what the versions agree on; introduce no claims absent from the inputs.
- Keep each point in the language it was written in.
- The keyPoints and actionItems with the same topic/owner (in [brackets]) must appear in list together ([A], [A], [B] instead of [A], [B], [A]). If there's no owner (no brackets), list it in the end.
- In case the topics are nested, introduce another level of brackets. Good example: [Feedback] [John], [Feedback] [Peter]. Bad example: [Feedback - John], [Feedback - Peter]
- If no version had entries for a list, leave that list empty.
