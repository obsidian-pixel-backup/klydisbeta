---
name: ui-color-theory-dark-mode
description: Applying color theory, OKLCH/HSL dynamic color generation, dark mode palette architecture, accessible contrast ratios, and brand palette harmony.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Color Theory & Dark Mode Architecture

Crafting premium color palettes requires understanding color harmony, perceptual lightness (OKLCH color space), dynamic dark mode mapping, and strict contrast accessibility.

## Color Palette Composition Rule (60-30-10 Rule)

- **60% Dominant Neutral**: Backgrounds, page canvas, muted surfaces (e.g., dark slate/gray).
- **30% Secondary Neutral**: Cards, modals, sidebars, borders, typography.
- **10% Accent Accent Color**: Primary buttons, active states, key data metrics, focal triggers.

---

## Modern OKLCH Dynamic Theme Blueprint

```css
:root {
  /* Light Theme OKLCH Tokens */
  --color-bg: oklch(98% 0.01 240);
  --color-card: oklch(100% 0 0);
  --color-text: oklch(20% 0.02 240);
  --color-primary: oklch(60% 0.22 260); /* Vibrant Indigo */
}

[data-theme="dark"] {
  /* Dark Theme OKLCH Tokens */
  --color-bg: oklch(14% 0.02 240);
  --color-card: oklch(19% 0.02 240);
  --color-text: oklch(96% 0.01 240);
  --color-primary: oklch(65% 0.22 260);
}
```

---

## Contrast Requirements (WCAG 2.2)

- **Normal Text**: $\ge 4.5:1$ contrast ratio against background.
- **Large Text ($\ge 24\text{px}$)**: $\ge 3.0:1$ contrast ratio.
- **UI Components & Icons**: $\ge 3.0:1$ contrast ratio.

---

## Verification Checklist

- [ ] Text elements meet minimum 4.5:1 contrast ratio against card backgrounds.
- [ ] Dark mode uses dark grays/slates (`oklch(14% ...)`) rather than harsh pure black (`#000000`).
- [ ] Accent color accounts for no more than ~10% of screen real estate.
