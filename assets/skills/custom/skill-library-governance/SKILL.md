---
name: skill-library-governance
description: Governance standards for managing and scaling the Skill Library — deduplication rules, folder layout organization, performance auditing, category taxonomy, and maintaining library health across custom, NVIDIA, and Awesome-LLM skill sources. Use when auditing the skill library, organizing skill folders, managing custom skills, or resolving duplicate skills.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Skill Library Governance & Organization

A large skill library (400+ skills) requires clean directory structures, strict deduplication, and continuous health auditing to maintain fast scan times and accurate dynamic selection.

## Directory Layout Standards

Maintain skills under `assets/skills/` grouped by source repository:

```
assets/skills/
  ├── awesome-llm-skills/   # Built-in community skills
  ├── nvidia-skills/        # Domain GPU/AI/Edge skills
  └── custom/               # Custom user and team skills
      ├── database-schema-design/
      │   └── SKILL.md
      ├── code-review/
      │   └── SKILL.md
      └── ...
```

- Each custom skill MUST live in its own directory (`assets/skills/custom/<skill-id>/SKILL.md`) or as a standalone file (`assets/skills/custom/<skill-id>.md`).
- Primary skill definition files MUST be named `SKILL.md` or `skill.md` when stored inside skill directories.

## Deduplication & Conflict Resolution Rules

- **Unique Skill IDs**: Every skill across all repositories MUST have a unique `Id` (lowercased kebab-case).
- **Precedence Order for Duplicate IDs**:
  1. `Custom` (Highest precedence — user overrides built-ins)
  2. `Awesome-LLM-Skills`
  3. `NVIDIA-Skills`
- If a custom skill is saved with an ID matching a built-in skill, `SkillLibraryManager` respects the custom definition.

## Health Auditing & Cleanup

Periodically audit the Skill Library for:
1. **Broken Frontmatter**: Skill files missing YAML `---` blocks or having invalid key formats.
2. **Orphan Metadata**: Files in `templates/`, `docs/`, or `examples/` that were incorrectly scanned as skills.
3. **Empty / Stub Skills**: Skills with fewer than 100 characters or missing prompt instructions.
4. **State Persistence**: `skill_states.json` correctly persisting user enabled/disabled toggles.

## Governance Checklist

- [ ] Every skill directory contains a valid `SKILL.md` or `skill.md` file
- [ ] Custom skills take precedence over built-in repository skills with matching IDs
- [ ] Unrelated repository files (`README.md`, `CHANGELOG.md`, `LICENSE`) excluded from scanner
- [ ] Library scan execution time completes in under 100ms
- [ ] User enabled/disabled states saved cleanly in `skill_states.json`
