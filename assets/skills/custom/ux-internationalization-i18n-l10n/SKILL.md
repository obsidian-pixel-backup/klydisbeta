---
name: ux-internationalization-i18n-l10n
description: Architecting internationalization (i18n) and localization (l10n): RTL layout mirroring, text expansion buffers, dynamic date/currency formatting, and locale switching.
category: UX & User Experience
author: Klydis Team
version: 2.0.0
---

# UX Internationalization (i18n) & Localization (l10n)

Internationalization ensures software can be translated and adapted seamlessly for global users across different languages, cultures, and Right-to-Left (RTL) layouts.

## Core i18n Considerations

1. **RTL (Right-To-Left) Mirroring**: For Arabic, Hebrew, and Persian languages, mirror page layouts, navigation icons, and form alignment using CSS logical properties.
2. **Text Expansion Buffers**: German and Scandinavian translations require up to $40\%$ more horizontal space than English text.
3. **Locale-Aware Formatting**: Format dates, times, numbers, and currencies using native `Intl` browser APIs.

---

## CSS Logical Properties Blueprint (RTL Support)

Avoid physical side properties (`margin-left`, `left`) in favor of logical properties:

```css
/* BAD: Fails in Right-To-Left layout */
.card-icon {
  margin-right: 16px;
  float: left;
}

/* GOOD: Automatically mirrors in RTL locales */
.card-icon {
  margin-inline-end: 16px;
}
```

---

## Native Browser Formatting Blueprint

```typescript
// Format Currency in Euro for German Locale
const amount = 1250.50;
const formattedCurrency = new Intl.NumberFormat('de-DE', {
  style: 'currency',
  currency: 'EUR'
}).format(amount); // Output: "1.250,50 €"

// Format Date for US Locale
const formattedDate = new Intl.DateTimeFormat('en-US', {
  dateStyle: 'medium'
}).format(new Date()); // Output: "Jul 26, 2026"
```

---

## Verification Checklist

- [ ] CSS layout uses logical properties (`margin-inline-start`, `padding-inline-end`).
- [ ] Hardcoded string literals are replaced by i18n translation keys (`t('dashboard.welcome')`).
- [ ] UI containers accommodate extended text string lengths without clipping.
