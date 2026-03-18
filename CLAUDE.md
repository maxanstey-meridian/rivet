# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## TypeScript

- Strict mode always. No `any` or `unknown` to silence errors.
- `const` arrow functions, not `function` declarations.
- Fix the type or the model, not the error. No `as any`, no non-null assertions as convenience.
- Prefer narrowing and better modelling over optional chaining everywhere.

## Nuxt / Vue

- `<script setup lang="ts">` always.
- `useState()` with namespace keys for SSR-safe shared state. No Pinia.
- Components are presentational; side effects and orchestration live in pages or composables.
- @nuxt/ui v4 for component library.

## .NET / C#

- `sealed` on all concrete types.
- Records for DTOs, commands, results, value objects.
- Primary constructor DI for use cases.
- Use case signature: `ExecuteAsync(Command, CancellationToken)` — always async, always cancellation token.
- EF Core entities: `sealed class` (not records), `init`/`set`. Fluent config in `*EntityConfiguration.cs`.
- Module registration via `IServiceCollection` extension methods. No auto-scanning.
- FluentValidation with `.WithErrorCode()`. Custom `ValidationActionFilter`.
- Cookie-based auth (HttpOnly `sid`/`rtid`). JWT extracted from cookie.
- `JsonStringEnumConverter` globally.
- Always use braces on control flow bodies — even single-line.
- Never fetch an entity just to check existence — use `ExistsAsync`.
- Colocate `Command` and `Result` records with their use case class.

## Code navigation

Prefer LSP over Grep/Glob/Read:
- `goToDefinition` / `goToImplementation` to jump to source
- `findReferences` to see all usages
- `workspaceSymbol` to find definitions
- `hover` for type info

After writing or editing code, check LSP diagnostics. Fix type errors and missing imports immediately.

## Context

`CONTEXT.md` is your working mental model of this project — post-it notes on a wall. It captures the "why", the gotchas, the decisions, and the cross-cutting knowledge that isn't obvious from reading code or git log.

- **Update it throughout the session** — don't wait until the end. If you learn something non-obvious, write it down.
- **Update in-place** — keep it tight. Don't just append; reorganise, merge, and prune stale entries.
- **Tell me when you update it** — mention "updated CONTEXT.md" in your response so I know.
- **Non-obvious only** — things you can't derive from reading the current code, CLAUDE.md, or git history. No duplication.
- **Remove resolved items** — if future work gets implemented or a gotcha gets fixed, clean it up.

## How to work with me

- **Default mode is rubber-ducking.** No code changes unless I give an imperative instruction.
- Treat me as senior. Conclusions first, then reasoning. No preamble.
- Challenge my assumptions with evidence.
- When I ask "is this clean?" — evaluate boundaries, type story, hidden dependencies.

## Debugging and refactoring

- **No shims.** Root causes, not symptoms.
- **Zoom out.** Is this a local fix or a broader design issue?
- **Respect invariants.** Do not weaken domain invariants to make something compile.

## Hard rules

1. **Principle-first answers.** Lead with one unqualified recommendation from first principles, not "for this codebase today".
2. **95% confidence threshold.** If not ~95% confident, say so and explain what would firm it up.
3. **No invented commands.** Infer tooling from project files.
4. **Proven solutions before hand-rolling.** Before implementing infrastructure concerns (auth, email, file storage, job scheduling, etc.), evaluate whether a battle-tested library or framework already handles it. The same principle as "every package must earn its place" applies in reverse: every hand-rolled solution must justify itself over an existing, audited alternative. Flag the option and the trade-offs before writing custom code.
