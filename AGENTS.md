# AI Challenge development rules

This file applies to the entire `AI_Challenge` repository. General machine and
filesystem safety rules from `C:\Users\Mankaev_r\.codex\AGENTS.md` still apply.
The global rules for ordinary Revit plugins, pyRevit commands, and Revit scripts
do not apply here.

## Git boundary

- Do not run `git add`, `git commit`, `git push`, create tags, releases, or pull
  requests unless the user explicitly requests that exact operation.
- The user owns change staging and history. After work, report changed files,
  tests, and `git status`.
- Never discard, overwrite, or reformat unrelated user changes.

## Tool routing

- Route common unambiguous Revit intents locally without an extra LLM call.
- Requests about selected, highlighted, or marked elements must first call
  `revit_get_selected_elements`; never ask the user to transcribe ElementIds.
- Requests about elements on a view must call
  `revit_custom_summarize_elements`, not `revit_get_current_view_info`.
- If the required tool is absent or fails, state that precisely. Never infer
  model contents from another tool or let the model invent an answer.
- Read-only preparation runs without confirmation. Ask for confirmation only
  immediately before a concrete model-changing call with resolved arguments.

## Multi-step calls

- Keep the periodic scheduler task-agnostic. Do not add task-specific flags,
  tool-result validators, or hardcoded workflows for an individual scheduled
  task. Put its procedure and required reporting in its agent prompt; base
  reported facts on actual tool responses. Change a tool's result contract
  when the needed facts are absent.

- Support bounded chains such as read context -> obtain IDs -> confirm -> write
  -> report the actual result.
- Pass real outputs from one tool into the next. Do not substitute empty arrays,
  zero IDs, or guessed values.
- Do not disable tool calling after the first result when the original request
  still requires another step.
- Stop a chain on error, cancellation, missing data, or its call limit.
- Add an end-to-end flow test for every new multi-tool workflow.

## Tool catalogue

- Give every enabled tool a manually reviewed Russian title, purpose, scope,
  mutation classification, and natural example.
- Do not translate tool names by mechanically replacing name fragments.
- For an unknown future tool, show its technical name and original MCP
  description instead of inventing a translation or capability.
- A new MCP tool is not integrated until it is present in the server toolset,
  launch arguments, assistant allowlist/classification, capability catalogue,
  ToolRouter tests, and the running client's `tools/list`.

## Model responses

- Answer about Revit only from the result of an appropriate tool.
- Report the concrete meaning of a tool error; do not replace it with a generic
  connection guess.
- Never claim a mutation succeeded before the tool reports success.
- Report empty selection and cancelled confirmation explicitly.

## Verification

- Add varied natural-language cases for routing changes, including negative and
  multi-step scenarios.
- Run the full `DesignerAssistant.Tests` suite after ToolRouter or tool-flow
  changes.
- Rebuild and restart `DesignerAssistant.Web`, then verify its listening port.
- Launch the web app with the project directory as its working directory so
  `wwwroot` resolves correctly.
- Diagnose in this order: ToolRouter trace, invoked tool and arguments,
  `%LOCALAPPDATA%\RvtMcp\mcp-calls.jsonl`, Revit handler result, model response.
