---
name: ui-design-system-tokens
description: Architecting design system tokens: color scales, spacing scales, typography hierarchy, elevation/shadows, W3C token format, and multi-theme management.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Design System Tokens

Design tokens are the single source of truth for visual style variables across platforms (Web, iOS, Android, Figma).

## Token Classification Taxonomy

1. **Global Tokens**: Raw design values (`color-blue-500: #3b82f6`, `space-16: 16px`).
2. **Alias / Semantic Tokens**: Purpose-bound tokens referencing global tokens (`color-brand-primary: var(--color-blue-500)`).
3. **Component Tokens**: Component-specific overrides (`button-bg-primary: var(--color-brand-primary)`).

---

## Design Tokens Specification Blueprint (`tokens.json`)

```json
{
  "color": {
    "brand": {
      "primary": { "$value": "#6366f1", "$type": "color" },
      "secondary": { "$value": "#a855f7", "$type": "color" }
    },
    "surface": {
      "background": { "$value": "#0f172a", "$type": "color" },
      "card": { "$value": "#1e293b", "$type": "color" }
    }
  },
  "spacing": {
    "xs": { "$value": "4px", "$type": "dimension" },
    "sm": { "$value": "8px", "$type": "dimension" },
    "md": { "$value": "16px", "$type": "dimension" },
    "lg": { "$value": "24px", "$type": "dimension" }
  }
}
```

---

## Verification Checklist

- [ ] UI components reference semantic variables (`var(--color-bg-card)`) instead of hardcoded hex values (`#1e293b`).
- [ ] Theme switching toggles CSS root classes cleanly (`.dark`, `.light`).
- [ ] Token names follow a standardized naming convention (e.g., `category-property-variant`).
