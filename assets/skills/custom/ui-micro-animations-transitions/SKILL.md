---
name: ui-micro-animations-transitions
description: Designing subtle UI micro-animations: CSS keyframes, spring physics transitions, hover/focus state feedback, dynamic active indicators, and reduced-motion.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Micro-Animations & Micro-Interactions

Micro-animations bring interfaces to life by providing visual feedback for user interactions (button clicks, hover states, card expansions, toggles).

## Core Principles

1. **Subtle & Purposeful**: Animations should feel snappy ($150\text{ms}-300\text{ms}$), never delaying user workflow.
2. **Easing Physics**: Use cubic-bezier easing curves (`cubic-bezier(0.16, 1, 0.3, 1)`) for natural fluid motion.
3. **Respect Reduced Motion**: Always disable animations when `prefers-reduced-motion: reduce` is enabled.

---

## Micro-Animation CSS Recipe Blueprint

```css
/* Interactive Card Hover Bounce */
.interactive-card {
  transition: transform 200ms cubic-bezier(0.16, 1, 0.3, 1),
              box-shadow 200ms cubic-bezier(0.16, 1, 0.3, 1),
              border-color 200ms ease;
}

.interactive-card:hover {
  transform: translateY(-4px);
  box-shadow: 0 12px 24px -8px rgba(56, 189, 248, 0.25);
  border-color: rgba(56, 189, 248, 0.4);
}

.interactive-card:active {
  transform: translateY(-1px);
}

/* Accessibility: Honor reduced motion */
@media (prefers-reduced-motion: reduce) {
  .interactive-card {
    transition: none !important;
    transform: none !important;
  }
}
```

---

## Verification Checklist

- [ ] Interaction transition durations stay under 300ms.
- [ ] Animations apply to GPU-accelerated properties (`transform`, `opacity`) rather than `height`/`width`.
- [ ] `@media (prefers-reduced-motion: reduce)` overrides all animations for accessible users.
