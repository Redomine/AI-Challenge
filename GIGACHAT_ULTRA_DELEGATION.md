# GigaChat Ultra: lessons from model-analysis tool development

This file is automatically prepended by `BIM_Coordinator_Assistant/tools/Invoke-GigaUltra.ps1` to every future Ultra task. It records observed behavior, not a blanket judgment of the model.

## What worked

- The authenticated `GigaChat-3-Ultra` API was reachable after the proxy was disabled.
- Ultra produced useful ideas for a pure parameter-comparison helper, focused tests, and bounded MCP results.
- Its integration review correctly spotted missing assistant allowlist and capability-catalog entries.

## Errors to guard against

- Several generated C# patches used nonexistent or incompatible APIs and needed human review before compilation.
- The integration review invented result fields (`export_metadata`, `result_id`) absent from the actual MCP contract, and wrongly claimed export returned element data. The real export returns only path, count, size, and hash.
- One proposed catalog patch called helper overloads with parameters they do not accept and described capabilities the implementation did not have.
- Ultra sometimes presented an incomplete patch as a complete solution and inferred test outcomes without executing tests.

## Delegation contract

1. Read the relevant `AGENTS.md`, custom-tool document, exact method signatures, and adjacent tests before proposing code.
2. Return a narrow unified diff or a review with file/line evidence. Do not invent API members, result fields, test results, or Revit runtime observations.
3. Keep full element-ID collections inside Revit. Return bounded counts, opaque selection handles, and at most one small page to the LLM; save full JSON only to a local task report.
4. The human agent applies and reviews changes, compiles both Revit targets, runs automated tests, and checks a fresh Revit process. A passing .NET test is not evidence of live Revit behavior.
5. Never include credentials, full RVT contents, private report files, or long element-ID lists in an Ultra prompt.

## Verification at pause

- Server and Revit 2022/2024 plugins compiled. The new MCP tools appeared in the running server's `tools/list`.
- The first live Revit 2022 collection on the supplied test model returned bounded counts across the 15 default categories with localized Russian category names. Model data is intentionally omitted here.
- Before the final, unverified tool-catalog reduction edit, assistant tests passed 215/215. The rvt-mcp suite passed 503/504; the remaining baked-tool ordering failure predates this work.
- GigaChat Max answered a simple live agent/tool query but twice declined a clear natural-language model-analysis request. The last attempt to narrow its tool catalog has not been built or tested because the user stopped testing.

Do not describe the parameter-analysis workflow as live-verified yet. Collection was verified; filter, summary, page, export, and the final Max routing change remain unverified in Revit.
