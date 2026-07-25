---
name: skill-editor-maintenance
description: Protocols for safely editing, updating, refactoring, and versioning existing agent skills — preserving YAML frontmatter integrity, updating skill versions, merging overlapping directives, and auditing skill updates. Use whenever modifying existing skill markdown files, updating skill rules, or refactoring domain knowledge modules.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Skill Editor & Maintenance Guide

Modifying an existing skill requires maintaining strict backwards compatibility for prompt routing while keeping domain directives up to date with modern standards.

## Editing Protocols

### 1. Frontmatter Preservation
When editing a skill markdown file, NEVER break or corrupt the frontmatter header:
- Keep the opening `---` on line 1 without leading blank lines or UTF-8 BOM characters.
- If updating skill behavior significantly, bump `version` in frontmatter (`1.0.0` ➔ `1.1.0` for feature additions, `2.0.0` for breaking structural rewrites).
- Update the `description` field if new triggers or domain topics were added so the dynamic selector captures new query types.

### 2. Preserving Existing Directives
- **Audit Before Replacing**: Read the complete existing skill file before modifying it. Do not delete established best practices unless they are deprecated or inaccurate.
- **Append & Integrate**: Integrate new patterns seamlessly under relevant section headers rather than appending random notes at the end of the file.

### 3. Updating Code Examples & Standards
- Keep syntax and code snippets aligned with current language versions (e.g. C# 12 / .NET 9+, Python 3.12+, TypeScript 5+, React 19+).
- Ensure all example code blocks specify language syntax identifiers (````csharp`, ````typescript`, ````json`).

## Refactoring Overlapping Skills

If two skills contain duplicate directives:
1. Identify the primary domain skill (e.g. `unit-testing-tdd` vs `testing-strategy`).
2. Consolidate specific operational rules into the primary skill.
3. Cross-reference related skills using explicit markdown file links (`see the [security-owasp-top-10](file:///...) skill`).

## Maintenance Checklist

- [ ] Frontmatter `---` header intact and valid
- [ ] `version` field updated if changes are material
- [ ] `description` reflects any new trigger keywords or domain additions
- [ ] Code examples verified for syntax correctness and modern standards
- [ ] Existing valid directives preserved (no accidental deletion of knowledge)
