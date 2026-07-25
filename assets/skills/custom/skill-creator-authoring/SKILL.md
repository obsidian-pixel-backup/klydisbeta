---
name: skill-creator-authoring
description: Framework and guidelines for authoring new agent skills — frontmatter schema (name, description, category, author, version), Markdown structure, trigger-word design, directive clarity, and quality checklists. Use whenever the user or model creates a new skill from scratch or designs a new domain knowledge module.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Skill Creator & Authoring Guide

Skills are modular, executable domain directives that equip an AI agent with specialized engineering standards, workflows, decision matrices, and checklists.

## Frontmatter Schema

Every skill MUST start with a clean YAML frontmatter block enclosed by `---`:

```yaml
---
name: kebab-case-skill-name
description: A concise 1-3 sentence summary of what the skill provides AND explicit trigger conditions (when to activate this skill).
category: Development & Architecture
author: Author Name / Klydis Custom
version: 1.0.0
---
```

### Mandatory Frontmatter Fields:
- **`name`**: Unique kebab-case identifier (e.g. `database-schema-design`, `code-review`).
- **`description`**: Clear summary including **trigger conditions** ("Use whenever the user..."). This is what the dynamic skill selector uses for relevance matching.
- **`category`**: One of the standard categories (`Development & Architecture`, `AI & ML Infrastructure`, `Robotics & Edge AI`, `Accelerated Computing & HPC`, `Creative & Design`, `Productivity & Collaboration`, `Writing & Research`, `Analysis & Workflows`, `General`).
- **`author`**: Creator identifier.
- **`version`**: Semantic version (default `1.0.0`).

## Skill Content Structure

Structure the markdown content logically to optimize AI execution:

1. **Title & High-Level Philosophy (`# Skill Title`)**: State the core objective and why this domain discipline matters.
2. **Core Directives & Principles (`## Section`)**: Clear rules, rules-of-thumb, and decision trees. Avoid fluff; use bullet points and bold emphasis.
3. **Good vs Bad Examples / Input vs Output**: Provide concrete code or structural comparisons (`Input (to avoid): ...` vs `Output (use instead): ...`).
4. **Tables & Specifications**: Use markdown tables for taxomony, status codes, or options comparison.
5. **Executable Checklist (`## Checklist`)**: Include a non-ambiguous checklist of checkbox items (`- [ ] ...`) for verifying work completion.

## Best Practices for Authoring Skills

- **Actionable & Direct**: Write in an imperative, authoritative tone ("Use X", "Avoid Y", "Always parameterize").
- **Self-Contained**: Do not depend on unstated external context; include explicit code patterns where necessary.
- **No Token Waste**: Keep explanation tight. Prefer structured markdown elements (tables, code blocks, checklists) over long prose paragraphs.
- **Unique Triggers**: Ensure `description` includes distinct domain keywords so `DynamicSkillSelector` routes user prompts accurately.

## Authoring Checklist

- [ ] Valid YAML frontmatter starting on line 1 with `---`
- [ ] `name` is unique and formatted in `kebab-case`
- [ ] `description` clearly specifies both scope AND trigger conditions
- [ ] Markdown body includes core principles, input/output examples, and a checklist
- [ ] No unparsed raw HTML tags or unescaped frontmatter errors
