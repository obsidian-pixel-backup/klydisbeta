---
name: css-architecture-tailwind-modules
description: Architecting scalable CSS systems: CSS variables design tokens, Tailwind CSS best practices, CSS Modules, BEM methodology, and cascade management.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# CSS Architecture: Tailwind, CSS Modules, & Variables

Scalable CSS architecture maintains visual consistency across team members while preventing selector specificity clashes and stylesheet bloat.

## CSS Architecture Options

1. **Tailwind CSS**: Utility-first CSS providing low-level utility classes directly in markup. High development speed and minimal output bundle.
2. **CSS Modules**: Scoped CSS styles local to individual components, avoiding global namespace pollution.
3. **Vanilla CSS + Variables**: Standard CSS utilizing modern CSS Custom Properties for design system tokens.

---

## Design System Tokens Blueprint (`globals.css`)

```css
:root {
  --color-bg-primary: #0f172a;
  --color-text-main: #f8fafc;
  --color-accent-blue: #38bdf8;
  --radius-card: 12px;
  --shadow-glow: 0 0 20px rgba(56, 189, 248, 0.15);
}

.dark {
  --color-bg-primary: #020617;
  --color-text-main: #ffffff;
}

/* Base style resets */
body {
  background-color: var(--color-bg-primary);
  color: var(--color-text-main);
  font-family: system-ui, -apple-system, sans-serif;
}
```

---

## Tailwind Clean Utility Pattern (`cn` Helper)

Use `clsx` + `tailwind-merge` to combine dynamic conditional classes without specificity issues:

```typescript
import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

// Component Usage
function Button({ variant = 'primary', className }: ButtonProps) {
  return (
    <button
      className={cn(
        "px-4 py-2 rounded-lg font-medium transition-colors",
        variant === 'primary' && "bg-blue-600 hover:bg-blue-700 text-white",
        variant === 'secondary' && "bg-slate-800 hover:bg-slate-700 text-slate-200",
        className
      )}
    />
  );
}
```

---

## Verification Checklist

- [ ] Global styling logic relies on CSS variables for theme switching.
- [ ] Component utility classes use `cn` helper for conflict-free merging.
- [ ] Unused CSS classes are purged automatically in production builds.
- [ ] Direct inline styles (`style={{ ... }}`) are avoided in favor of CSS utility classes.
