# OpenCode MiniMax M3: lessons from delegation setup

This file is automatically prepended by
`BIM_Coordinator_Assistant/tools/Invoke-OpenCodeMiniMax.ps1` to every MiniMax
task. It records observed behavior of the locally available
`opencode-go/minimax-m3` model behind `opencode-cli.exe`, not a blanket
judgment of the model.

## What worked

- `opencode-cli.exe` at `%LOCALAPPDATA%\Programs\@opencodedesktop\resources\opencode-cli.exe`
  (not on PATH) starts a fresh session with `run --model opencode-go/minimax-m3`.
- `--format json` produces newline-delimited events with assistant `text` parts.
- `--file <path>` attaches a file to the message so the wrapper can pass
  context paths instead of inline secrets.
- A focused, single-purpose message produces short, parseable replies.

## Errors to guard against

- **Shell syntax.** MiniMax repeatedly used `&&` and `ls -la` in Windows
  PowerShell 5.1. Check every command before running it.
- **Unsupported `SchemaValidator` bounds.** MiniMax occasionally proposes JSON
  Schema bounds such as `minItems`/`maxItems`/`minimum`/`maximum` as if the
  rvt-mcp `SchemaValidator` enforced them. It does not. Enforce these bounds
  in the custom handler and test that boundary.
- **False `parameterId > 0` assumption.** MiniMax has proposed guards such as
  `parameterId > 0`. Built-in Revit parameter IDs can be negative; that guard
  breaks disambiguation. Use `RevitCompat.GetId()` and check only the Revit
  version's representable range.
- **Document wrappers.** `ReferenceEquals` rejected a selection from the same
  open Revit document on the next MCP call. Use Revit `Document.Equals` and
  live-test collection followed by filtering, including stable document tokens.
- **Live Revit verification.** A passing .NET test is not evidence of live
  Revit behavior. A delegated code review does not prove runtime behavior.
  Any fact about a real Revit process, document identity, selection, parameter
  value, or filter result must be confirmed by an MCP tool result on this
  machine before it is treated as confirmed. Until then, status is
  `Hypothesis`.

## Delegation contract

Tool-specific behavior belongs in the tool's schema, concise description,
result contract, and focused tests. Do not add hardcoded instructions for
individual tools to a shared system prompt or prepend them to every model
request. Keep only universal rules in the shared prompt; enforce safety and
result bounds in code rather than relying on prompt wording.

1. Read the relevant `AGENTS.md`, the tool document, exact method signatures,
   and adjacent tests before proposing code. Quote file paths and line numbers;
   do not invent API members or result fields.
2. Return a narrow unified diff or a focused review with file/line evidence.
   Do not present an incomplete patch as a complete solution. Do not infer test
   outcomes you have not executed.
3. Keep full element-ID collections inside Revit. Return bounded counts, opaque
   selection handles, and at most one small page to the model; save full JSON
   only to a local task report.
4. The primary agent reviews changes, compiles the target Revit version,
   runs automated tests, and checks a fresh Revit process. A passing .NET test
   is not evidence of live Revit behavior.
5. Never include credentials, full RVT contents, private report files, or long
   element-ID lists in a MiniMax prompt. Review prompt and attached files
   before invocation; the wrapper enforces a size limit, not secret detection.
6. Limit each task to a single bounded unit of work. Do not chain tool
   execution across multiple repos or run `git add`/`commit`/`push`/tags/PRs
   on the model's behalf.

## Verification at setup

- `opencode-cli.exe --version` returned `opencode v2.0.19`; `models` lists
  `opencode-go/minimax-m3`.
- `run --format json --model opencode-go/minimax-m3 --title <t> --file <p> <msg>`
  produced a JSON stream with one `text` part per smoke test.
- MiniMax's first review added the invalid `parameterId > 0` guard. A later
  Revit 2022 call exposed the document-wrapper bug; the primary agent verified
  both findings against code and live tool results.
