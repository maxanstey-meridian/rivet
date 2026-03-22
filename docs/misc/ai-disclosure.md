# AI Disclosure

Rivet is built with AI assistance. Every commit, from the very first one, has involved Claude Code. This page explains how and why.

## Who builds Rivet

Rivet is built by a [solo developer](https://www.linkedin.com/in/max-anstey-7006921ba/) who works full-time as a technical consultant and contractor in the UK public sector, supporting dozens of codebases across numerous clients in PHP, TypeScript, and .NET. This is not a full-time project. If it were, there would probably be fewer AI-assisted commits — but the constraint is time, not capability.

## How AI is used

Claude Code is used as a development tool throughout Rivet — code generation, refactoring, test scaffolding, documentation. The git history reflects this honestly. There is no attempt to obscure or minimise the AI contribution.

What AI does not do is make design decisions. The architecture, the type mappings, the contract model, the emitter pipeline — these are directed and reviewed by a senior engineer with experience across .NET, TypeScript, and enterprise systems. AI accelerates the implementation of decisions that have already been made.

## Why this works

Rivet has a comprehensive test suite: unit tests, fixture round-trip tests, real-world OpenAPI import tests, and sample projects that must build against the current output. Every change — whether written by hand or with AI assistance — must pass the same gates. The test suite is the quality bar, not the authorship.

The tool is used in production across multiple client codebases. It generates typed clients, runtime validators, and OpenAPI specs that real systems depend on. Bugs surface quickly.

## The honest version

AI tooling lets a solo contractor ship and maintain a tool like this alongside a full-time consulting practice. Without it, Rivet either wouldn't exist or would move at a fraction of the pace. The trade-off is visible in the commit history — and the quality is visible in the test suite and the output.

That's the trade-off, and I'm comfortable with it.
